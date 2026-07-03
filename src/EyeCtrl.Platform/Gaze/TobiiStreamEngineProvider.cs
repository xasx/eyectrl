#if TOBII_AVAILABLE
namespace EyeCtrl.Platform.Gaze;

using System.Threading;
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

    // Must keep a reference to prevent GC from collecting the delegate
    private tobii_gaze_point_callback_t? _gazeCallback;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

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

            bool hasError;
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
            Interop.tobii_device_destroy(_deviceContext);
            _deviceContext = IntPtr.Zero;
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
