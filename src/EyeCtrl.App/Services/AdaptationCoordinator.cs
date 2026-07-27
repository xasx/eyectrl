namespace EyeCtrl.App.Services;

using System;
using System.Threading;
using System.Windows.Threading;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The struggle-adaptation coordinator (design §4): bridges the Core adaptation pieces into the running
/// app. On a periodic dispatcher tick it enable-gates (§9.1), drives the <see cref="StruggleMonitor"/> →
/// <see cref="AdaptationController"/> pipeline, and fires the session-reset triggers — app-start,
/// resume-from-Pause, and gaze-provider disconnect (the latter because idle detection is quality-stream-only
/// and goes blind on a disconnect — 1b-③c F3). The per-tick decision (<see cref="Tick"/>) is pure and
/// headless-testable via its <see cref="CoordinatorTickResult"/> return; the <see cref="DispatcherTimer"/>
/// and <c>GazeSampled</c> subscription are wired in <see cref="Start"/> (App runtime only).
/// </summary>
public sealed class AdaptationCoordinator : IDisposable
{
    // Tick cadence: a few Hz on the dispatcher (§9). Tunable. Only FixationRadius is routed today (a cheap
    // fixation-detector rebuild); the expensive smoothing-filter rebuild + its coalescing land with
    // smoothing routing (see 1b-④b's perf note), so a few-Hz tick is within the §9.1 budget.
    private const int TickIntervalMs = 250;

    // "No gaze sample seen yet." long.MinValue, NOT 0 — real tracker timestamps carry an arbitrary large
    // epoch (Tobii device µs) and 0 is a legitimate value, so 0 must not be overloaded as a sentinel
    // (mirrors ToolbarAutoHideController's Unseeded discipline).
    private const long Unseeded = long.MinValue;

    private readonly StruggleMonitor _monitor;
    private readonly AdaptationController _controller;
    private readonly AdaptationSettings _adaptation;
    private readonly IGazeProvider _gazeProvider;
    private readonly InteractionManager _interactionManager;

    // Written on the gaze thread (OnGazeSample), read on the dispatcher thread (Tick). `volatile long` is
    // illegal (CS0677), so accesses go through Volatile.Read/Write (same acquire/release semantics). The app
    // targets win-x64, where aligned 64-bit reads/writes are atomic (no torn read) — Volatile adds only the
    // memory fence; a 32-bit target would need Interlocked for atomicity.
    private long _latestGazeTs = Unseeded;

    // Tick-only state (dispatcher thread) — edge detection across ticks.
    private bool _wasEnabled;
    private bool _wasConnected;
    private bool _wasPaused;

    private GazeEngine? _gazeEngine; // retained to unsubscribe in Dispose
    private DispatcherTimer? _timer;

    // True once a None assessment has been raised, so runs of Nones collapse to their first. Starts true:
    // subscribers begin in their own "nothing to show" state (cue hidden, drift armed, log empty), which a
    // leading None would only restate.
    private bool _lastRaisedWasNone = true;

    /// <summary>
    /// The current assessment for the cue/report (plan 1b-⑤b): the one from the most recent tick that
    /// actually assessed (<see cref="CoordinatorTickResult.Assessed"/>), or <see cref="StruggleAssessment.None"/>
    /// whenever the last tick did NOT assess — disabled, paused, disconnected, awaiting-gaze, or a reset. So
    /// the cue reflects only *live* struggle and fades on pause/disconnect without 1b-⑤b having to gate away
    /// a stale value.
    /// </summary>
    public StruggleAssessment LastAssessment { get; private set; } = StruggleAssessment.None;

    /// <summary>
    /// Raised once per timer tick (App runtime), carrying <see cref="LastAssessment"/> — the current
    /// assessment, or <see cref="StruggleAssessment.None"/> whenever the tick did not assess (disabled,
    /// paused, disconnected, awaiting-gaze, or a reset). The 1b-⑤b/⑤c feedback surface subscribes to this
    /// single per-tick signal on the coordinator's cadence; because it carries None on non-assessing
    /// ticks, the cue fades naturally without a subscriber having to gate away a stale value.
    /// </summary>
    public event Action<StruggleAssessment>? AssessmentUpdated;

