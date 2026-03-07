namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;
using Tobii.StreamEngine;

/// <summary>
/// Gaze provider using the Tobii Stream Engine (Tobii.StreamEngine.Net.dll).
/// Alternative to TobiiInteractionLibProvider. Uses lower-level SDK with
/// callback-based gaze subscription and blocking wait.
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
    private string? _lastErrorMessage;

    // Must keep a reference to prevent GC from collecting the delegate
    private tobii_gaze_point_callback_t? _gazeCallback;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a Tobii Stream Engine provider. Throws if the Stream Engine API
    /// cannot be initialized or no device is found.
    /// </summary>
    public TobiiStreamEngineProvider(double screenWidth, double screenHeight,
        ILogger<TobiiStreamEngineProvider> logger)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _logger = logger;

        ConnectApi();
        ConnectDevice();
    }

    public void Start()
    {
        SubscribeGaze();

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
        _pollThread?.Join(1000);
        _pollThread = null;

        lock (_lock)
        {
            CleanupDevice();
            CleanupApi();
        }

        IsConnected = false;
    }

    private void ConnectApi()
    {
        tobii_error_t result = Interop.tobii_api_create(out _apiContext, null);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException(
                $"Failed to create Tobii Stream Engine API: {result}");
    }

    private void ConnectDevice()
    {
        tobii_error_t result = Interop.tobii_enumerate_local_device_urls(
            _apiContext, out List<string> urls);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR || urls.Count == 0)
            throw new InvalidOperationException("No Tobii device found via Stream Engine");

        result = Interop.tobii_device_create(
            _apiContext, urls[0],
            Interop.tobii_field_of_use_t.TOBII_FIELD_OF_USE_INTERACTIVE,
            out _deviceContext);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException(
                $"Failed to create Tobii device: {result}");
    }

    private void SubscribeGaze()
    {
        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            if (gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
            {
                // Stream Engine gives normalized 0..1 coords; convert to screen pixels
                var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                GazePointReceived?.Invoke(new GazePoint(
                    _screenWidth * gazePoint.position.x,
                    _screenHeight * gazePoint.position.y,
                    timestampUs,
                    true));
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
            lock (_lock)
            {
                if (_deviceContext == IntPtr.Zero) break;

                tobii_error_t result = Interop.tobii_wait_for_callbacks(
                    new[] { _deviceContext });
                bool hasError = IsError(result);

                if (!hasError)
                {
                    result = Interop.tobii_device_process_callbacks(_deviceContext);
                    hasError = IsError(result);
                }

                if (hasError)
                {
                    IsConnected = false;
                    Thread.Sleep(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                    Interop.tobii_device_reconnect(_deviceContext);
                }
                else
                {
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        _lastErrorMessage = null;
                        backoffMs = 500;
                        _logger.LogInformation("Connected");
                    }
                }
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
        GC.SuppressFinalize(this);
    }
}
