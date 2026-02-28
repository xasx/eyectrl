namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Tobii.InteractionLib;

/// <summary>
/// Gaze provider using the Tobii Interaction Library (tobii_interaction_lib_cs.dll).
/// Polls the tracker on a background thread and fires GazePointReceived on each valid gaze sample.
/// </summary>
public class TobiiInteractionLibProvider : IGazeProvider
{
    private readonly float _screenWidth;
    private readonly float _screenHeight;

    private IInteractionLib? _interactionLib;
    private Thread? _pollThread;
    private volatile bool _running;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a Tobii gaze provider. Throws immediately if the Tobii native library
    /// is not available, so callers can catch and fall back to MouseGazeProvider.
    /// </summary>
    public TobiiInteractionLibProvider(double screenWidth, double screenHeight)
    {
        _screenWidth = (float)screenWidth;
        _screenHeight = (float)screenHeight;

        // Create the InteractionLib immediately to fail fast if Tobii is unavailable
        _interactionLib = InteractionLibFactory.CreateInteractionLib(FieldOfUse.Interactive);
    }

    public void Start()
    {
        // Configure display area to match the primary screen in pixels
        _interactionLib!.CoordinateTransformAddOrUpdateDisplayArea(
            _screenWidth, _screenHeight,  // width/height in user units (pixels)
            _screenWidth, _screenHeight,  // width/height in virtual units (pixels)
            0f, 0f,                       // origin offset
            "main");

        // Subscribe to gaze point data
        _interactionLib.GazePointDataEvent += OnGazePointData;

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
        _running = false;
        _pollThread?.Join(1000);
        _pollThread = null;

        if (_interactionLib != null)
        {
            _interactionLib.GazePointDataEvent -= OnGazePointData;
            _interactionLib = null;
        }

        IsConnected = false;
    }

    private void PollLoop()
    {
        while (_running)
        {
            try
            {
                _interactionLib?.WaitAndUpdate(33); // ~30 Hz
                if (!IsConnected) IsConnected = true;
            }
            catch
            {
                IsConnected = false;
                Thread.Sleep(100);
            }
        }
    }

    private void OnGazePointData(GazePointData data)
    {
        if (data.validity != Validity.Valid) return;

        GazePointReceived?.Invoke(new GazePoint(
            data.x,
            data.y,
            data.timestamp_us,
            true));
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
