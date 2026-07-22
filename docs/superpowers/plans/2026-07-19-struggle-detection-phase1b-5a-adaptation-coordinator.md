# Struggle Detection — Phase 1b-⑤a AdaptationCoordinator (control brain) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the Core adaptation pieces into the running app: add the `AdaptationCoordinator` (App service) that, on a periodic dispatcher tick, **enable-gates**, drives the `StruggleMonitor` → `AdaptationController` pipeline, and fires the **session-reset triggers** (app-start, resume-from-Pause, gaze-provider disconnect); DI-register the `StruggleMonitor`, `AdaptationController`, and coordinator; start the tick loop in `App.OnStartup`. This is the first slice that makes adaptation actually **run** (and auto-reset) when the user opts in. **Zero behaviour change while `AdaptationSettings.Enabled == false`** (the default): the coordinator's tick short-circuits to no monitor/controller work (§9.1).

**Architecture:** The coordinator's per-tick decision is **pure and headless-testable** (mirrors `ToolbarAutoHideController`): `Tick()` returns a `CoordinatorTickResult` reporting which branch it took, so every decision (disabled / reset-on-disconnect / reset-on-resume / assessed / …) is asserted via the return value — no spies, no new interfaces. The tick **clock** is the latest gaze `TimestampMicroseconds` (tracked via `GazeSampled`), because the monitor filters evidence against telemetry timestamps and those carry a provider-specific epoch (the Tobii device clock is **not** wall-clock — a `DateTimeOffset.UtcNow` `now` would empty the window on the real device). The `DispatcherTimer` + `GazeSampled` subscription live in `Start()` (App runtime only); the pure logic (`OnGazeSample`/`Tick`/`Reset`) is exercised directly by tests.