    /// <summary>Initializes the coordinator over the Core adaptation pieces and the runtime signals it gates on.</summary>
    /// <param name="monitor">The struggle monitor, assessed each active tick.</param>
    /// <param name="controller">The adaptation controller, driven each active tick.</param>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> gates the whole tick loop (§9.1).</param>
    /// <param name="gazeProvider">The gaze provider; its <see cref="IGazeProvider.IsConnected"/> is polled to detect disconnect.</param>
    /// <param name="interactionManager">The interaction state machine; its <see cref="InteractionManager.CurrentState"/> is polled to detect pause/resume.</param>
    public AdaptationCoordinator(
        StruggleMonitor monitor,
        AdaptationController controller,
        AdaptationSettings adaptation,
        IGazeProvider gazeProvider,
        InteractionManager interactionManager)
    {
        _monitor = monitor;
        _controller = controller;
        _adaptation = adaptation;
        _gazeProvider = gazeProvider;
        _interactionManager = interactionManager;
    }

    /// <summary>
    /// Records the latest gaze timestamp as the tick clock (see <see cref="Tick"/>). Subscribed to
    /// <c>GazeEngine.GazeSampled</c>, so it runs on the gaze-provider thread; the write is published via
    /// <see cref="Volatile"/> for the dispatcher-thread read in <see cref="Tick"/>.
    /// </summary>
    /// <param name="sample">The raw gaze sample; only its timestamp is used.</param>
    public void OnGazeSample(GazePoint sample) => Volatile.Write(ref _latestGazeTs, sample.TimestampMicroseconds);

    /// <summary>
    /// Advances the coordinator one tick (called on the dispatcher thread by the timer). Enable-gates,
    /// fires the session-reset triggers on their edges, and — when active — assesses struggle and drives
    /// the controller with the latest gaze timestamp as <c>now</c>.
    /// </summary>
    /// <returns>Which branch was taken this tick.</returns>
    public CoordinatorTickResult Tick()
    {
        // Enable-gate (§9.1: disabled ⇒ no monitor/controller work). Tear down once on the enabled→disabled edge.
        if (!_adaptation.Enabled)
        {
            if (_wasEnabled)
            {
                _wasEnabled = false;
                ResetInternal(Volatile.Read(ref _latestGazeTs));
                return CoordinatorTickResult.ClearedOnDisable;
            }
            return CoordinatorTickResult.Disabled;
        }

        long now = Volatile.Read(ref _latestGazeTs);
        bool connected = _gazeProvider.IsConnected;
        bool paused = _interactionManager.CurrentState == InteractionState.Paused;

        // Session-reset triggers (each: reset a fresh session + clear momentary, then skip this tick):
        //   - first active tick after enable / app-start,
        //   - gaze-provider disconnect EDGE (1b-③c F3 — telemetry goes blind, so reset the moment it drops),
        //   - resume-from-Pause EDGE (deliberately the RESUME edge, not the pause-ENTRY edge: a pause is a
        //     deliberate user action and evidence gathered during it is meaningless, so a fresh session at
        //     resume is both correct per §13.1 and cleaner than resetting on entry — unlike disconnect,
        //     which must reset immediately because idle detection can no longer see the stall).
        CoordinatorTickResult? reset =
            !_wasEnabled ? CoordinatorTickResult.ResetOnEnable
            : _wasConnected && !connected ? CoordinatorTickResult.ResetOnDisconnect
            : _wasPaused && !paused ? CoordinatorTickResult.ResetOnResume
            : null;

        _wasEnabled = true;
        _wasConnected = connected;
        _wasPaused = paused;

        if (reset is { } reason)
        {
            ResetInternal(now);
            return reason;
        }

        // Not actively adapting → there is no current assessment, so clear it (the cue fades, §10) rather
        // than leave a stale pre-skip value that 1b-⑤b's cue would otherwise have to gate away.
        if (!connected) { LastAssessment = StruggleAssessment.None; return CoordinatorTickResult.Disconnected; }
        if (paused) { LastAssessment = StruggleAssessment.None; return CoordinatorTickResult.Paused; }
        if (now == Unseeded) { LastAssessment = StruggleAssessment.None; return CoordinatorTickResult.AwaitingGaze; }

        StruggleAssessment assessment = _monitor.Assess(now);
        _controller.Tick(assessment, now);
        LastAssessment = assessment;
        return CoordinatorTickResult.Assessed;
    }

