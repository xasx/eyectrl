namespace EyeCtrl.Core;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// Core gaze processing engine.
/// All gaze coordinates are in physical screen pixels.
/// </summary>
public class GazeEngine : IDisposable
{
    private readonly IGazeProvider _gazeProvider;
    private readonly IInputSimulator _inputSimulator;
    private readonly EyeCtrlSettings _settings;
    private readonly CalibrationService? _calibrationService;
    private volatile GapInterpolator _gapInterpolator;
    private readonly FixationDetector _fixationDetector;
    private readonly FixationCentroidTracker _centroidTracker;

    /// <summary>Interaction state machine controlling fixation → activation → action flow.</summary>
    public InteractionManager InteractionManager { get; }
    private volatile bool _suppressFixation;

    /// <summary>When true, fixation detection is suppressed (e.g., during overlay input or dwell cooldown).</summary>
    public bool SuppressFixation
    {
        get => _suppressFixation;
        set => _suppressFixation = value;
    }
    /// <summary>Stable centroid position in physical screen pixels.</summary>
    public PhysicalPoint? StableCentroid => _centroidTracker.StableCentroid;
    /// <summary>Fired when cursor moves with filtered position in physical screen pixels.</summary>
    public event Action<PhysicalPoint, long>? CursorMoved;

    /// <summary>
    /// Initializes a new instance of the <see cref="GazeEngine"/> class.
    /// Wires up the gaze provider, filter, fixation detector, centroid tracker, and interaction manager.
    /// </summary>
    /// <param name="gazeProvider">The gaze data source (Tobii, mouse, etc.).</param>
    /// <param name="inputSimulator">Platform-level mouse/keyboard input simulator.</param>
    /// <param name="settings">Application settings for filter, dwell, and selection configuration.</param>
    /// <param name="calibrationService">Optional calibration service for gaze-to-screen coordinate mapping.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null)
    {
        _gazeProvider = gazeProvider;
        _inputSimulator = inputSimulator;
        _settings = settings;
        _calibrationService = calibrationService;

        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(settings.Cursor),
            settings.Cursor.MaxGapInterpolationMs);

        _settings.Cursor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs))
            {
                var newInterpolator = new GapInterpolator(
                    GazeFilterFactory.Create(_settings.Cursor),
                    _settings.Cursor.MaxGapInterpolationMs);
                newInterpolator.CopyStateFrom(_gapInterpolator);
                _gapInterpolator = newInterpolator;
            }
        };

        _fixationDetector = new FixationDetector(
            settings.Dwell.FixationRadiusPixels,
            settings.Dwell.FixationDetectionMs);

        _centroidTracker = new FixationCentroidTracker(settings.Dwell.VelocityThresholdPxPerSec);

        InteractionManager = new InteractionManager
        {
            SelectionMode = settings.SelectionMode
        };

        _gazeProvider.GazePointReceived += OnGazePoint;
        _fixationDetector.FixationStarted += InteractionManager.OnFixationStarted;
        _fixationDetector.FixationEnded += InteractionManager.OnFixationEnded;
        _fixationDetector.FixationStarted += _ => _centroidTracker.StartTracking();
        _fixationDetector.FixationEnded += _centroidTracker.StopTracking;
    }

    /// <summary>Starts the gaze data stream.</summary>
    public void Start()
    {
        _gazeProvider.Start();
    }

    /// <summary>Stops the gaze data stream.</summary>
    public void Stop()
    {
        _gazeProvider.Stop();
    }

    private void OnGazePoint(GazePoint gaze)
    {
        var corrected = gaze;
        if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
        {
            var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
            corrected = gaze with { X = p.X, Y = p.Y };
        }
        var (filtered, isInGap) = _gapInterpolator.Process(corrected);

        // Always fire CursorMoved so the cursor circle + toolbar receive gaze
        // (this allows unpause via toolbar dwell even when paused)
        CursorMoved?.Invoke(filtered, gaze.TimestampMicroseconds);

        _centroidTracker.Update(filtered, gaze.TimestampMicroseconds);

        if (InteractionManager.CurrentState == InteractionState.Paused)
            return;

        if (InteractionManager.CurrentState == InteractionState.FreeLook)
        {
            _inputSimulator.MoveCursor(filtered);
        }

        if (!SuppressFixation && !isInGap)
            _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
    }

    /// <summary>Unsubscribes from the gaze provider and disposes it.</summary>
    public void Dispose()
    {
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