**Tech Stack:** C# / .NET 10; `EyeCtrl.App` (WPF); xUnit v3 + NFluent (`Check.That(...)`). Headless App-service tests (no STA/WPF), the `ToolbarAutoHideController` test tier.

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §4 (`AdaptationCoordinator` in the App layer), §7.2 (momentary tick), §8.6 (always-reachable escape — `Reset()`), §9 (single serialized/dispatcher thread; aggregate on the gaze thread), §9.1 (disabled = current cost: the monitor isn't fed, no work), §13.1 (session boundaries: app-start / resume-from-Pause / gaze-idle) — plus the **③c F3 carry-forward** (idle detection is quality-stream-only, so reset on provider-disconnect).

**Depends on:** **Phase 1a + 1b-①/②/③(all)/④(all) implemented.** From those slices: `BufferingTelemetrySink` (Core, DI-registered by **1a-②**), `EffectiveSettingsProvider` (DI-registered by **1b-①**), `StruggleMonitor(BufferingTelemetrySink, EyeCareSettings, …)` with `Assess(long) → StruggleAssessment` + `ResetSession(long)` (**1b-③c**), `AdaptationController(EffectiveSettingsProvider, AdaptationSettings)` with `Tick(StruggleAssessment, long)` + `Reset()` (**1b-④b**), `StruggleAssessment` + `StruggleAssessment.None` (**1b-③a**), `AdaptationKnob`/`DwellOutcome`/`OutcomeKind`/`AbandonReason`/`DwellContext` (**1a-①/1b-④a**). Written against the **post-1a/1b-①..④** source.

**Executor pre-flight (do this FIRST — MANDATORY; the upstream slices are plans-only):** confirm against the *merged* source:
1. `BufferingTelemetrySink` is DI-registered in `AppBootstrapper` (1a-②) as the concrete type, resolvable via `GetRequiredService<BufferingTelemetrySink>()`, with a `BufferingTelemetrySink(int capacity)` ctor + `Report(in DwellOutcome)`. **If 1a-② registered it under an interface only, register/resolve accordingly.**
2. `EffectiveSettingsProvider` is DI-registered in `AppBootstrapper` (1b-①, `services.AddSingleton(sp => new EffectiveSettingsProvider(sp.GetRequiredService<EyeCtrlSettings>().Adaptation))`), resolvable via `GetRequiredService<EffectiveSettingsProvider>()`.
3. `StruggleMonitor(BufferingTelemetrySink sink, EyeCareSettings eyeCare, …)` + `StruggleAssessment Assess(long nowMicroseconds)` + `void ResetSession(long nowMicroseconds)` (1b-③c).
4. `AdaptationController(EffectiveSettingsProvider provider, AdaptationSettings adaptation)` + `void Tick(StruggleAssessment, long nowMicroseconds)` + `void Reset()` (1b-④b).
5. `StruggleAssessment` is a reference type with a static `None` (1b-③a); `DwellOutcome` positional shape `(long, DwellContext, string, OutcomeKind, AbandonReason, double, PhysicalPoint GazePoint, PhysicalPoint TargetCenter, double, PhysicalRect?)` (1a-①) — used only in the Task-1 integration test.
6. **Pre-existing (verified this session):** `IGazeProvider` (`EyeCtrl.Core.Interfaces`) has `bool IsConnected { get; }`, `event Action<GazePoint>? GazePointReceived`, `void Start()/Stop()`, `: IDisposable`, with `Capabilities`/`TrackStatusReceived`/`TryGetLatestStatus` as **default-implemented** members (a fake need not implement them) (`IGazeProvider.cs:9-55`); `GazeEngine` (`EyeCtrl.Core`) exposes `event Action<GazePoint>? GazeSampled` (used at `App.xaml.cs:219,237`) and `InteractionManager InteractionManager { get; }`; `InteractionManager` (`EyeCtrl.Core.Processing`) has a parameterless ctor, `InteractionState CurrentState { get; }` (starts `FreeLook`), and `TogglePause()` (FreeLook↔Paused); `InteractionState.Paused` (`EyeCtrl.Core.Models`); `GazePoint.TimestampMicroseconds` (`long`); `EyeCtrlSettings.Adaptation`/`.EyeCare`; `AppBootstrapper.BuildServiceProvider(...)` uses `services.AddSingleton(sp => new X(...))` factory lambdas and `App.OnStartup` resolves + starts services after `gazeEngine.Start()` (`App.xaml.cs:209`, eye-care wiring `:236-245`).

If any differ, adjust before proceeding.

**Docs / warnings note:** `EyeCtrl.App` does not enforce CS1591 as strictly as Core, but the coordinator's public members are documented anyway (it is a public service). A test fake's `IGazeProvider.GazePointReceived` is implemented with **explicit no-op accessors** to avoid a `CS0067` "event never used" warning.

---

## Scope boundary (explicit — deferred pieces land in 1b-⑤b)

This slice is the **control brain** only. Deferred to **1b-⑤b** (the feedback & manual-escape surface):
- **The cue overlay** (§10 subtle corner badge, intensity from the assessment) — it reads `coordinator.LastAssessment`.
- **The manual toolbar/tray reset** (§8.6 — a `ResetAdaptationMessage` whose handler calls `coordinator.Reset()`, plus the toolbar button + tray item that send it). `Reset()` is **public here** so ⑤b only adds the senders + subscription.
- **The drift report** (§7.1 "report" — a user-visible note that calibration may have drifted; calibration is NEVER auto-touched, §8.5).
- **The adaptation log** (§10 `adaptation-log.jsonl` / Serilog action log).

Other deferrals (unchanged from the ④ slices):
- **Only `FixationRadius` (tolerance) routes to a consumer today** (via `GazeEngine`, 1b-①). The coordinator drives the whole `StruggleMonitor`→`AdaptationController` pipeline, but `DwellTime`/`Smoothing`/`TargetGeometry` deltas remain stored no-ops until their routing slices. So the only *observable* effect of ⑤a is adaptive fixation tolerance.
- **Tick cadence** (`TickIntervalMs = 250`, ~4 Hz) is a tunable constant. Only the cheap fixation-detector rebuild is routed today (the expensive smoothing-filter rebuild + its batch-coalescing land with smoothing routing — see 1b-④b's perf note); so a few-Hz tick is within budget (§9.1).
- **Fatigue relay is NOT ⑤'s concern** — plan 1a-④ adds `EyeCareService.FatigueScore` + the gaze-quality-aggregator → sink relay, so the fatigue signal already reaches the monitor. ⑤a touches no EyeCare wiring.

---

## File Structure

**Create (App):**
- `src/EyeCtrl.App/Services/CoordinatorTickResult.cs` — the enum `Tick()` returns.
- `src/EyeCtrl.App/Services/AdaptationCoordinator.cs` — the coordinator (pure per-tick logic + `Start`/`Dispose` runtime wiring).

**Modify (App):**
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — register `StruggleMonitor`, `AdaptationController`, `AdaptationCoordinator`.
- `src/EyeCtrl.App/App.xaml.cs` — resolve + `Start(gazeEngine)` the coordinator in `OnStartup` (after `gazeEngine.Start()`).

**Test (create):**
- `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs` — headless decision-outcome tests + one integration test.

**Zero-behaviour-change guarantee:** the coordinator's `Tick()` returns `Disabled` (and does no monitor/controller work) whenever `AdaptationSettings.Enabled == false` (the default). The DI registrations add inert singletons; the `Start()` call begins a timer that, while disabled, only evaluates the enable-gate. Nothing changes the pipeline's behaviour until the user opts in.

---

## Task 1: `AdaptationCoordinator` + `CoordinatorTickResult` (the headless control brain)

**Files:**
- Create: `src/EyeCtrl.App/Services/CoordinatorTickResult.cs`, `src/EyeCtrl.App/Services/AdaptationCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationCoordinatorTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // Minimal IGazeProvider fake: only IsConnected is exercised. GazePointReceived uses explicit no-op
    // accessors so there is no backing field (avoids CS0067 "event never used"). The Capabilities /
    // TrackStatusReceived / TryGetLatestStatus members are default-implemented on the interface.
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public bool IsConnected { get; set; } = true;
        event Action<GazePoint>? IGazeProvider.GazePointReceived { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    // Builds the coordinator over REAL Core pieces (headless — no WPF): a real sink/monitor/provider/
    // controller, a fake gaze provider, and a real interaction manager. Returns the handles tests poke.
    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink,
                    AdaptationSettings Adaptation, FakeGazeProvider Gaze, InteractionManager Im) Make(bool enabled)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        var gaze = new FakeGazeProvider();
        var im = new InteractionManager();
        var coord = new AdaptationCoordinator(monitor, controller, settings.Adaptation, gaze, im);
        return (coord, provider, sink, settings.Adaptation, gaze, im);
    }

    private static DwellOutcome Abandon(long ts, double progress) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
        new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30, null);

    [Fact]
    public void Disabled_SkipsAllWork()
    {
        var (c, _, _, _, _, _) = Make(enabled: false);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disabled);
    }

    [Fact]
    public void FirstEnabledTick_ResetsOnStart()
    {
        var (c, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnStart);
    }

    [Fact]
    public void AwaitingGaze_WhenNoSampleYet()
    {
        var (c, _, _, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnStart); // tick 1: fresh session
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.AwaitingGaze); // tick 2: now == Unseeded (no gaze)
    }

    [Fact]
    public void DisableAfterActive_ClearsOnceThenReportsDisabled()
    {
        var (c, _, _, adaptation, _, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnStart
        c.Tick(); // Assessed
        adaptation.Enabled = false;
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ClearedOnDisable);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disabled);
    }

    [Fact]
    public void ProviderDisconnect_ResetsThenReportsDisconnected()
    {
        var (c, _, _, _, gaze, _) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnStart
        c.Tick(); // Assessed
        gaze.IsConnected = false;
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnDisconnect); // the ③c F3 carry-forward
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Disconnected);
    }

    [Fact]
    public void ResumeFromPause_Resets()
    {
        var (c, _, _, _, _, im) = Make(enabled: true);
        c.OnGazeSample(new GazePoint(0, 0, 10 * Sec, true));
        c.Tick(); // ResetOnStart
        c.Tick(); // Assessed
        im.TogglePause(); // FreeLook → Paused
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Paused);        // into-pause: skip (no reset)
        im.TogglePause(); // Paused → FreeLook (resume)
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnResume); // resume edge: reset + skip
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Assessed);      // back to active
    }

    [Fact]
    public void ActiveTick_AssessesAndAdaptsTolerance()
    {
        var (c, provider, sink, _, _, _) = Make(enabled: true);
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.ResetOnStart); // now == Unseeded → sessionStart = long.MinValue (excludes nothing)

        // Gate SustainFailure: 4 high-progress abandons in one context (abandonment score 0.8, ≥ 4 attempts).
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8));

        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true));
        Check.That(c.Tick()).IsEqualTo(CoordinatorTickResult.Assessed);     // relaxSince anchored at 40 s
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true));
        c.Tick();                                                            // 3 s sustain met → tolerance relaxes one step

        // Tolerance loosened above baseline (the one observably-routed knob).
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
    }

    [Fact]
    public void Reset_ClearsMomentaryAdaptation()
    {
        var (c, provider, sink, _, _, _) = Make(enabled: true);
        c.Tick(); // ResetOnStart
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8));
        c.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); c.Tick();
        c.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); c.Tick();
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        c.Reset(); // the always-reachable escape (§8.6)
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCoordinatorTests"`
Expected: FAIL to **compile** — `AdaptationCoordinator` and `CoordinatorTickResult` do not exist.

- [ ] **Step 3: Create `CoordinatorTickResult`**

Create `src/EyeCtrl.App/Services/CoordinatorTickResult.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>The branch <see cref="AdaptationCoordinator.Tick"/> took this tick (for tests + diagnostics).</summary>
public enum CoordinatorTickResult
{
    /// <summary>Adaptation is disabled; the tick did no monitor/controller work (design §9.1).</summary>
    Disabled,
    /// <summary>Adaptation transitioned enabled→disabled this tick; momentary state was cleared once.</summary>
    ClearedOnDisable,
    /// <summary>First active tick after enable / app-start; a fresh evidence session was started.</summary>
    ResetOnStart,
    /// <summary>The gaze provider went connected→disconnected; the session was reset (1b-③c F3) and the tick skipped.</summary>
    ResetOnDisconnect,
    /// <summary>Interaction resumed from Pause; the session was reset and the tick skipped.</summary>
    ResetOnResume,
    /// <summary>The gaze provider is disconnected; the tick was skipped.</summary>
    Disconnected,
    /// <summary>Interaction is paused; the tick was skipped.</summary>
    Paused,
    /// <summary>No gaze sample has been seen yet; the tick was skipped.</summary>
    AwaitingGaze,
    /// <summary>Struggle was assessed and the controller was driven.</summary>
    Assessed,
}
```

- [ ] **Step 4: Create `AdaptationCoordinator`**

Create `src/EyeCtrl.App/Services/AdaptationCoordinator.cs`:

```csharp
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
    // illegal (CS0677), so accesses go through Volatile.Read/Write (same acquire/release semantics).
    private long _latestGazeTs = Unseeded;

    // Tick-only state (dispatcher thread) — edge detection across ticks.
    private bool _wasEnabled;
    private bool _wasConnected;
    private bool _wasPaused;

    private GazeEngine? _gazeEngine; // retained to unsubscribe in Dispose
    private DispatcherTimer? _timer;

    /// <summary>
    /// The most recent assessment produced by <see cref="Tick"/>, consumed by the cue/report (plan 1b-⑤b).
    /// <see cref="StruggleAssessment.None"/> before the first assessment and after any reset.
    /// </summary>
    public StruggleAssessment LastAssessment { get; private set; } = StruggleAssessment.None;

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
        //   - gaze-provider disconnect edge (1b-③c F3),
        //   - resume-from-Pause edge.
        CoordinatorTickResult? reset =
            !_wasEnabled ? CoordinatorTickResult.ResetOnStart
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

        if (!connected) return CoordinatorTickResult.Disconnected;
        if (paused) return CoordinatorTickResult.Paused;
        if (now == Unseeded) return CoordinatorTickResult.AwaitingGaze;

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
    /// timer. Call once on the UI thread after <c>GazeEngine.Start()</c>. App-runtime wiring only — the tick
    /// logic is exercised headlessly via <see cref="Tick"/>/<see cref="OnGazeSample"/>.
    /// </summary>
    /// <param name="gazeEngine">The gaze engine whose <c>GazeSampled</c> stream drives the tick clock.</param>
    public void Start(GazeEngine gazeEngine)
    {
        _gazeEngine = gazeEngine;
        gazeEngine.GazeSampled += OnGazeSample;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => Tick();

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
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCoordinatorTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/CoordinatorTickResult.cs src/EyeCtrl.App/Services/AdaptationCoordinator.cs tests/EyeCtrl.App.Tests/Services/AdaptationCoordinatorTests.cs
git commit -m "feat(adaptation): add AdaptationCoordinator (enable-gated tick loop + reset triggers)"
```

---

## Task 2: DI registration + `App.OnStartup` wiring

Register the monitor, controller, and coordinator (the sink + provider are already registered by 1a-②/1b-①), and start the coordinator's tick loop after the gaze engine starts.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`, `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register the monitor, controller, and coordinator**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, immediately **before** the `return services.BuildServiceProvider();` line (currently `:234`), add (the file already has `using EyeCtrl.Core.Adaptation;` from 1b-①; `AdaptationCoordinator` is same-namespace):

```csharp
        // Struggle-detection adaptation runtime (Phase 1b). The telemetry sink (1a-②) and the
        // effective-settings provider (1b-①) are already registered above; here we add the monitor,
        // controller, and coordinator. The coordinator ticks the monitor→controller pipeline on a
        // dispatcher timer, enable-gated on AdaptationSettings.Enabled (off by default) — inert until opted in.
        services.AddSingleton(sp => new StruggleMonitor(
            sp.GetRequiredService<BufferingTelemetrySink>(),
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare));
        services.AddSingleton(sp => new AdaptationController(
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation));
        services.AddSingleton(sp => new AdaptationCoordinator(
            sp.GetRequiredService<StruggleMonitor>(),
            sp.GetRequiredService<AdaptationController>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<GazeEngine>().InteractionManager));
```

- [ ] **Step 2: Start the coordinator in `App.OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, in `OnStartup`, immediately **after** the eye-care `GazeSampled` wiring block (the lambda ending at `:245`), add:

```csharp
            // Struggle-detection coordinator: ticks the monitor→controller pipeline on a dispatcher timer,
            // enable-gated on AdaptationSettings.Enabled (off by default). Resolving the singleton and
            // Start()ing it subscribes GazeSampled (the tick clock) + starts the timer; the container holds
            // the instance for the process lifetime (like eyeCare/gazeEngine), so no explicit field is kept.
            _serviceProvider.GetRequiredService<AdaptationCoordinator>().Start(gazeEngine);
```

- [ ] **Step 3: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. The three factory registrations resolve their dependencies (`BufferingTelemetrySink` from 1a-②, `EffectiveSettingsProvider` from 1b-①, `EyeCtrlSettings`/`IGazeProvider`/`GazeEngine` pre-existing); `AdaptationCoordinator.Start(GazeEngine)` matches the call site.

- [ ] **Step 4: Run the headless App + Core suites (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS — `AdaptationCoordinatorTests` (Task 1) plus the existing headless suites. The new registrations are inert singletons; the coordinator's timer only evaluates the enable-gate while `Enabled == false` (the default), so no existing behaviour changes. (Full DI-container resolution of the coordinator is exercised when the app launches — the UI-test tier — since building the container instantiates WPF overlay windows that require STA.)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): register + start the AdaptationCoordinator tick loop"
```

---

## Self-Review

**Spec coverage (this slice):**
- §4 `AdaptationCoordinator` in the App layer, bridging `SINK → SM → AC → ES` into the runtime — the coordinator ticks `StruggleMonitor.Assess` → `AdaptationController.Tick`. ✓
- §7.2 momentary tick on the dispatcher — `DispatcherTimer` at a few Hz calling `Tick()`. ✓
- §8.6 always-reachable escape — `Reset()` is public (the manual toolbar/tray sender is 1b-⑤b). ✓
- §9 single serialized/dispatcher thread; aggregate on the gaze thread — `Tick()` runs on the dispatcher; `OnGazeSample` runs on the gaze thread and only publishes a timestamp via `Volatile`. ✓
- §9.1 disabled = current cost — `Tick()` returns `Disabled` and does no monitor/controller work when `Enabled == false` (the default). ✓
- §13.1 session boundaries — `ResetOnStart` (app-start/enable), `ResetOnResume` (resume-from-Pause). ✓
- **③c F3 carry-forward** — `ResetOnDisconnect` on the `IsConnected` true→false edge (idle detection is quality-stream-only). ✓
- **Clock correctness** — `now` is the latest gaze `TimestampMicroseconds`, matching the telemetry clock (the monitor filters against telemetry timestamps; the Tobii device epoch is not wall-clock). ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `AdaptationCoordinator(StruggleMonitor, AdaptationController, AdaptationSettings, IGazeProvider, InteractionManager)`, `OnGazeSample(GazePoint)`, `Tick() → CoordinatorTickResult`, `Reset()`, `LastAssessment`, `Start(GazeEngine)` are used identically in the tests, the DI registration, and the `OnStartup` call. `StruggleMonitor.Assess(long)`/`ResetSession(long)`, `AdaptationController.Tick(StruggleAssessment, long)`/`Reset()`, `EffectiveSettingsProvider.Effective(AdaptationKnob, DwellContext, double)`, `StruggleAssessment.None`, `BufferingTelemetrySink(int)`/`Report(in DwellOutcome)` match the committed upstream plans (pre-flight-confirmed). `GazeEngine.GazeSampled`/`InteractionManager`, `IGazeProvider.IsConnected`, `InteractionManager.CurrentState`/`TogglePause()`, `InteractionState.Paused`, `GazePoint.TimestampMicroseconds` match source.

**Verified facts relied on (from source, this session):**
- `IGazeProvider` DIM members (`Capabilities`/`TrackStatusReceived`/`TryGetLatestStatus`) let the test fake implement only `IsConnected`/`GazePointReceived`/`Start`/`Stop`/`Dispose` (`IGazeProvider.cs:9-55`); `GazePointReceived` via explicit no-op accessors avoids CS0067.
- `MouseGazeProvider` stamps `DateTimeOffset.UtcNow` µs (`MouseGazeProvider.cs:53`); `TobiiStreamEngineProvider` stamps device monotonic µs from an arbitrary epoch (`TobiiStreamEngineProvider.cs:176-188`) — so `now` MUST be the gaze/telemetry clock, not wall-clock. This is the load-bearing reason for the `GazeSampled`-derived `now`.
- `App.OnStartup` resolves + starts services after `gazeEngine.Start()` (`App.xaml.cs:209`) and subscribes `gazeEngine.GazeSampled` at `:219,237`; `AppBootstrapper` uses `AddSingleton(sp => new X(...))` factory lambdas (`:142`, `:210-232`); `EyeCtrlSettings` is the root singleton with sub-settings via properties (`:59`).
- `ScrollWorker` is the `DispatcherTimer`-owning-service precedent (Start/Stop/Dispose, `Tick += OnTick`) (`ScrollWorker.cs:28-32,100-104`); `ToolbarAutoHideController` is the headless-service test tier (fed synthetic samples + timestamps, `Check.That`) and the `long.MinValue` sentinel discipline (`ToolbarAutoHideController.cs:44-50`).

**Honest testability limits:** the coordinator's decision logic is fully headless-unit-tested (8 tests) — every `CoordinatorTickResult` branch (disabled / clear-on-disable / reset-on-start / reset-on-disconnect+disconnected / paused+reset-on-resume / awaiting-gaze), plus one integration test driving a real sink→monitor→controller→provider through to an observable tolerance change and its `Reset()`. What is **build-verified only** (not headlessly unit-tested), correctly per the App test tiers: `Start()`'s `DispatcherTimer` + `GazeSampled` subscription (the timer fires only under a running dispatcher) and the full DI-container resolution (building the container instantiates STA WPF overlays). The tick interval (250 ms) is a tunable starting value.

**Deferred (documented above, not gaps):** the cue overlay, manual toolbar/tray reset, drift report, and adaptation log (1b-⑤b — they consume `LastAssessment` / `Reset()`); observable routing of dwell/smoothing/geometry (their routing slices); sensitive-context suppression (1b-⑥).