    /// <summary>
    /// Clears all momentary adaptation and starts a fresh evidence session — the always-reachable escape
    /// (§8.6). Invoked by the reset triggers and by the manual toolbar/tray reset (wired in plan 1b-⑤b).
    /// </summary>
    public void Reset() => ResetInternal(Volatile.Read(ref _latestGazeTs));

    // Moves the monitor's session boundary to `now` and clears the controller's momentary deltas.
    private void ResetInternal(long now)
    {
        _monitor.ResetSession(now);
        _controller.Reset();
        LastAssessment = StruggleAssessment.None;
    }

    /// <summary>
    /// Starts the runtime tick loop: subscribes to gaze samples (the tick clock) and starts the dispatcher
    /// timer. Call once on the UI thread after <c>GazeEngine.Start()</c>; idempotent (a second call is a
    /// no-op, so it never double-subscribes or leaks a second timer). The timer + subscription are created
    /// here rather than in the constructor so headless tests — which never call <see cref="Start"/> — don't
    /// construct a <see cref="DispatcherTimer"/>; the tick logic itself is exercised via
    /// <see cref="Tick"/>/<see cref="OnGazeSample"/>.
    /// </summary>
    /// <param name="gazeEngine">The gaze engine whose <c>GazeSampled</c> stream drives the tick clock.</param>
    public void Start(GazeEngine gazeEngine)
    {
        if (_timer is not null)
            return; // already started
        _gazeEngine = gazeEngine;
        gazeEngine.GazeSampled += OnGazeSample;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => RunTick();

    /// <summary>
    /// One timer iteration: the guarded <see cref="Tick"/> plus the guarded, None-coalesced
    /// <see cref="AssessmentUpdated"/> raise. Internal rather than private so the crash-containment and
    /// coalescing behaviour is headless-testable without a <see cref="DispatcherTimer"/>.
    /// </summary>
    internal void RunTick()
    {
        // The whole tick is guarded. App.OnStartup deliberately does NOT set DispatcherUnhandledException's
        // Handled flag ("log + let the process crash so the supervisor's respawn owns recovery"), so ANY
        // throw escaping here takes the process down — and for a user whose sole input channel is this app,
        // that is a loss of control until the supervisor respawns. AdaptationController.Tick has its own §11
        // catch, but Assess and the AssessmentUpdated fan-out did not: the subscribers do WPF window work
        // (cue overlay, drift banner) and Serilog file I/O, both of which can fail — during shutdown, or on
        // a full disk. Adaptation is a comfort feature; it must never be able to end the session.
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Adaptation coordinator tick failed; skipping this tick.");
            LastAssessment = StruggleAssessment.None;
        }

        // Coalesce runs of "nothing to say". Every non-None assessment is still raised individually — the
        // subscribers each decide what a repeat means (AdaptationCueService throttles its re-flash to the
        // badge's fade duration, the drift notice is once per episode, the log fires on gated-mode
        // transitions) — but consecutive Nones are collapsed to the first, which is the one that takes the
        // cue down, re-arms the drift notice, and logs the transition. Without this the disabled-by-default
        // case ran the whole fan-out at 4 Hz forever.
        bool isNone = IsNone(LastAssessment);
        if (isNone && _lastRaisedWasNone)
            return;
        _lastRaisedWasNone = isNone;

        // Raised outside the try above but guarded separately, so one faulting subscriber cannot suppress
        // the others' notification or the tick itself.
        try
        {
            AssessmentUpdated?.Invoke(LastAssessment);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An adaptation assessment subscriber threw; feedback for this tick was skipped.");
        }
    }

    private static bool IsNone(StruggleAssessment assessment) =>
        ReferenceEquals(assessment, StruggleAssessment.None)
        || (assessment.Modes.Count == 0 && assessment.AggregateLevel <= 0.0);

    /// <summary>Stops the tick loop and unsubscribes from gaze samples.</summary>
    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }
        if (_gazeEngine is not null)
        {
            _gazeEngine.GazeSampled -= OnGazeSample;
            _gazeEngine = null;
        }
    }
}
