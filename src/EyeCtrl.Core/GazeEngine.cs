namespace EyeCtrl.Core;

using System.ComponentModel;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

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
    // Same defer-onto-the-gaze-thread contract as _fixationReconfigureRequested below; see the comment there.
    // A cursor-settings edit only requests the interpolator rebuild, which then runs on the gaze thread so
    // CopyStateFrom can never read the old interpolator while Process is mutating it.
    private volatile bool _interpolatorRebuildRequested;
    private readonly PropertyChangedEventHandler _cursorSettingsChangedHandler;
    private readonly Action<PhysicalPoint> _startTrackingHandler;
    // Never swapped: tolerance changes are applied IN PLACE via FixationDetector.RadiusPixels /
    // FixationTimeMs, so the instance (and its four subscriptions, and any in-progress fixation) outlives
    // every reconfiguration. See ApplyPendingFixationReconfiguration.
    private readonly FixationDetector _fixationDetector;
    private readonly FixationCentroidTracker _centroidTracker;
    private readonly EffectiveSettingsProvider? _effectiveSettings;
    private readonly PropertyChangedEventHandler _dwellSettingsChangedHandler;
    private readonly Action? _effectiveSettingsChangedHandler;
    // Set by the effective-settings Changed handler / the DwellSettings handler (dispatcher thread) and
    // read+cleared on the gaze thread at the top of OnGazePoint. Deferring the apply onto the gaze thread
    // keeps every _fixationDetector mutation on the one thread that calls Update, eliminating the
    // dispatcher/gaze data race a synchronous dispatcher-thread write would create now that the
    // AdaptationCoordinator ticks the controller from its dispatcher timer. Volatile supplies the
    // release/acquire fence; a bool is atomic on every arch.
    private volatile bool _fixationReconfigureRequested;

    // Copy-on-write handler array. Publication does one volatile read and iterates by index, so it
    // never allocates and cannot be disturbed by a subscription change mid-publication — which
    // matters because a handler may unsubscribe itself from inside its own callback.
    // GetInvocationList() was the obvious alternative and was rejected: it allocates per sample at
    // 30-90 Hz, trading a crash risk for GC pressure on the gaze thread.
    //
    // INVARIANT: array elements are NEVER mutated in place. Add/remove build a whole new array under
    // _handlerLock and publish it by assigning this field. That assignment is the only fence there
    // is: `volatile` orders the reference read and write, but the elements are plain references, so
    // correctness depends on a reader that observes the new reference observing a fully-constructed
    // array. Writing into an existing array — even under the lock — would let a concurrent publisher
    // read a half-updated slot, and no amount of `volatile` on this field would catch it.
    private volatile Action<GazePoint>[] _gazeSampledHandlers = [];
    // Copy-on-write, same invariant as _gazeSampledHandlers: elements are never mutated in place;
    // add/remove build a new array under _handlerLock and publish it by assigning this field.
    private volatile Action<PhysicalPoint, long>[] _cursorMovedHandlers = [];
    private readonly object _handlerLock = new();
    private readonly HandlerFaultReporter _faults;

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
    /// <summary>Fired when cursor moves with filtered position in physical screen pixels.
    /// <para>
    /// Handlers run on the gaze thread and must be cheap and non-blocking, as with
    /// <see cref="GazeSampled"/>. A throwing handler is caught and reported rather than aborting the
    /// remaining handlers or the rest of the pipeline. Note this event carries the filtered point and
    /// deliberately does NOT carry validity — consumers needing eyes-lost detection or off-screen
    /// coordinates must use <see cref="GazeSampled"/>.
    /// </para></summary>
    public event Action<PhysicalPoint, long>? CursorMoved
    {
        add
        {
            if (value is null) return;
            lock (_handlerLock)
                _cursorMovedHandlers = [.. _cursorMovedHandlers, value];
        }
        remove
        {
            if (value is null) return;
            bool gone;
            lock (_handlerLock)
                gone = !Contains(_cursorMovedHandlers = RemoveLast(_cursorMovedHandlers, value), value);
            // Outside the lock: Forget takes the reporter's own gate, and nesting _handlerLock around
            // it would invent a lock order for no reason.
            if (gone) _faults.Forget(value);
        }
    }

    /// <summary>Fires the raw (pre-interpolation) gaze sample including its validity flag.
    /// Used by eye-care scheduling/away-detection. CursorMoved fires the interpolated point;
    /// this event fires the raw point so subscribers can detect eyes-lost / looking-away.
    /// <para>
    /// Handlers run on the gaze thread, inside the gaze provider's lock. They must be cheap and must
    /// not block — blocking here also blocks Stop() and TryGetLatestStatus. A handler that throws is
    /// caught and reported; it does not abort the remaining handlers and does not reach the provider
    /// thread, where an unhandled exception would terminate the process.
    /// </para></summary>
    public event Action<GazePoint>? GazeSampled
    {
        add
        {
            if (value is null) return;
            lock (_handlerLock)
                _gazeSampledHandlers = [.. _gazeSampledHandlers, value];
        }
        remove
        {
            if (value is null) return;
            bool gone;
            lock (_handlerLock)
                gone = !Contains(_gazeSampledHandlers = RemoveLast(_gazeSampledHandlers, value), value);
            if (gone) _faults.Forget(value);
        }
    }

    // Only drop the fault bookkeeping once the LAST equal subscription is gone — a caller that
    // subscribed the same handler twice still has one live, and resetting its window would let it
    // log again inside the window it is being rate-limited by.
    private static bool Contains<T>(T[] source, T value) where T : class
    {
        foreach (var candidate in source)
        {
            if (candidate.Equals(value)) return true;
        }
        return false;
    }

    // Mirrors multicast -= semantics: remove the LAST matching handler, not every match. A filter
    // such as Where(h => !h.Equals(value)) would drop all duplicates and silently change behaviour
    // for any caller that subscribed the same target twice.
    private static T[] RemoveLast<T>(T[] source, T value) where T : class
    {
        for (int i = source.Length - 1; i >= 0; i--)
        {
            if (!source[i].Equals(value))
                continue;
            var result = new T[source.Length - 1];
            Array.Copy(source, 0, result, 0, i);
            Array.Copy(source, i + 1, result, i, source.Length - i - 1);
            return result;
        }
        return source;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GazeEngine"/> class.
    /// Wires up the gaze provider, filter, fixation detector, centroid tracker, and interaction manager.
    /// </summary>
    /// <param name="gazeProvider">The gaze data source (Tobii, mouse, etc.).</param>
    /// <param name="inputSimulator">Platform-level mouse/keyboard input simulator.</param>
    /// <param name="settings">Application settings for filter, dwell, and selection configuration.</param>
    /// <param name="calibrationService">Optional calibration service for gaze-to-screen coordinate mapping.</param>
    /// <param name="effectiveSettings">Optional effective-settings provider. When null, tolerance
    /// reads the baseline directly and no adaptation-driven reconfiguration occurs.</param>
    /// <param name="logger">Optional logger for handler faults. When null, faults are swallowed
    /// silently — acceptable in tests, but production should always supply one.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null,
        EffectiveSettingsProvider? effectiveSettings = null,
        ILogger<GazeEngine>? logger = null)
    {
        _faults = new HandlerFaultReporter(
            (message, ex) => logger?.LogError(ex, "{FaultMessage}", message),
            () => Environment.TickCount64 / 1000,
            windowSeconds: 30);

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

        // Built once with the effective radius and reconfigured IN PLACE thereafter (design §5): its
        // RadiusPixels/FixationTimeMs are settable, so the instance and its four subscriptions live for the
        // engine's lifetime and an in-progress fixation survives every tolerance change.
        _fixationDetector = new FixationDetector(EffectiveFixationRadius(), settings.Dwell.FixationDetectionMs);
        WireFixation(_fixationDetector);

        // Reconfigure on a baseline tolerance edit. GazeEngine previously subscribed to CursorSettings only,
        // so DwellSettings tolerance edits never reached the live detector (latent bug — now fixed).
        _dwellSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(DwellSettings.FixationRadiusPixels)
                               or nameof(DwellSettings.FixationDetectionMs))
                RequestFixationReconfiguration();
        };
        _settings.Dwell.PropertyChanged += _dwellSettingsChangedHandler;

        // Reconfigure when an adaptation delta changes the effective tolerance. The provider's Changed fires
        // on EVERY SetMomentaryFill (any knob), so a fill on a non-tolerance knob also lands here; the apply
        // is idempotent (writing the unchanged radius is a no-op), so it costs two compares and needs no
        // change detection of its own.
        if (_effectiveSettings is not null)
        {
            _effectiveSettingsChangedHandler = RequestFixationReconfiguration;
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
    // actual write runs on the gaze thread at the top of the next OnGazePoint, which keeps every
    // _fixationDetector mutation on the one thread that calls Update. Coalescing is a bonus: a burst of
    // Changed events (the controller steps four knobs per tick) costs one apply per gaze frame.
    private void RequestFixationReconfiguration() => _fixationReconfigureRequested = true;

    // Runs on the gaze thread. Applies the current effective tolerance to the LIVE detector in place. No
    // rebuild, so an in-progress fixation is preserved across an adaptation step: the controller relaxes and
    // recovers a knob every tick (250 ms) while active, and a rebuild-per-step would end the fixation each
    // time — with FixationDetectionMs (default 300 ms) longer than the tick interval, no fixation could ever
    // complete during a ramp. Writing an unchanged value is a no-op, so no change detection is needed and a
    // non-tolerance knob's fill costs two compares.
    private void ApplyPendingFixationReconfiguration()
    {
        if (!_fixationReconfigureRequested)
            return;
        _fixationReconfigureRequested = false;
        // FixationDetectionMs is not an adaptation knob, so it comes straight from the baseline. Applying it
        // here (rather than only on a radius change) is what makes a manual detection-time edit take effect.
        _fixationDetector.RadiusPixels = EffectiveFixationRadius();
        _fixationDetector.FixationTimeMs = _settings.Dwell.FixationDetectionMs;
    }

    // Runs on the gaze thread, before Process below, so CopyStateFrom reads the outgoing interpolator on the
    // only thread that mutates it. Same contract as ApplyPendingFixationReconfiguration — one convention for
    // every reconfiguration of live gaze-pipeline state. (This one still rebuilds: IGazeFilter exposes no way
    // to retune in place, so the fresh-filter + WarmStartFromCurrentState path stands — see design §5 R1.)
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

    /// <summary>Stops the gaze data stream.</summary>
    public void Stop()
    {
        _gazeProvider.Stop();
    }

    private void PublishGazeSampled(GazePoint gaze)
    {
        var handlers = _gazeSampledHandlers;   // one volatile read; the loop owns this snapshot
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](gaze);
            }
            catch (Exception ex)
            {
                _faults.Report(nameof(GazeSampled), handlers[i], ex);
            }
        }
    }

    private void PublishCursorMoved(PhysicalPoint point, long timestampUs)
    {
        var handlers = _cursorMovedHandlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](point, timestampUs);
            }
            catch (Exception ex)
            {
                _faults.Report(nameof(CursorMoved), handlers[i], ex);
            }
        }
    }

    private void OnGazePoint(GazePoint gaze)
    {
        // Deferred reconfiguration (requested on the dispatcher thread) runs here on the gaze thread first,
        // before any _fixationDetector/_centroidTracker/_gapInterpolator use below, so all mutations of the
        // live pipeline state stay on this thread. Each is skipped when nothing was requested.
        ApplyPendingFixationReconfiguration();
        ApplyPendingInterpolatorRebuild();

        PublishGazeSampled(gaze);

        var corrected = gaze;
        if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
        {
            var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
            corrected = gaze with { X = p.X, Y = p.Y };
        }
        var (filtered, isInGap) = _gapInterpolator.Process(corrected);

        // Always fire CursorMoved so the cursor circle + toolbar receive gaze
        // (this allows unpause via toolbar dwell even when paused)
        PublishCursorMoved(filtered, gaze.TimestampMicroseconds);

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
