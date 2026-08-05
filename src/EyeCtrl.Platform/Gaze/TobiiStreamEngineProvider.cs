#if TOBII_AVAILABLE
namespace EyeCtrl.Platform.Gaze;

using System.Threading;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;
using Tobii.StreamEngine;

/// <summary>
/// Gaze provider using the Tobii Stream Engine (Tobii.StreamEngine.Net.dll, 64-bit).
/// Uses callback-based gaze subscription and blocking wait.
/// Reconnects indefinitely on disconnect.
/// </summary>
public class TobiiStreamEngineProvider : IGazeProvider
{
    private readonly double _screenWidth;
    private readonly double _screenHeight;
    private readonly ILogger<TobiiStreamEngineProvider> _logger;

    private IntPtr _apiContext;
    private IntPtr _deviceContext;
    private Thread? _pollThread;
    private volatile bool _running;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private volatile bool _pollThreadExited = true;
    private string? _lastErrorMessage;

    // A managed fault in the pipeline repeats on every sample, and the backoff below caps at 5 s, so
    // an ungated log here would write a stack trace every 5 s for as long as the app runs. Rate-limit
    // it the same way the fan-out does — the suppressed count is also what distinguishes a one-off
    // blip from a pipeline that is broken for good.
    private readonly HandlerFaultReporter _faults;

    private const string CallbackFault =
        "Gaze callback threw; treating it as a device error and reconnecting";

    // Must keep a reference to prevent GC from collecting the delegate
    private tobii_gaze_point_callback_t? _gazeCallback;
    private tobii_eye_position_normalized_callback_t? _eyePositionCallback;
    private tobii_head_pose_callback_t? _headPoseCallback;
    private tobii_user_presence_callback_t? _presenceCallback;

    // Track-status cache: written by three independent stream callbacks (each may
    // fire on the Tobii callback thread at its own cadence), read by the UI's
    // DispatcherTimer poll. _statusLock serializes the partial-update reads/writes
    // so one callback never clobbers fields owned by another.
    private readonly object _statusLock = new();
    private TrackStatusSample _latestStatus;
    private bool _hasStatus;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// The Tobii Stream Engine supplies all three optional track-status streams
    /// (normalized eye position, head pose, user presence).
    /// </summary>
    public GazeProviderCapabilities Capabilities =>
        GazeProviderCapabilities.EyePosition |
        GazeProviderCapabilities.HeadPose |
        GazeProviderCapabilities.UserPresence;

    /// <summary>
    /// Thread-safe read of the latest track-status sample assembled from the three
    /// stream callbacks. Returns false until at least one callback has fired.
    /// </summary>
    public bool TryGetLatestStatus(out TrackStatusSample sample)
    {
        lock (_statusLock)
        {
            sample = _latestStatus;
            return _hasStatus;
        }
    }

