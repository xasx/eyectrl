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
    private readonly IStruggleTelemetrySink? _telemetry;
    // The offer lifecycle's Information lines (ticket 8 fixup, review B F2) — the live check reads the
    // run's constants and the offer events from the log. Null in tests.
    private readonly ILogger<GazeEngine>? _logger;
    private readonly PropertyChangedEventHandler _dwellSettingsChangedHandler;
    private readonly Action? _effectiveSettingsChangedHandler;
    // Set from the dispatcher thread (FreeLook entry, SuppressFixation release) and read+cleared on the
    // gaze thread before the detector is fed. Zeroing the age on those edges is what stops pent-up age
    // — time the eyes spent inside another overlay or another state — from firing an activation the
    // moment the gate lifts (skeptic condition 2).
    private volatile bool _fixationAgeResetRequested;
    // The narrower re-arm (age + latches), set only on Paused → FreeLook and on the SuppressFixation
    // true→false edge. It is what lets held gaze re-show the button after a short pause or a suppressed
    // stretch; every other FreeLook entry deliberately keeps the latches (fixup-02 R2-1 — a fixed exit
    // button under an unmoved gaze would otherwise re-open the overlay in a loop).
    private volatile bool _fixationRearmRequested;
    // Whether that pending re-arm must also END the run whose onset was reported. Set only by the
    // SuppressFixation release edge: the interaction state is still Fixation there, and ending the run is
    // the only thing that can unstrand it (fixup-02 N1). The Paused → FreeLook edge leaves it false —
    // that state already left Fixation, so an end there would be a spurious transition.
    private volatile bool _fixationRearmEndsReportedRun;
    private readonly Action<InteractionState> _interactionStateChangedHandler;
    // StateChanged carries only the new state; the re-arm needs to know whether FreeLook came from
    // Paused. Transitions are raised by the state machine's own callers (dispatcher for pause/menu,
    // gaze thread for fixation end), and a torn read at worst mis-classifies one edge, which the next
    // FreeLook corrects.
    private InteractionState _previousInteractionState = InteractionState.FreeLook;
    // The activation offer's geometry, pushed by the app (screen bounds and scale are app knowledge —
    // Core owns no screen API). The rebuild runs on the gaze thread like every other live-pipeline
    // mutation: ConfigureActivationOffers only records the request.
    private sealed record ActivationOfferGeometry(PhysicalRect ScreenBoundsPx, double Scale);
    private volatile ActivationOfferGeometry? _activationOfferGeometry;
    private volatile bool _activationOfferRebuildRequested;
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
    private volatile Action<GazeFrame>[] _cursorMovedHandlers = [];
    private readonly object _handlerLock = new();
    private readonly HandlerFaultReporter _faults;

    // Consecutive faults escaping ProcessGazePoint (the per-sample pipeline body). The per-sample
    // fault containment in OnGazePoint keeps the stream alive across them; these two fields exist
    // to escalate once per episode when the fault is SUSTAINED — every sample rejected for ~5s at
    // 30Hz — which is the "app alive, cursor dead" state nothing else detects (samples are
    // published before the faulting step, so the heartbeat stays fresh).
    private int _consecutiveSampleFaults;
    private bool _sustainedSampleFaultEpisodeLogged;
    private const int SustainedSampleFaultThreshold = 150;

    // The activation surface's telemetry target id. Kept identical to the one the ViewModel's DwellDetector
    // used, so recorded outcomes stay comparable across the ticket-3 handover.
    private const string ActivationTargetId = "activate";

    /// <summary>Interaction state machine controlling fixation → activation → action flow.</summary>
    public InteractionManager InteractionManager { get; }
    private volatile bool _suppressFixation;

    /// <summary>When true, fixation detection is suppressed (e.g., during overlay input or dwell cooldown).</summary>
    public bool SuppressFixation
    {
        get => _suppressFixation;
        set
        {
            // Releasing the gate ends the run whose onset was reported — the interaction state is still
            // Fixation, and only an end can return it to FreeLook (fixup-02 N1) — and re-arms (age +
            // latches) so the user gets a deliberate fresh onset rather than an activation inherited from
            // before the gate. Time accumulated before the overlay opened cannot complete an activation
            // on the first sample after it closes (skeptic condition 2).
            if (_suppressFixation && !value)
            {
                _fixationAgeResetRequested = true;
                _fixationRearmRequested = true;
                _fixationRearmEndsReportedRun = true;
            }
            if (!_suppressFixation && value)
            {
                // Every suppression owner is a teardown — the keyboard toggle, external dwell and
                // calibration all take this edge — so the offer goes with them. Requested, applied by
                // the gaze thread; the pipeline keeps running while suppressed even though the
                // detector is not fed, so the clear lands on the next sample.
                InteractionManager.RequestActivationOfferClear();
            }
            _suppressFixation = value;
        }
    }
    /// <summary>Stable centroid position in physical screen pixels.</summary>
    public PhysicalPoint? StableCentroid => _centroidTracker.StableCentroid;
    /// <summary>Gaze-speed standard deviation (px/s) of the last *completed* fixation — a jitter/shakiness
    /// proxy latched at fixation end (design §6.2). Persists between fixations.</summary>
    public double LastFixationJitter => _centroidTracker.LastCompletedSpeedStdDev;
    /// <summary>Fired when cursor moves, carrying the per-sample <see cref="GazeFrame"/> (filtered
    /// position in physical screen pixels, timestamp, and whether the position is gap-held).
    /// <para>
    /// Handlers run on the gaze thread and must be cheap and non-blocking, as with
    /// <see cref="GazeSampled"/>. A throwing handler is caught and reported rather than aborting the
    /// remaining handlers or the rest of the pipeline. Note this event carries the filtered point and
    /// deliberately does NOT carry validity — consumers needing eyes-lost detection or off-screen
    /// coordinates must use <see cref="GazeSampled"/>. <see cref="GazeFrame.IsInGap"/> is the weaker,
    /// derived signal a dwell surface needs: the point is HELD, so it must not advance dwell.
    /// </para></summary>
    public event Action<GazeFrame>? CursorMoved
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

    /// <summary>Fired once per fixation when the valid-time age reaches the onset age plus the effective
    /// activation dwell, carrying the activation time, the credited age and the running centroid (ticket
    /// 3). This is the Core-published activation signal the app consumes; nothing downstream recomputes
    /// the age. Handlers run on the gaze thread.</summary>
    public event Action<FixationActivation>? FixationActivated;

    /// <summary>Fired when a fixation run whose onset was reported is released, with the valid-time ledger
    /// (last credited sample, its age, the centroid) and the release reason. The replay harness uses it to
    /// compare engine end times against its own runs; struggle telemetry uses it for abandonments.
    /// Handlers run on the gaze thread.</summary>
    public event Action<FixationRelease>? FixationReleased;

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
    /// <param name="telemetry">Optional struggle-telemetry sink. When supplied, the activation surface's
    /// outcomes are reported from here (ticket 3 moved its clock out of the ViewModel's DwellDetector),
    /// with the same valid-time progress the rule publishes. Null disables emission and leaves the hot
    /// path unchanged.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null,
        EffectiveSettingsProvider? effectiveSettings = null,
        ILogger<GazeEngine>? logger = null,
        IStruggleTelemetrySink? telemetry = null)
    {
        _faults = new HandlerFaultReporter(
            (message, ex) => logger?.LogError(ex, "{FaultMessage}", message),
            () => Environment.TickCount64 / 1000,
            windowSeconds: 30);

        _gazeProvider = gazeProvider;
        _inputSimulator = inputSimulator;
        _settings = settings;
        _calibrationService = calibrationService;
        _telemetry = telemetry;
        _logger = logger;

        _effectiveSettings = effectiveSettings;

        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor, EffectiveSmoothingParameter()),
            _settings.Cursor.MaxGapInterpolationMs);

        _cursorSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(CursorSettings.FilterMode)
                               or nameof(CursorSettings.MaxGapInterpolationMs)
                               or nameof(CursorSettings.OneEuroMinCutoff)
                               or nameof(CursorSettings.OneEuroBetaPerDiagonal)
                               or nameof(CursorSettings.SmoothingFactor))
            {
                // Request only — the rebuild itself runs on the gaze thread (ApplyPendingInterpolatorRebuild),
                // because CopyStateFrom reads the OLD interpolator's carried-over position/timestamp, which the
                // gaze thread mutates inside Process. Coalescing is a bonus: dragging a smoothing slider now
                // costs one rebuild per gaze frame instead of one per PropertyChanged tick.
                _interpolatorRebuildRequested = true;
            }

            if (e.PropertyName == nameof(CursorSettings.CircleSize))
            {
                // The ring size is part of the offer geometry (the placement offset follows it), so a
                // ring change rebuilds the offer engine once on the gaze thread. It drops a live offer,
                // which is right: its B was placed for the old ring.
                _activationOfferRebuildRequested = true;
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
        // RadiusPixels/FixationTimeMs/ActivationDwellMs/CooldownMs are settable, so the instance and its
        // subscriptions live for the engine's lifetime and an in-progress fixation survives every change.
        _fixationDetector = new FixationDetector(EffectiveFixationRadius(), settings.Dwell.FixationDetectionMs)
        {
            ActivationDwellMs = EffectiveActivationDwellMs(),
            CooldownMs = settings.Dwell.DwellCooldownMs,
        };
        WireFixation(_fixationDetector);

        // Reconfigure on a baseline tolerance edit. GazeEngine previously subscribed to CursorSettings only,
        // so DwellSettings tolerance edits never reached the live detector (latent bug — now fixed).
        // ActivationButtonDwellMs and DwellCooldownMs join them because the activation clock lives in the
        // detector since ticket 3.
        _dwellSettingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(DwellSettings.FixationRadiusPixels)
                               or nameof(DwellSettings.FixationDetectionMs)
                               or nameof(DwellSettings.ActivationButtonDwellMs)
                               or nameof(DwellSettings.DwellCooldownMs))
                RequestFixationReconfiguration();
        };
        _settings.Dwell.PropertyChanged += _dwellSettingsChangedHandler;

        // FreeLook entry zeroes the run's age (skeptic condition 2). The handler only raises the flags;
        // the apply runs on the gaze thread before the next feed, like every other detector mutation.
        // The LATCHES are cleared only for a resume from Paused: a fixed exit button sits under an
        // unmoved gaze, and re-arming there would re-onset at +300 ms and re-open the overlay in a loop
        // (fixup-02 R2-1). This path re-arms SILENTLY — the state has already left Fixation, so no end is
        // needed (and an end here would read as an abandonment). Suppression release re-arms too, and
        // there the run must be ENDED first (see the SuppressFixation setter and _fixationRearmEndsReportedRun).
        _interactionStateChangedHandler = state =>
        {
            var previous = _previousInteractionState;
            _previousInteractionState = state;
            if (state != InteractionState.FreeLook)
                return;
            _fixationAgeResetRequested = true;
            if (previous == InteractionState.Paused)
                _fixationRearmRequested = true;
        };
        InteractionManager.StateChanged += _interactionStateChangedHandler;

        // Button-dwell telemetry (ticket 8, M7/N8): completions and offers that expired unused. The
        // age-based reports the detector used to feed are retired with _runAccepted — the age signal
        // is replay-only now, and the offer is the one activation ledger.
        InteractionManager.ActivationOfferFired += OnActivationOfferFired;
        InteractionManager.ActivationOfferExpired += OnActivationOfferExpired;

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

    // Ticket 3's effective-dwell wiring, and a deliberate, documented IDwellDetectorRegistry bypass
    // (designer cross-review, skeptic condition 5: "re-home the threshold through the effective-dwell
    // map, or document the opt-out"). The registry's appliers push maps onto DwellDetector TARGETS; the
    // activation clock now lives in FixationDetector, which has no target to push onto. Reading the
    // provider here keeps the fill reaching activation all the same: DwellTimeRouter's map resolves
    // Effective(DwellTime, Other, baseline) — the SAME knob, context and baseline as this read — and the
    // provider's Changed handler above already re-requests the apply, so a controller fill lands on the
    // live detector on the next gaze sample. Context is Other because AdaptationController stores fills
    // under Other; passing ActivationButton would miss every stored fill. When adaptation is off,
    // Effective returns the baseline exactly (the disabled ⇒ effective == baseline invariant).
    private int EffectiveActivationDwellMs()
    {
        double baseline = _settings.Dwell.ActivationButtonDwellMs;
        double effective = _effectiveSettings?.Effective(AdaptationKnob.DwellTime, DwellContext.Other, baseline)
            ?? baseline;
        return (int)Math.Clamp(
            Math.Round(effective),
            AdaptationDwellRanges.StandardMinDwellMs,
            AdaptationDwellRanges.StandardMaxDwellMs);
    }

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

    /// <summary>
    /// The live fixation rule's radius in physical pixels: the adaptation-effective value the detector is
    /// actually running with, not the raw setting. The app reads it at fixation onset to size the drawn
    /// halo, so the hold radius it marks is the radius the dwell is counted within.
    /// </summary>
    public double EffectiveFixationRadiusPixels => _fixationDetector.RadiusPixels;

    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests): the live fixation detector's
    /// configured radius in physical pixels. The units check pins it equal to the setting at ScaleX 2.0 —
    /// the radius is already physical and must never be DPI-converted.</summary>
    internal double FixationRadiusPixelsForTest => EffectiveFixationRadiusPixels;

    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests): the live fixation detector's
    /// effective activation dwell, so the effective-dwell routing can be pinned end-to-end (a provider fill
    /// must reach it). Production reads nothing.</summary>
    internal int ActivationDwellMsForTest => _fixationDetector.ActivationDwellMs;

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

    /// <summary>
    /// Records the activation offer's geometry — the primary screen's bounds in physical pixels and the
    /// live DPI scale, both app knowledge Core has no API for (ticket 8, the plan's "the app pushes the
    /// screen bounds and the scale as plain data"). Callable from the dispatcher; the rebuild that
    /// consumes it runs on the gaze thread at the top of the next sample, like every other live-pipeline
    /// mutation. A later call replaces the geometry and rebuilds again (ring size changes request the
    /// same rebuild through the settings handler).
    /// </summary>
    public void ConfigureActivationOffers(PhysicalRect screenBoundsPx, double scale)
    {
        _activationOfferGeometry = new ActivationOfferGeometry(screenBoundsPx, scale);
        _activationOfferRebuildRequested = true;
    }

    // Runs on the gaze thread. Builds the offer engine for the recorded geometry and attaches it to the
    // InteractionManager, which drives it from then on. Rebuilding drops a live offer — only a ring or
    // screen change does it, and the old B was placed for the old geometry.
    private void ApplyPendingActivationOfferRebuild()
    {
        if (!_activationOfferRebuildRequested)
            return;
        _activationOfferRebuildRequested = false;
        if (_activationOfferGeometry is not { } geometry)
            return;

        var offers = new ActivationOfferEngine(
            geometry.ScreenBoundsPx,
            geometry.Scale,
            _settings.Cursor.CircleSize,
            EffectiveActivationDwellMs(),
            EffectiveFixationRadius())
        {
            // Decision 3's linger was set by the user from the ticket 7 replay report (750 ms), down
            // from the engine's 1 s default. The default is left alone so the report's lever table
            // keeps its baseline.
            OfferExpiryMs = ActivationOfferEngine.ProductOfferExpiryMs,
        };
        InteractionManager.AttachActivationOffers(offers);
        LogOfferEngineConfiguration(offers);
    }

    // The live check's setup-sanity line (review B F2): the constants the product is actually running.
    // Low-rate (configure, ring change, display change, and an effective R/dwell move), so it is not
    // gated behind a diagnostics flag.
    private void LogOfferEngineConfiguration(ActivationOfferEngine offers)
        => _logger?.LogInformation(
            "Offer engine: {Width}×{Height} px @{Scale}, ring {Ring}, R {R}, dwell {Dwell}, linger {Expiry} ms",
            offers.ScreenBoundsPx.Width,
            offers.ScreenBoundsPx.Height,
            offers.Scale,
            offers.RingSizeDip,
            offers.FixationRadiusPx,
            offers.DwellMs,
            offers.OfferExpiryMs);

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
        // The activation dwell DOES flow through the effective-dwell map (EffectiveActivationDwellMs), so
        // adaptation's dwell knob keeps reaching activation; the cooldown is a plain baseline.
        _fixationDetector.RadiusPixels = EffectiveFixationRadius();
        _fixationDetector.FixationTimeMs = _settings.Dwell.FixationDetectionMs;
        _fixationDetector.ActivationDwellMs = EffectiveActivationDwellMs();
        _fixationDetector.CooldownMs = _settings.Dwell.DwellCooldownMs;

        // The offer engine follows the same two live values. They are settable in place (review A's
        // ticket-8 nit): an R or dwell change must not drop a live offer. The live check reads the
        // constants from the log, so a real move logs the new configuration — not every request, which
        // fires on each adaptation fill even when the effective value is unchanged.
        if (InteractionManager.ActivationOffers is { } offers)
        {
            int previousDwellMs = offers.DwellMs;
            double previousRadiusPx = offers.FixationRadiusPx;
            offers.DwellMs = EffectiveActivationDwellMs();
            offers.FixationRadiusPx = EffectiveFixationRadius();
            if (offers.DwellMs != previousDwellMs || offers.FixationRadiusPx != previousRadiusPx)
                LogOfferEngineConfiguration(offers);
        }
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
        d.FixationSettled += InteractionManager.OnFixationSettled;
        d.FixationStarted += _startTrackingHandler;
        d.FixationEnded += _centroidTracker.StopTracking;
        d.FixationActivated += ForwardFixationActivated;
        d.FixationReleased += ForwardFixationReleased;
    }

    private void UnwireFixation(FixationDetector d)
    {
        d.FixationStarted -= InteractionManager.OnFixationStarted;
        d.FixationEnded -= InteractionManager.OnFixationEnded;
        d.FixationSettled -= InteractionManager.OnFixationSettled;
        d.FixationStarted -= _startTrackingHandler;
        d.FixationEnded -= _centroidTracker.StopTracking;
        d.FixationActivated -= ForwardFixationActivated;
        d.FixationReleased -= ForwardFixationReleased;
    }

    // Method groups, not lambdas: UnwireFixation must be able to remove exactly these subscriptions.
    // Ticket 8: these are pure forwarding now — the age-based telemetry that used to hang off them is
    // retired with the offer (M7/N8), and the age signal is replay-only.
    private void ForwardFixationActivated(FixationActivation activation)
        => FixationActivated?.Invoke(activation);

    private void ForwardFixationReleased(FixationRelease release)
        => FixationReleased?.Invoke(release);

    // Ticket 8's button-dwell telemetry, replacing ticket 3's age-based reports in the same change that
    // moved the button's show to the offer. The target geometry is the button's own: TargetCenter = B
    // and TargetRadius = the hit radius, with GazePoint = B as the old activation reports had
    // (NearMiss 0) so adaptation's geometric drives see the same shape they always did.
    private void OnActivationOfferFired(ActivationOffer offer, long timestampUs)
    {
        _telemetry?.Report(new DwellOutcome(
            timestampUs,
            DwellContext.ActivationButton,
            ActivationTargetId,
            OutcomeKind.Activated,
            AbandonReason.None,
            1.0,
            offer.Button,
            offer.Button,
            offer.HitRadiusPx,
            TargetBounds: null));
    }

    private void OnActivationOfferExpired(ActivationOffer offer, long timestampUs, double progressAtExpiry)
    {
        // Every expiry is logged for the live check, before the telemetry threshold below: a glance
        // that did not complete is exactly what the "no chase" and completion-count readings need.
        _logger?.LogInformation(
            "Offer expired unused #{Id} (dwell {Progress:P0})", offer.Id, progressAtExpiry);

        // The same threshold the age-based abandonment used: below it the gaze merely passed over the
        // button, not an attempt the user gave up on.
        if (_telemetry is null || progressAtExpiry < TelemetryThresholds.AbandonProgress)
            return;

        _telemetry.Report(new DwellOutcome(
            timestampUs,
            DwellContext.ActivationButton,
            ActivationTargetId,
            OutcomeKind.Abandoned,
            AbandonReason.GazeLeft,
            progressAtExpiry,
            offer.Button,
            offer.Button,
            offer.HitRadiusPx,
            TargetBounds: null));
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

    private void PublishCursorMoved(GazeFrame frame)
    {
        var handlers = _cursorMovedHandlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](frame);
            }
            catch (Exception ex)
            {
                _faults.Report(nameof(CursorMoved), handlers[i], ex);
            }
        }
    }

    private void OnGazePoint(GazePoint gaze)
    {
        // Everything below runs on the gaze thread inside the provider's callback. A throw there
        // used to unwind into the provider — on Tobii across the native dispatch frame — where the
        // poll loop treats it as a DEVICE error: disconnect, backoff, reconnect. A data-dependent
        // fault (a non-finite sample through the total-fail coordinate mapper, zero screen metrics
        // during a display transition, a degenerate calibration transform) then recurred on the
        // first sample of every reconnect: a reconnect storm with a live-looking heartbeat and a
        // dead cursor. Contain it here instead: skip the sample, keep the stream alive, and
        // escalate once when faults are sustained. A skipped sample is invisible (the next one is
        // milliseconds away); a reconnect cycle costs seconds and hides the cause.
        try
        {
            ProcessGazePoint(gaze);
            _consecutiveSampleFaults = 0;
            _sustainedSampleFaultEpisodeLogged = false;
        }
        catch (Exception ex)
        {
            _consecutiveSampleFaults++;
            _faults.Report("gaze sample fault", ex);
            if (_consecutiveSampleFaults >= SustainedSampleFaultThreshold && !_sustainedSampleFaultEpisodeLogged)
            {
                _sustainedSampleFaultEpisodeLogged = true;
                // Constant, per HandlerFaultReporter's contract: the context string doubles as
                // the rate-limit key, so a per-episode interpolation would mint an unbounded
                // dictionary entry set (one per distinct count). The threshold itself carries
                // the magnitude; the exception carries the cause.
                _faults.Report(
                    "gaze pipeline rejecting every sample — cursor output is dead while the process stays alive", ex);
            }
        }
    }

    private void ProcessGazePoint(GazePoint gaze)
    {
        // Deferred reconfiguration (requested on the dispatcher thread) runs here on the gaze thread first,
        // before any _fixationDetector/_centroidTracker/_gapInterpolator use below, so all mutations of the
        // live pipeline state stay on this thread. Each is skipped when nothing was requested.
        ApplyPendingFixationReconfiguration();
        ApplyPendingInterpolatorRebuild(gaze.TimestampMicroseconds);
        // The offer engine before the sample that will drive it: a pending rebuild attaches first, then
        // the volatile teardown/completion requests land, then the sample is fed.
        ApplyPendingActivationOfferRebuild();
        InteractionManager.ApplyPendingActivationOfferRequests();

        // Per-sample snapshot: InteractionManager.CurrentState is also written from the dispatcher thread
        // (TogglePause / OnActionSelected), so re-reading it per decision lets a write land between the
        // reads and makes ONE sample take mutually inconsistent decisions. Every decision below uses this
        // one value.
        var interactionState = InteractionManager.CurrentState;

        // Raw-sample liveness for repeat mode's dwell gate — must precede every
        // calibration/interpolation view of the sample, which all make a lost-eyes gap look
        // like a valid frozen point.
        InteractionManager.NoteGazeSample(gaze.IsValid, gaze.TimestampMicroseconds);

        PublishGazeSampled(gaze);

        var corrected = gaze;
        if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
        {
            var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
            corrected = gaze with { X = p.X, Y = p.Y };
        }
        var (filtered, isInGap) = _gapInterpolator.Process(corrected);

        // Gap-held samples are excluded: the interpolator hands back the frozen last valid position with a
        // real dt, so they enter the tracker as zero-displacement (speed-0) points — deflating
        // LastFixationJitter exactly when tracking is worst, and biasing StableCentroid toward the freeze.
        if (!isInGap)
            _centroidTracker.Update(filtered, gaze.TimestampMicroseconds);

        // Fixation runs FIRST so the frame published below carries THIS sample's activation progress
        // (ticket 3: Core owns the valid-time ledger and publishes the scalar). The gates are unchanged
        // from the pre-ticket-3 order: paused samples never feed the detector, and neither do suppressed
        // ones; an age reset requested by FreeLook entry / SuppressFixation release lands before the feed.
        ApplyPendingFixationAgeReset();
        if (interactionState != InteractionState.Paused && !SuppressFixation && !isInGap)
        {
            // The offer engine first, then the detector: the settle the detector may raise on this
            // sample is classified against the sample the engine has already seen (the engine's
            // documented call order). Both see only non-gap samples; the engine additionally gets the
            // raw validity so a tracker-reported invalid sample pauses the button dwell.
            InteractionManager.NoteActivationGazeSample(filtered, gaze.TimestampMicroseconds, gaze.IsValid);
            _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
        }

        // Always fire CursorMoved so the cursor circle + toolbar receive gaze
        // (this allows unpause via toolbar dwell even when paused). Gap-held samples are published
        // too — display surfaces may keep showing the held point — but the frame says so, and every
        // dwell surface downstream skips its DwellDetector.Update for them.
        PublishCursorMoved(new GazeFrame(
            filtered, gaze.TimestampMicroseconds, isInGap, _fixationDetector.DwellProgress,
            InteractionManager.ActivationOffers?.DwellProgress ?? 0));

        if (interactionState == InteractionState.Paused)
            return;

        // RepeatMode keeps the cursor following gaze (like Dragging): the dwell-to-act click
        // lands at the fixated point, so the user must see where the pointer is.
        if (interactionState is InteractionState.FreeLook
            or InteractionState.Dragging
            or InteractionState.RepeatMode)
        {
            _inputSimulator.MoveCursor(filtered);
        }
    }

    private void ApplyPendingFixationAgeReset()
    {
        if (_fixationRearmRequested)
        {
            var endsReportedRun = _fixationRearmEndsReportedRun;
            _fixationRearmRequested = false;
            _fixationRearmEndsReportedRun = false;
            _fixationAgeResetRequested = false;
            _fixationDetector.Rearm(endsReportedRun);
            return;
        }
        if (!_fixationAgeResetRequested)
            return;
        _fixationAgeResetRequested = false;
        _fixationDetector.ResetAge();
    }

    /// <summary>Unsubscribes from the gaze provider and settings, and disposes the provider.</summary>
    public void Dispose()
    {
        _settings.Cursor.PropertyChanged -= _cursorSettingsChangedHandler;
        _settings.Dwell.PropertyChanged -= _dwellSettingsChangedHandler;
        if (_effectiveSettings is not null && _effectiveSettingsChangedHandler is not null)
            _effectiveSettings.Changed -= _effectiveSettingsChangedHandler;
        InteractionManager.StateChanged -= _interactionStateChangedHandler;
        InteractionManager.ActivationOfferFired -= OnActivationOfferFired;
        InteractionManager.ActivationOfferExpired -= OnActivationOfferExpired;
        _gazeProvider.GazePointReceived -= OnGazePoint;
        UnwireFixation(_fixationDetector);
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
