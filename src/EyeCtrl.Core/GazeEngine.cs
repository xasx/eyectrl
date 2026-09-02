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

    // §9.1 rebuild budget: at most one ADAPTIVE rebuild per second (baseline slider rebuilds are exempt —
    // they already coalesce to one per gaze frame). Seeded at long.MinValue/2 so the FIRST adaptive rebuild
    // of a session is never deferred: a 0 seed would stall it for a full second of gaze-clock time (test
    // clocks start near zero), and long.MinValue itself would overflow the subtraction.
    // See ApplyPendingInterpolatorRebuild for the restart guard.
    private const long MinAdaptiveRebuildIntervalMicroseconds = 1_000_000;
    private long _lastAdaptiveRebuildTimestampMicroseconds = long.MinValue / 2;
    // Written on the dispatcher thread (the provider's Changed handler), read+cleared on the gaze thread —
    // same defer-onto-the-gaze-thread contract as _interpolatorRebuildRequested — and also RE-SET on the
    // gaze thread by the post-publish revalidation (see ApplyPendingInterpolatorRebuild). Every write is
    // `= true` and only the gaze thread clears it, so the two writers cannot lose each other's update.
    private volatile bool _adaptiveRebuildRequested;

    // §13.3 smoothing routing. Sentinel for "the live filter was not built from an effective smoothing
    // parameter" (no provider, or Direct mode). NB: long.MinValue IS the bit pattern of −0.0 — Clamp
    // returns −0.0 unchanged and the CursorSettings guard accepts it (−0.0 < 0.0 is false), so a
    // hand-edited settings.json could collide the sentinel with a real EMA parameter. Benign: −0.0 ≡ 0.0
    // numerically (identical filter behaviour) and the value never changes, so the swallowed comparison
    // is a correct no-op. The bits round-trip exists because the gaze thread (apply) publishes the applied
    // value and the dispatcher thread (the Changed handler) compares against it — a double has no atomic
    // cross-thread read; the long does (Interlocked).
    private const long NoSmoothingBits = long.MinValue;
    private long _lastAppliedSmoothingBits;

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

        _effectiveSettings = effectiveSettings;

        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor, EffectiveSmoothingParameter()),
            _settings.Cursor.MaxGapInterpolationMs);

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
            _effectiveSettingsChangedHandler = OnEffectiveSettingsChanged;
            _effectiveSettings.Changed += _effectiveSettingsChangedHandler;
        }

        // The parameter the constructor-built interpolator runs at: the effective value when the provider
        // holds pre-existing fills, the active mode's baseline with no provider or no fills. Without this,
        // bits default to 0 and the first Changed of ANY knob would compare unequal and spend a spurious rebuild.
        _lastAppliedSmoothingBits = EffectiveSmoothingParameter() is { } initial
            ? BitConverter.DoubleToInt64Bits(initial)
            : NoSmoothingBits;

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

    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests, the ClassifyMode pattern): the
    /// smoothing parameter the live filter was last BUILT with, or <c>null</c> whenever the live filter has
    /// no smoothing parameter (no provider, or Direct mode — Task 3's apply clears the bits to the sentinel
    /// on a null-parameter rebuild). Production reads nothing — the routing tests assert against this
    /// instead of inferring the parameter from step responses.</summary>
    internal double? AppliedSmoothingParameter
    {
        get
        {
            long bits = Interlocked.Read(ref _lastAppliedSmoothingBits); // one atomic read, used twice
            return bits == NoSmoothingBits ? null : BitConverter.Int64BitsToDouble(bits);
        }
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests): how many interpolator rebuilds
    /// (baseline OR adaptive) have run. The knob-aware request is a COST property — a spurious
    /// same-parameter rebuild is behaviourally invisible in step responses — so only a count can pin that a
    /// non-smoothing fill spends none (external implementation review, 2026-09-02). Written on the gaze
    /// thread; tests read it after a synchronous SimulateGaze, i.e. on the same thread. Production reads
    /// nothing.</summary>
    internal int InterpolatorRebuildCount;

    /// <summary>Test seam: invoked on the gaze thread AFTER the fresh interpolator is swapped in but BEFORE
    /// the applied-bits publication — the exact window where a racing provider write (e.g. the §8.6 toolbar
    /// reset's <c>ClearMomentaryFills</c>) compares against the STALE applied bits and skips its request,
    /// which is the lost update the post-publish revalidation exists to close. A test that clears the fills
    /// inside this hook makes that interleaving deterministic. Production leaves it null.</summary>
    internal Action? BeforeSmoothingPublishForTest;

    // §13.3 item (1): the provider→filter VALUE path. The active filter's ONE adaptation-sensitive
    // parameter, composed against the CURRENT mode's baseline — the fill is a fraction OF the baseline, so
    // the baseline must be the active parameter's (OneEuroMinCutoff vs SmoothingFactor), resolved afresh on
    // every read. null = the knob no-ops: Direct has no smoothing parameter (mode is never auto-switched,
    // §5), and no provider means no adaptation at all.
    //
    // §13.3 SAFETY CLAMP: the provider's Smoothing policy carries the union superset [0, 20], but the
    // ACTIVE filter's own validated range is re-applied here — [0.1, 20] for One-Euro, [0, 1] for EMA.
    // Without it, a 0.1 Hz One-Euro baseline at the default fraction composes to 0.05 (and to exactly 0.0
    // at fraction 1.0), which OneEuroFilter's constructor rejects — a throw on the gaze thread, inside the
    // unguarded rebuild. The literals deliberately duplicate the CursorSettings guards (the FixationRadius
    // precedent); the assign-back oracle test fails the build if they drift. Only the FLOOR can engage:
    // smoothing is forgiving-only (parameter down, no tightening), so the composed value never exceeds the
    // baseline, which the setters keep inside the range.
    internal double? EffectiveSmoothingParameter()
    {
        if (_effectiveSettings is null)
            return null;
        return _settings.Cursor.FilterMode switch
        {
            GazeFilterMode.Direct => null,
            GazeFilterMode.Smoothed or GazeFilterMode.SmoothedWithSnapping => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.SmoothingFactor),
                0.0, 1.0),
            // OneEuro, plus the default arm mirroring GazeFilterFactory's default (OneEuroFilter) so a
            // future enum member can never compose an EMA baseline onto a One-Euro filter.
            GazeFilterMode.OneEuro => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.OneEuroMinCutoff),
                0.1, 20.0),
            _ => Math.Clamp(
                _effectiveSettings.Effective(AdaptationKnob.Smoothing, DwellContext.Other, _settings.Cursor.OneEuroMinCutoff),
                0.1, 20.0),
        };
    }

    // Called on the dispatcher thread (DwellSettings change or provider.Changed). Just sets a flag — the
    // actual write runs on the gaze thread at the top of the next OnGazePoint, which keeps every
    // _fixationDetector mutation on the one thread that calls Update. Coalescing is a bonus: a burst of
    // Changed events (the controller steps four knobs per tick) costs one apply per gaze frame.
    private void RequestFixationReconfiguration() => _fixationReconfigureRequested = true;

    // Runs on the dispatcher thread (the provider's Changed fires there — controller tick, toolbar reset,
    // per-knob disable). Besides the in-place tolerance apply, an effective SMOOTHING change needs the
    // interpolator REBUILT. Changed is payloadless and fires for every knob's fill (§13.3 item 2), so the
    // request is knob-aware by comparison: only when the live effective parameter differs from the one the
    // filter was last built with. Baseline smoothing edits take the separate CursorSettings path, which
    // rebuilds per gaze frame as before.
    private void OnEffectiveSettingsChanged()
    {
        RequestFixationReconfiguration();
        if (EffectiveSmoothingParameter() is { } parameter
            && BitConverter.DoubleToInt64Bits(parameter) != Interlocked.Read(ref _lastAppliedSmoothingBits))
            _adaptiveRebuildRequested = true;
    }

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
    //
    // The rebuild composes the ACTIVE filter's EFFECTIVE smoothing parameter (§13.3 routing), re-read HERE
    // so a coalesced burst applies the latest value, not the one that set the flag.
    private void ApplyPendingInterpolatorRebuild(long timestampMicroseconds)
    {
        if (!_interpolatorRebuildRequested && !_adaptiveRebuildRequested)
            return;

        // §9.1 rebuild budget: the controller can step the smoothing fill on every 250 ms tick, so an
        // ADAPTIVE rebuild is rate-limited to one per second — a pending request is DEFERRED, not dropped:
        // the next sample after the interval applies the then-current effective value, so the final value
        // always lands. Baseline (settings-slider) rebuilds are exempt and satisfy an outstanding adaptive
        // request too (they apply the effective parameter below); any rebuild that runs resets the clock.
        // A NEGATIVE elapsed is a gaze-clock restart (device reconnect — the same backward jump the
        // telemetry sink's resync guard handles, TelemetryClockRestartTests): resync rather than defer for
        // the length of the old epoch.
        if (!_interpolatorRebuildRequested)
        {
            long elapsed = timestampMicroseconds - _lastAdaptiveRebuildTimestampMicroseconds;
            if (elapsed >= 0 && elapsed < MinAdaptiveRebuildIntervalMicroseconds)
                return;
        }
        _interpolatorRebuildRequested = false;
        _adaptiveRebuildRequested = false;
        _lastAdaptiveRebuildTimestampMicroseconds = timestampMicroseconds;

        double? parameter = EffectiveSmoothingParameter();
        var fresh = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor, parameter),
            _settings.Cursor.MaxGapInterpolationMs);
        fresh.CopyStateFrom(_gapInterpolator);
        fresh.WarmStartFromCurrentState(); // R1: seed the fresh filter — no cursor jump
        _gapInterpolator = fresh;
        InterpolatorRebuildCount++; // test seam — see its doc

        // Publish the applied parameter (the sentinel for null — Direct/no-provider — so a runtime switch
        // into Direct reports null honestly). Then RE-VALIDATE: a provider write that landed between the
        // effective read at the top and this exchange compared against the STALE applied bits and may have
        // skipped its request — e.g. the §8.6 toolbar reset clearing fills while an older fill's rebuild was
        // in flight, which would pin the filter at a stale value with no outstanding request. The write
        // orderings close the window (the dispatcher writes fills before reading bits; the gaze thread
        // exchanges bits before re-reading), so at least one side observes the other; any re-request goes
        // back through the budget gate (deferred, not dropped).
        BeforeSmoothingPublishForTest?.Invoke(); // test seam: simulate a racing fill write in the stale-bits window
        Interlocked.Exchange(
            ref _lastAppliedSmoothingBits,
            parameter is { } applied ? BitConverter.DoubleToInt64Bits(applied) : NoSmoothingBits);
        if (parameter is { } published && EffectiveSmoothingParameter() is { } current && current != published)
            _adaptiveRebuildRequested = true;
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
        ApplyPendingInterpolatorRebuild(gaze.TimestampMicroseconds);

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
