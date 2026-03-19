namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;
using Tobii.InteractionLib;

/// <summary>
/// Gaze provider using the Tobii Interaction Library (tobii_interaction_lib_cs.dll).
/// Polls the tracker on a background thread and fires GazePointReceived on each valid gaze sample.
/// Reconnects indefinitely with exponential back-off if the tracker disconnects.
/// </summary>
public class TobiiInteractionLibProvider : IGazeProvider
{
    private readonly float _screenWidth;
    private readonly float _screenHeight;
    private readonly ILogger<TobiiInteractionLibProvider> _logger;

    private IInteractionLib? _interactionLib;
    private Thread? _pollThread;
    private volatile bool _running;
    private string? _lastExceptionMessage;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a Tobii gaze provider. Throws immediately if the Tobii native library
    /// is not available, so callers can catch and fall back to the next provider.
    /// </summary>
    public TobiiInteractionLibProvider(double screenWidth, double screenHeight,
        ILogger<TobiiInteractionLibProvider> logger)
    {
        _screenWidth = (float)screenWidth;
        _screenHeight = (float)screenHeight;
        _logger = logger;

        // Create the InteractionLib immediately to fail fast if Tobii is unavailable
        _interactionLib = InteractionLibFactory.CreateInteractionLib(FieldOfUse.Interactive);
    }

    public void Start()
    {
        _logger.LogInformation("Starting Tobii gaze provider (screen: {Width}x{Height})",
            _screenWidth, _screenHeight);

        ConfigureAndSubscribe();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiGazePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _logger.LogInformation("Stopping Tobii gaze provider");

        _running = false;
        _pollThread?.Join(1000);
        _pollThread = null;

        if (_interactionLib != null)
        {
            _interactionLib.GazePointDataEvent -= OnGazePointData;
            _interactionLib = null;
        }

        IsConnected = false;
        _logger.LogInformation("Tobii gaze provider stopped");
    }

    private void ConfigureAndSubscribe()
    {
        _interactionLib!.CoordinateTransformAddOrUpdateDisplayArea(
            _screenWidth, _screenHeight,
            _screenWidth, _screenHeight,
            0f, 0f, "main");

        _interactionLib.GazePointDataEvent += OnGazePointData;
    }

    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            try
            {
                // Ensure _interactionLib is initialized before calling WaitAndUpdate
                // If null (e.g., after failed reconnection), throw to force reconnection path
                if (_interactionLib == null)
                    throw new InvalidOperationException("InteractionLib not initialized");

                _interactionLib.WaitAndUpdate(33); // ~30 Hz
                if (!IsConnected)
                {
                    IsConnected = true;
                    _lastExceptionMessage = null;
                    backoffMs = 500; // reset back-off on success
                    _logger.LogInformation("Connected");
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;

                // Error dedup: only log when the message changes
                if (ex.Message != _lastExceptionMessage)
                {
                    _lastExceptionMessage = ex.Message;
                    _logger.LogWarning("Disconnected: {Message}", ex.Message);
                }

                // Attempt reconnect
                Thread.Sleep(backoffMs);
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);

                try
                {
                    // Dispose old instance
                    if (_interactionLib != null)
                    {
                        _interactionLib.GazePointDataEvent -= OnGazePointData;
                        _interactionLib = null;
                    }

                    // Recreate
                    _interactionLib = InteractionLibFactory.CreateInteractionLib(
                        FieldOfUse.Interactive);
                    ConfigureAndSubscribe();
                    _logger.LogInformation("Reconnection attempt successful, waiting for data");
                }
                catch (Exception reconnectEx)
                {
                    // Log specific reconnection failures to aid troubleshooting
                    _logger.LogWarning(reconnectEx, "Reconnection attempt failed, will retry in {BackoffMs}ms", backoffMs);
                }
            }
        }
    }

    private void OnGazePointData(GazePointData data)
    {
        if (data.validity != Validity.Valid) return;

        // Use system time to avoid Tobii clock drift (~5ms/6min)
        var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        GazePointReceived?.Invoke(new GazePoint(
            data.x,
            data.y,
            timestampUs,
            true));
    }

    public void Dispose()
    {
        Stop();
    }
}
