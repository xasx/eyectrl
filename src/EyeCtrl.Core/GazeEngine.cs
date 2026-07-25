namespace EyeCtrl.Core;

using System.ComponentModel;
using EyeCtrl.Core.Adaptation;
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
    // Same defer-onto-the-gaze-thread contract as _fixationRebuildRequested below; see the comment there.
    // A cursor-settings edit only requests the interpolator rebuild, which then runs on the gaze thread so
    // CopyStateFrom can never read the old interpolator while Process is mutating it.
    private volatile bool _interpolatorRebuildRequested;
    private readonly PropertyChangedEventHandler _cursorSettingsChangedHandler;
    private readonly Action<PhysicalPoint> _startTrackingHandler;
    private volatile FixationDetector _fixationDetector;
    private double _currentFixationRadius;
    private readonly FixationCentroidTracker _centroidTracker;
    private readonly EffectiveSettingsProvider? _effectiveSettings;
    private readonly PropertyChangedEventHandler _dwellSettingsChangedHandler;
    private readonly Action? _effectiveSettingsChangedHandler;
    // Set by the effective-settings Changed handler (dispatcher thread) and read+cleared on the gaze thread
    // at the top of OnGazePoint. Deferring the rebuild onto the gaze thread serializes every _fixationDetector
    // and _centroidTracker mutation on that one thread (the centroid tracker is also mutated by Update below),
    // eliminating the dispatcher/gaze data race that a synchronous dispatcher-thread rebuild would create now
    // that the AdaptationCoordinator ticks the controller from its dispatcher timer. Volatile supplies the
    // release/acquire fence; a bool is atomic on every arch.
    private volatile bool _fixationRebuildRequested;

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
    /// <summary>Gaze-speed standard deviation (px/s) of the last *completed* fixation — a jitter/shakiness
    /// proxy latched at fixation end (design §6.2). Persists between fixations.</summary>
    public double LastFixationJitter => _centroidTracker.LastCompletedSpeedStdDev;
    /// <summary>Fired when cursor moves with filtered position in physical screen pixels.</summary>
    public event Action<PhysicalPoint, long>? CursorMoved;

    /// <summary>Fires the raw (pre-interpolation) gaze sample including its validity flag.
    /// Used by eye-care scheduling/away-detection. CursorMoved fires the interpolated point;
    /// this event fires the raw point so subscribers can detect eyes-lost / looking-away.</summary>
    public event Action<GazePoint>? GazeSampled;

    /// <summary>
    /// Initializes a new instance of the <see cref="GazeEngine"/> class.
    /// Wires up the gaze provider, filter, fixation detector, centroid tracker, and interaction manager.
    /// </summary>
    /// <param name="gazeProvider">The gaze data source (Tobii, mouse, etc.).</param>
    /// <param name="inputSimulator">Platform-level mouse/keyboard input simulator.</param>
    /// <param name="settings">Application settings for filter, dwell, and selection configuration.</param>
    /// <param name="calibrationService">Optional calibration service for gaze-to-screen coordinate mapping.</param>
    /// <param name="effectiveSettings">Optional effective-settings provider. When null, tolerance
    /// reads the baseline directly and no adaptation-driven rebuild occurs.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null,
        EffectiveSettingsProvider? effectiveSettings = null)
    {
        _gazeProvider = gazeProvider;
        _inputSimulator = inputSimulator;
        _settings = settings;
        _calibrationService = calibrationService;

        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(settings.Cursor),
            settings.Cursor.MaxGapInterpolationMs);

        _cursorSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs)
                               or nameof(CursorSettings.OneEuroMinCutoff)
                               or nameof(CursorSettings.OneEuroBeta)
                               or nameof(CursorSettings.SmoothingFactor))
            {
                // Request only — the rebuild itself runs on the gaze thread (ApplyPendingInterpolatorRebuild),
                // because CopyStateFrom reads the OLD interpolator's carried-over position/timestamp, which the
                // gaze thread mutates inside Process. Coalescing is a bonus: dragging a smoothing slider now
                // costs one rebuild per gaze frame instead of one per PropertyChanged tick.
                _interpolatorRebuildRequested = true;
            }
        };
        _settings.Cursor.PropertyChanged += _cursorSettingsChangedHandler;

        _effectiveSettings = effectiveSettings;

        _centroidTracker = new FixationCentroidTracker(settings.Dwell.VelocityThresholdPxPerSec);

        InteractionManager = new InteractionManager
        {
            SelectionMode = settings.SelectionMode
        };

        _startTrackingHandler = _ => _centroidTracker.StartTracking();

        // FixationDetector radius/time are readonly, so tolerance changes are applied by REBUILDING it
        // (design §5). Build with the effective radius and move its four subscriptions across rebuilds.
        // The rebuild itself is deferred onto the gaze thread (see _fixationRebuildRequested) so the
        // _centroidTracker is never mutated from two threads.
        _currentFixationRadius = EffectiveFixationRadius();
        _fixationDetector = new FixationDetector(_currentFixationRadius, settings.Dwell.FixationDetectionMs);
        WireFixation(_fixationDetector);

        // Rebuild on a baseline tolerance edit. GazeEngine previously subscribed to CursorSettings only,
        // so DwellSettings tolerance edits never reached the live detector (latent bug — now fixed).
        _dwellSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(DwellSettings.FixationRadiusPixels)
                               or nameof(DwellSettings.FixationDetectionMs))
                RequestFixationRebuild();
        };
        _settings.Dwell.PropertyChanged += _dwellSettingsChangedHandler;

        // Rebuild when an adaptation delta changes the effective tolerance. The provider's Changed fires on
        // EVERY SetMomentaryFill (any knob), so requesting-then-deferring lets the gaze thread skip the
        // rebuild when the effective FixationRadius is unchanged (e.g. only Dwell/Smoothing/Geometry fills
        // moved) — no teardown of an in-progress fixation for a non-tolerance knob step.
        if (_effectiveSettings is not null)
        {
            _effectiveSettingsChangedHandler = RequestFixationRebuild;
            _effectiveSettings.Changed += _effectiveSettingsChangedHandler;
        }

        _gazeProvider.GazePointReceived += OnGazePoint;
    }

    /// <summary>Starts the gaze data stream.</summary>
    public void Start()
    {
        _gazeProvider.Start();
    }

    private double EffectiveFixationRadius() =>
        _effectiveSettings?.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, _settings.Dwell.FixationRadiusPixels)
        ?? _settings.Dwell.FixationRadiusPixels;

    // Called on the dispatcher thread (DwellSettings change or provider.Changed). Just sets a flag — the
    // actual rebuild runs on the gaze thread at the top of the next OnGazePoint, which serializes every
    // _fixationDetector/_centroidTracker mutation on one thread. The gaze thread then skips the rebuild when
    // the effective radius is unchanged (the common case when only a non-tolerance knob fill moved).
    private void RequestFixationRebuild() => _fixationRebuildRequested = true;

    // Runs on the gaze thread. Performs the deferred rebuild if one was requested AND the effective radius
    // actually changed. Skipping on an unchanged radius avoids tearing down an in-progress fixation for a
    // non-tolerance knob step (Dwell/Smoothing/Geometry fills fire Changed too).
    private void ApplyPendingFixationRebuild()
    {
        if (!_fixationRebuildRequested)
            return;
        _fixationRebuildRequested = false;
        double radius = EffectiveFixationRadius();
        if (radius == _currentFixationRadius)
            return; // a non-tolerance fill changed, or FixationDetectionMs alone — no radius rebuild needed
        _currentFixationRadius = radius;
        RebuildFixationDetector();
    }

    // Runs on the gaze thread, before Process below, so CopyStateFrom reads the outgoing interpolator on the
    // only thread that mutates it. Same contract as ApplyPendingFixationRebuild — one convention for every
    // reconfiguration of live gaze-pipeline state.
    private void ApplyPendingInterpolatorRebuild()
    {
        if (!_interpolatorRebuildRequested)
            return;
        _interpolatorRebuildRequested = false;
        var fresh = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor),
            _settings.Cursor.MaxGapInterpolationMs);
        fresh.CopyStateFrom(_gapInterpolator);
        fresh.WarmStartFromCurrentState(); // R1: seed the fresh filter — no cursor jump
        _gapInterpolator = fresh;
    }

    private void WireFixation(FixationDetector d)
    {
        d.FixationStarted += InteractionManager.OnFixationStarted;
        d.FixationEnded += InteractionManager.OnFixationEnded;
        d.FixationStarted += _startTrackingHandler;
        d.FixationEnded += _centroidTracker.StopTracking;
    }

    private void UnwireFixation(FixationDetector d)
    {
        d.FixationStarted -= InteractionManager.OnFixationStarted;
        d.FixationEnded -= InteractionManager.OnFixationEnded;
        d.FixationStarted -= _startTrackingHandler;
        d.FixationEnded -= _centroidTracker.StopTracking;
    }

    // Rebuilds the detector with the current effective radius + baseline detection time (FixationDetectionMs
    // is not an adaptation knob). Runs on the gaze thread (ApplyPendingFixationRebuild), so _centroidTracker
    // is mutated here on the same thread that calls Update below — no cross-thread race. The volatile publish
    // makes the fresh detector visible to any other reader. The fresh detector starts cold, so any in-progress
    // fixation is dropped; since the state machine only honours a new FixationStarted from FreeLook
    // (InteractionManager.OnFixationStarted no-ops otherwise), we END an in-progress fixation cleanly here —
    // mirroring the FixationEnded fan-out from WireFixation — so the activation button never strands and the
    // fresh detector's next fixation re-triggers (review round-1 F2).
    private void RebuildFixationDetector()
    {
        var fresh = new FixationDetector(EffectiveFixationRadius(), _settings.Dwell.FixationDetectionMs);
        WireFixation(fresh);
        var old = _fixationDetector;
        bool wasFixating = old.IsFixating;
        _fixationDetector = fresh;
        UnwireFixation(old);
        if (wasFixating)
        {
            // Old is now unwired; replicate its FixationEnded handlers so the state machine returns to
            // FreeLook and the centroid tracker stops. OnFixationEnded is guarded (no-op unless in Fixation).
            InteractionManager.OnFixationEnded();
            _centroidTracker.StopTracking();
        }
    }

    /// <summary>Stops the gaze data stream.</summary>
    public void Stop()
    {
        _gazeProvider.Stop();
    }

    private void OnGazePoint(GazePoint gaze)
    {
        // Deferred reconfiguration (requested on the dispatcher thread) runs here on the gaze thread first,
        // before any _fixationDetector/_centroidTracker/_gapInterpolator use below, so all mutations of the
        // live pipeline state stay on this thread. Each is skipped when nothing was requested.
        ApplyPendingFixationRebuild();
        ApplyPendingInterpolatorRebuild();

        GazeSampled?.Invoke(gaze);

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

        if (InteractionManager.CurrentState is InteractionState.FreeLook or InteractionState.Dragging)
        {
            _inputSimulator.MoveCursor(filtered);
        }

        if (!SuppressFixation && !isInGap)
            _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
    }

    /// <summary>Unsubscribes from the gaze provider and settings, and disposes the provider.</summary>
    public void Dispose()
    {
        _settings.Cursor.PropertyChanged -= _cursorSettingsChangedHandler;
        _settings.Dwell.PropertyChanged -= _dwellSettingsChangedHandler;
        if (_effectiveSettings is not null && _effectiveSettingsChangedHandler is not null)
            _effectiveSettings.Changed -= _effectiveSettingsChangedHandler;
        _gazeProvider.GazePointReceived -= OnGazePoint;
        UnwireFixation(_fixationDetector);
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