    /// <summary>
    /// Creates a Tobii Stream Engine provider. Connection is attempted in
    /// <see cref="Start"/> and retried automatically in the poll loop, so the
    /// app never crashes at startup even when no Tobii device is present.
    /// </summary>
    public TobiiStreamEngineProvider(double screenWidth, double screenHeight,
        ILogger<TobiiStreamEngineProvider> logger)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _logger = logger;
        _faults = new HandlerFaultReporter(
            (message, ex) => logger.LogError(ex, "{FaultMessage}", message),
            () => Environment.TickCount64 / 1000,
            windowSeconds: 30);
    }

    public void Start()
    {
        _stopSignal.Reset();
        TryFullConnect();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiStreamEnginePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _stopSignal.Set();

        // Retain the thread reference if the join times out so a later Dispose can re-join
        // before disposing _stopSignal (the poll thread waits on it during backoff).
        bool joined = _pollThread?.Join(2000) ?? true;
        if (joined) _pollThread = null;
        _pollThreadExited = joined;

        lock (_lock)
        {
            CleanupDevice();
            CleanupApi();
        }

        IsConnected = false;
    }

    /// <summary>
    /// Attempts the full connection chain: API → device → gaze subscription.
    /// Safe to call at any time; no-ops stages that are already connected.
    /// </summary>
    private void TryFullConnect()
    {
        if (!TryConnectApi()) return;
        if (!TryConnectDevice()) return;
        SubscribeGaze();
    }

    private bool TryConnectApi()
    {
        if (_apiContext != IntPtr.Zero) return true;

        var result = Interop.tobii_api_create(out _apiContext, null);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
        {
            _logger.LogWarning("Failed to create Tobii Stream Engine API: {Result}", result);
            _apiContext = IntPtr.Zero;
            return false;
        }
        _logger.LogInformation("Tobii Stream Engine API initialized");
        return true;
    }

    private bool TryConnectDevice()
    {
        if (_deviceContext != IntPtr.Zero) return true;
        if (_apiContext == IntPtr.Zero) return false;

        var result = Interop.tobii_enumerate_local_device_urls(
            _apiContext, out List<string> urls);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR || urls.Count == 0)
        {
            _logger.LogWarning("No Tobii device found via Stream Engine");
            return false;
        }

        result = Interop.tobii_device_create(
            _apiContext, urls[0],
            Interop.tobii_field_of_use_t.TOBII_FIELD_OF_USE_STORE_OR_TRANSFER_FALSE,
            out _deviceContext);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
        {
            _logger.LogWarning("Failed to create Tobii device: {Result}", result);
            _deviceContext = IntPtr.Zero;
            return false;
        }
        _logger.LogInformation("Tobii device created: {Url}", urls[0]);
        return true;
    }

    private void SubscribeGaze()
    {
        if (_deviceContext == IntPtr.Zero) return;

        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            // Device timestamp: monotonic microseconds from the tracker clock (arbitrary
            // epoch). The pipeline only ever consumes timestamp DIFFERENCES (OneEuroFilter
            // dt, GapInterpolator gap length, fixation durations), so the epoch is
            // irrelevant — but the previous wall-clock fabrication
            // (UtcNow.ToUnixTimeMilliseconds() * 1000) had only 1 ms resolution, causing
            // dt jitter/zero divisions in the OneEuroFilter at ~33 Hz.
            if (gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
            {
                // Stream Engine gives normalized 0..1 coords; convert to screen pixels
                GazePointReceived?.Invoke(new GazePoint(
                    _screenWidth * gazePoint.position.x,
                    _screenHeight * gazePoint.position.y,
                    gazePoint.timestamp_us,
                    true));
            }
            else
            {
                // Forward invalid samples (eyes lost / blink) instead of dropping them.
                // GapInterpolator blink handling and EyeCare away-detection both key off
                // GazePoint.IsValid == false and never saw it from the real device before.
                GazePointReceived?.Invoke(new GazePoint(0, 0, gazePoint.timestamp_us, false));
            }
        };

        tobii_error_t result = Interop.tobii_gaze_point_subscribe(
            _deviceContext, _gazeCallback, IntPtr.Zero);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            _logger.LogWarning("Gaze subscription failed: {Error}", result);

        // --- Track-status streams (eye position / head pose / user presence) ---
        // Each callback does a PARTIAL update of the shared cache, copying out the
        // previous sample under the lock, overwriting only its own fields, and
        // writing back — so concurrent callbacks never clobber each other's data.
        // left/right_validity is per-eye (not per-coordinate) in this wrapper, so a
        // single validity check flattens to the EyeSample.IsValid bool.

        _eyePositionCallback = (ref tobii_eye_position_normalized_t eyePosition, IntPtr userData) =>
        {
            var left = new EyeSample(
                eyePosition.left.x, eyePosition.left.y, eyePosition.left.z,
                eyePosition.left_validity == tobii_validity_t.TOBII_VALIDITY_VALID);
            var right = new EyeSample(
                eyePosition.right.x, eyePosition.right.y, eyePosition.right.z,
                eyePosition.right_validity == tobii_validity_t.TOBII_VALIDITY_VALID);
            lock (_statusLock)
            {
                _latestStatus = _latestStatus with
                {
                    Left = left,
                    Right = right,
                    TimestampMicroseconds = eyePosition.timestamp_us
                };
                _hasStatus = true;
            }
        };

        _headPoseCallback = (ref tobii_head_pose_t headPose, IntPtr userData) =>
        {
            // position_xyz.z is the head's distance from the tracker in mm
            // (positive, increases as the user moves away).
            double distanceMm = headPose.position_xyz.z;
            bool distanceValid = headPose.position_validity == tobii_validity_t.TOBII_VALIDITY_VALID;
            lock (_statusLock)
            {
                _latestStatus = _latestStatus with
                {
                    HeadDistanceMm = distanceMm,
                    HeadDistanceValid = distanceValid
                };
                _hasStatus = true;
            }
        };

        _presenceCallback = (tobii_user_presence_status_t status, long timestampUs, IntPtr userData) =>
        {
            bool present = status == tobii_user_presence_status_t.TOBII_USER_PRESENCE_STATUS_PRESENT;
            lock (_statusLock)
            {
                _latestStatus = _latestStatus with { UserPresent = present, TimestampMicroseconds = timestampUs };
                _hasStatus = true;
            }
        };

        result = Interop.tobii_eye_position_normalized_subscribe(
            _deviceContext, _eyePositionCallback, IntPtr.Zero);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            _logger.LogWarning("Eye position subscription failed: {Error}", result);

        result = Interop.tobii_head_pose_subscribe(
            _deviceContext, _headPoseCallback, IntPtr.Zero);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            _logger.LogWarning("Head pose subscription failed: {Error}", result);

        result = Interop.tobii_user_presence_subscribe(
            _deviceContext, _presenceCallback, IntPtr.Zero);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            _logger.LogWarning("User presence subscription failed: {Error}", result);
    }

    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            bool connected;
            lock (_lock)
            {
                connected = _apiContext != IntPtr.Zero && _deviceContext != IntPtr.Zero;
            }

            if (!connected)
            {
                if (_stopSignal.Wait(backoffMs)) return;
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                lock (_lock)
                {
                    if (_running) TryFullConnect();
                }
                continue;
            }

            bool hasError = false;
            try
            {
                lock (_lock)
                {
                    if (!_running) return;
                    tobii_error_t result = Interop.tobii_wait_for_callbacks(
                        new[] { _deviceContext });
                    hasError = IsError(result);

                    if (!hasError)
                    {
                        result = Interop.tobii_device_process_callbacks(_deviceContext);
                        hasError = IsError(result);
                    }
                }
            }
            catch (Exception ex)
            {
                // tobii_device_process_callbacks invokes the managed gaze callback, so a subscriber
                // fault re-enters managed code here. This thread is IsBackground = true, and an
                // unhandled exception on it terminates the process — which for this app costs the
                // user their only input channel. GazeEngine guards its own handlers, so reaching
                // this catch means a fault in the pipeline itself.
                //
                // Treating it as a device error and reconnecting is not just a convenient reuse of
                // the backoff: the exception unwound through Stream Engine's native frame while it
                // was dispatching callbacks, so the device's internal dispatch state is unknown and
                // continuing to poll it is the less defensible option. IsConnected therefore goes
                // false, which is honest — no gaze is reaching the pipeline either way.
                //
                // The catch is OUTSIDE the lock on purpose: the backoff below must not hold _lock,
                // or Stop() cannot make progress. Reporting is rate-limited and swallows a broken
                // logger, so nothing here can escape onto this thread.
                _faults.Report(CallbackFault, ex);
                hasError = true;
            }

            if (hasError)
            {
                IsConnected = false;
                if (_stopSignal.Wait(backoffMs)) return;
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                lock (_lock)
                {
                    if (_running && _deviceContext != IntPtr.Zero)
                        Interop.tobii_device_reconnect(_deviceContext);
                }
            }
            else if (!IsConnected)
            {
                IsConnected = true;
                _lastErrorMessage = null;
                backoffMs = 500;
                _logger.LogInformation("Connected");
            }
        }
    }

    private bool IsError(tobii_error_t error)
    {
        if (error == tobii_error_t.TOBII_ERROR_NO_ERROR) return false;
        // Error dedup
        var msg = error.ToString();
        if (msg != _lastErrorMessage)
        {
            _lastErrorMessage = msg;
            _logger.LogWarning("Error: {Error}", msg);
        }
        return true;
    }

    private void CleanupDevice()
    {
        if (_deviceContext != IntPtr.Zero)
        {
            Interop.tobii_gaze_point_unsubscribe(_deviceContext);
            Interop.tobii_eye_position_normalized_unsubscribe(_deviceContext);
            Interop.tobii_head_pose_unsubscribe(_deviceContext);
            Interop.tobii_user_presence_unsubscribe(_deviceContext);
            Interop.tobii_device_destroy(_deviceContext);
            _deviceContext = IntPtr.Zero;
        }

        // Drop any cached status from the destroyed device so stale eye/head/presence
        // data isn't reported after disconnect/reconnect.
        lock (_statusLock)
        {
            _latestStatus = default;
            _hasStatus = false;
        }
    }

    private void CleanupApi()
    {
        if (_apiContext != IntPtr.Zero)
        {
            Interop.tobii_api_destroy(_apiContext);
            _apiContext = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        // Only dispose the stop signal once the poll thread has actually exited; otherwise a
        // still-running poll thread could Wait() on a disposed handle (ObjectDisposedException).
        // A leaked lightweight handle at shutdown is preferable to a background-thread throw.
        if (_pollThreadExited)
            _stopSignal.Dispose();
    }
}
#endif
