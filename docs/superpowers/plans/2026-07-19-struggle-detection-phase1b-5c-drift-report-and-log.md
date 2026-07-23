# Struggle Detection — Phase 1b-⑤c Drift Report & Adaptation Log — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the adaptation feedback surface with the two reviewable-record pieces: a **structured Serilog adaptation log** (logged on gated-mode transitions, coarse-marker in sensitive contexts) and a **report-only drift notice** — a lightweight, non-interactive, auto-fading banner shown when cross-context drift is detected, suggesting the user consider re-calibrating and **never touching calibration** (§8.5). Both subscribe to the coordinator's per-tick `AssessmentUpdated` event (added in 1b-⑤b) and consult the single `IFeedbackGate` seam (1b-⑤b) so 1b-⑥ suppresses them in sensitive contexts with no further change.

**Architecture:** Two small App services mirroring 1b-⑤b's cue service: each has a `Start(AdaptationCoordinator)` that subscribes to `AssessmentUpdated`, and an `OnAssessment(StruggleAssessment)` that returns the decision it took (an enum / bool) so the logic is **headless-testable** without a live dispatcher. `AdaptationLog` tracks the set of gated `FailureMode`s and logs (via static `Serilog.Log`, the established service pattern) only when that set **changes** — an "adaptation action" boundary — since the finer per-knob `{oldEffective, newEffective}` detail isn't available (`EffectiveSettingsProvider.Changed` is payloadless and the 1b-④b controller exposes no change-feed; that + the JSONL/log-review surface are Phase 2). `DriftNoticeService` shows the drift banner once per drift-gated episode. The banner (`DriftNoticeWindow`) is a WPF `OverlayWindow` — click-through, capture-excluded, no dwell targets — so it adds **zero gaze mis-activation surface** (unlike the interactive `EyeCareBannerWindow`, which was considered and rejected for that reason).

**Tech Stack:** C# / .NET 10; `EyeCtrl.App` (WPF); Serilog (already the app's logging facility); xUnit v3 + NFluent (`Check.That(...)`). Headless App-service tests for the decision logic; the banner window is build-verified.

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §10 (session log: append-only, structured, size-capped, coarse `"adaptation suppressed (sensitive)"` marker in sensitive contexts — the JSONL file + log-review are Phase 2), §7.1 (drift → looser tolerance + **report**; report only, calibration never auto-touched), §8.5 (calibration off-limits — detect + report + tolerance compensation only), §8.10 (no feedback leakage — the single suppression seam), §9.1 (I/O off the hot path — log on transitions, not per tick), §13.1 (drift = ±30°-coherent `NearMiss` bias across ≥ 2 contexts). Deferred to **1b-⑥**: the real `IFeedbackGate` (UIA sensitive-context detector).

**Depends on:** **Phase 1a + 1b-①/②/③(all)/④(all)/⑤a/⑤b implemented.** From 1b-⑤b: `AdaptationCoordinator.AssessmentUpdated` (`event Action<StruggleAssessment>?`, raised each tick carrying `LastAssessment`), `IFeedbackGate` (`bool IsFeedbackSuppressed`) + its DI registration, the cue service's `Start(AdaptationCoordinator)` + `OnAssessment`-returns-decision pattern + `CueOverlayWindow` (the DI-registered capture-excluded `OverlayWindow`/presenter precedent), and the `App.OnStartup` block that resolves `var adaptationCoordinator` and starts the cue service + reset handler. From 1b-③a: `StruggleAssessment` (`IReadOnlyList<FailureModeAssessment> Modes`, `double AggregateLevel`, `FailureModeAssessment? ForMode(FailureMode)`, static `None`), `FailureModeAssessment(FailureMode Mode, double Score, double Confidence, bool GatePassed, DwellContext? Context)`, `FailureMode` (incl. `Drift`). From 1b-②/1a-⑤: `AdaptationSettings.Posture` (`AdaptationPosture`). Written against the **post-1a/1b-①..⑤b** source.

**Executor pre-flight (do this FIRST — MANDATORY; the struggle-detection slices are plans-only):** confirm against the *merged* source:
1. **1b-⑤b is merged:** `AdaptationCoordinator` (`src/EyeCtrl.App/Services/AdaptationCoordinator.cs`, namespace `EyeCtrl.App.Services`) exposes `public event Action<StruggleAssessment>? AssessmentUpdated;`. `IFeedbackGate { bool IsFeedbackSuppressed { get; } }` + `AllowAllFeedbackGate` exist in `EyeCtrl.App.Services` and `IFeedbackGate` is DI-registered. The cue service (`AdaptationCueService`) exists with `public void Start(AdaptationCoordinator)` + a `public XxxState OnAssessment(StruggleAssessment)` returning its decision (mirror this shape). `App.OnStartup` contains, after the ⑤a `AdaptationCoordinator.Start(gazeEngine)` line, a block resolving `var adaptationCoordinator = _serviceProvider.GetRequiredService<AdaptationCoordinator>();` and starting the cue service + reset handler — **⑤c inserts adjacent to that, reusing the `adaptationCoordinator` local**. `AppBootstrapper.BuildServiceProvider` has the ⑤b feedback registrations (`IFeedbackGate`, `CueOverlayWindow`, `ICuePresenter`, `AdaptationCueService`, `AdaptationResetHandler`) immediately before `return services.BuildServiceProvider();`. If ⑤b placed anything differently, insert relative to whatever it merged.
2. **1b-③a types:** `StruggleAssessment` reference type — `IReadOnlyList<FailureModeAssessment> Modes`, `double AggregateLevel`, `FailureModeAssessment? ForMode(FailureMode)`, static `None`. `FailureModeAssessment` = `readonly record struct (FailureMode Mode, double Score, double Confidence, bool GatePassed, DwellContext? Context)`. `FailureMode` enum includes `Drift`. (Tests construct assessments directly via these.)
3. **`AdaptationSettings.Posture`** (`EyeCtrl.Core.Settings`, 1b-②/1a-⑤): typed `AdaptationPosture` (enum `Conservative`/`Balanced`/`Responsive`), reachable as `EyeCtrlSettings.Adaptation.Posture`.
4. **Serilog is the app logger:** static `Serilog.Log.Information(...)` is used directly in services (e.g. `TrayIconManager.cs:87` `Log.Warning(...)`, `using Serilog;`); `Log.Logger` is configured in `LoggingInitializer` (`AppBootstrapper.cs:52` bridges MEL → Serilog). In headless tests `Log.Logger` is the default silent logger, so a `Log.Information(...)` call is a harmless no-op — the tested surface is `OnAssessment`'s **return value**, not the sink output.
5. **Pre-existing overlay infra (verified against HEAD this session):** `OverlayWindow` (`src/EyeCtrl.App/Overlays/OverlayWindow.cs:7,32,42,44-54,63`) — base `Window`, `init`-only `IsClickThrough`/`ExcludeFromScreenCapture`, `OnSourceInitialized` applies click-through + `WDA_EXCLUDEFROMCAPTURE`, `public void RaiseToTop()`. `CueOverlayWindow` (1b-⑤b) is the sibling precedent: sets both init props in the ctor before `InitializeComponent()`, DI-registered as a singleton, injected as a presenter interface. `SystemParameters.WorkArea` (DIPs) positioning precedent: `PauseIndicatorWindow.SetEdge` / `ToolbarWindow.SetMode`.

If any differ, adjust before proceeding.

**Docs / warnings note:** `EyeCtrl.App` does not enforce CS1591 as strictly as Core, but every public member added here is documented anyway. WPF SDK auto-globs `*.xaml` as `Page`, so `DriftNoticeWindow.xaml` needs no `.csproj` edit.

---

## Scope boundary (explicit — deferred pieces noted where they land)

This slice completes the 1b feedback surface's record/notice half. Deferred:
- **The `adaptation-log.jsonl` file + log-review UI** (§10) → **Phase 2.** This slice logs to the existing rolling Serilog file. The structured entries here are the substrate a Phase-2 JSONL sink / review tool can build on.
- **Per-knob `{knob, oldEffective, newEffective}` log detail** (§10 schema) → **Phase 2.** `EffectiveSettingsProvider.Changed` is a payloadless `event Action?` (1b-①), and the 1b-④b controller exposes no per-knob change-feed, so the finest granularity available now is the gated-mode-set transition (logged here). Adding a controller change-feed is out of scope.
- **The real sensitive-context gate** — this slice consults `IFeedbackGate` (the no-op `AllowAllFeedbackGate` until 1b-⑥); the UIA `ISensitiveContextDetector`-backed gate is **1b-⑥**. When it lands, the log switches to the coarse marker and the drift banner is suppressed with no change here.
- **Caregiver alert** (§10, on sustained struggle / poor tracking) → **Phase 3** (net-new toast/supervisor channel; not this slice).
- **Drift vector/magnitude in the notice** — `FailureModeAssessment` carries no `NearMiss` vector (it stays inside the Drift scorer, 1b-③b), so the notice is a generic "may have drifted" suggestion, which is all a report-only note needs.

---

## File Structure

**Create (App):**
- `src/EyeCtrl.App/Services/AdaptationLogAction.cs` — the enum `AdaptationLog.OnAssessment` returns.
- `src/EyeCtrl.App/Services/AdaptationLog.cs` — logs gated-mode transitions (full / coarse marker) via Serilog.
- `src/EyeCtrl.App/Services/IDriftNoticePresenter.cs` — the drift-notice rendering seam.
- `src/EyeCtrl.App/Services/DriftNoticeService.cs` — once-per-episode, gate-checked drift notice + log.
- `src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml` + `.xaml.cs` — the click-through, capture-excluded, auto-fading banner implementing `IDriftNoticePresenter`.

**Modify:**
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` + `src/EyeCtrl.App/App.xaml.cs` — DI-register + start the log + drift services.

**Test (create):**
- `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs`
- `tests/EyeCtrl.App.Tests/Services/DriftNoticeServiceTests.cs`

**Zero-behaviour-change guarantee:** both services subscribe to an event that only fires while the coordinator is actively assessing (which requires `AdaptationSettings.Enabled`, off by default); when disabled the coordinator reports `StruggleAssessment.None`, so there are no gated modes → no log transitions and no drift gate → no banner. The static `Serilog.Log` calls add rolling-file lines only on genuine adaptation transitions. The banner window is constructed-but-hidden until first shown. Nothing touches the gaze hot path.

---

## Task 1: Adaptation log (`AdaptationLogAction` + `AdaptationLog`)

Log a structured entry on each **change to the set of gated failure modes** — the coarsest available proxy for an adaptation action — with a coarse marker in sensitive contexts.

**Files:**
- Create: `src/EyeCtrl.App/Services/AdaptationLogAction.cs`, `src/EyeCtrl.App/Services/AdaptationLog.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationLogTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    // Builds an assessment whose only gated mode(s) are the ones passed (Score/Confidence are irrelevant
    // to the log, which keys off GatePassed). An empty list = no struggle (idle).
    private static StruggleAssessment Gated(params FailureMode[] modes)
    {
        var list = new FailureModeAssessment[modes.Length];
        for (int i = 0; i < modes.Length; i++)
            list[i] = new FailureModeAssessment(modes[i], Score: 0.7, Confidence: 1.0, GatePassed: true, Context: null);
        double aggregate = modes.Length == 0 ? 0.0 : 0.7;
        return new StruggleAssessment(list, aggregate);
    }

    private static (AdaptationLog Log, AdaptationSettings Adaptation, FakeFeedbackGate Gate) Make()
    {
        var settings = new EyeCtrlSettings();
        var gate = new FakeFeedbackGate();
        return (new AdaptationLog(settings.Adaptation, gate), settings.Adaptation, gate);
    }

    [Fact]
    public void IdleTicks_LogNothing()
    {
        var (log, _, _) = Make();
        Check.That(log.OnAssessment(StruggleAssessment.None)).IsEqualTo(AdaptationLogAction.None);
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.None); // still no gated modes
    }

    [Fact]
    public void StruggleOnset_IsLogged()
    {
        var (log, _, _) = Make();
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure))).IsEqualTo(AdaptationLogAction.Logged);
    }

    [Fact]
    public void SameGatedSet_IsNotReLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure)))               // unchanged set
            .IsEqualTo(AdaptationLogAction.None);
    }

    [Fact]
    public void AddingAMode_IsLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure, FailureMode.Drift)))
            .IsEqualTo(AdaptationLogAction.Logged);                                   // set grew
    }

    [Fact]
    public void StruggleClearing_IsLogged()
    {
        var (log, _, _) = Make();
        log.OnAssessment(Gated(FailureMode.SustainFailure));                          // Logged
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.Logged);  // back to empty = a transition
        Check.That(log.OnAssessment(Gated())).IsEqualTo(AdaptationLogAction.None);    // stays empty
    }

    [Fact]
    public void Transition_InSensitiveContext_LogsCoarseMarkerOnly()
    {
        var (log, _, gate) = Make();
        gate.IsFeedbackSuppressed = true;
        Check.That(log.OnAssessment(Gated(FailureMode.SustainFailure)))
            .IsEqualTo(AdaptationLogAction.SuppressedMarker);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationLogTests"`
Expected: FAIL to **compile** — `AdaptationLog` / `AdaptationLogAction` do not exist.

- [ ] **Step 3: Create `AdaptationLogAction`**

Create `src/EyeCtrl.App/Services/AdaptationLogAction.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>What <see cref="AdaptationLog.OnAssessment"/> did for one assessment (for tests + diagnostics).</summary>
public enum AdaptationLogAction
{
    /// <summary>The gated-failure-mode set was unchanged since the last assessment; nothing was logged.</summary>
    None,
    /// <summary>The gated-mode set changed and a full structured entry was logged.</summary>
    Logged,
    /// <summary>The set changed in a sensitive context; only the coarse suppression marker was logged (§8.10/§10).</summary>
    SuppressedMarker,
}
```

- [ ] **Step 4: Create `AdaptationLog`**

Create `src/EyeCtrl.App/Services/AdaptationLog.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The struggle-adaptation session log (design §10). Subscribes to <see cref="AdaptationCoordinator"/>'s
/// per-tick <c>AssessmentUpdated</c> event and writes a structured Serilog entry whenever the set of
/// <em>gated</em> failure modes changes — the coarsest adaptation-action boundary available (per-knob
/// deltas aren't observable: the provider's <c>Changed</c> event is payloadless and the controller exposes
/// no change-feed; finer detail + the JSONL file/review are Phase 2). Logging on transitions (not per tick)
/// keeps I/O off the hot path (§9.1). In a sensitive context the entry is replaced by a coarse
/// <c>"adaptation suppressed (sensitive context)"</c> marker (§8.10/§10). The decision
/// (<see cref="OnAssessment"/>) is pure and headless-testable via its <see cref="AdaptationLogAction"/>
/// return; only <see cref="Start"/>'s subscription is runtime. No explicit <c>Enabled</c> check is needed:
/// while adaptation is disabled the coordinator reports <see cref="StruggleAssessment.None"/> (no gated
/// modes ⇒ no transitions ⇒ silent).
/// </summary>
public sealed class AdaptationLog : IDisposable
{
    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly HashSet<FailureMode> _loggedGatedModes = new();
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the log over its posture source and the suppression seam.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Posture"/> is recorded in each entry.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, only the coarse marker is logged.</param>
    public AdaptationLog(AdaptationSettings adaptation, IFeedbackGate feedbackGate)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
    }

    /// <summary>The set of failure modes whose confidence gate is met in the given assessment.</summary>
    /// <param name="assessment">The assessment to inspect.</param>
    /// <returns>The gated modes (empty when there is no gated struggle).</returns>
    public static IReadOnlySet<FailureMode> GatedModes(StruggleAssessment assessment)
    {
        var set = new HashSet<FailureMode>();
        foreach (var mode in assessment.Modes)
            if (mode.GatePassed)
                set.Add(mode.Mode);
        return set;
    }

    /// <summary>
    /// Logs a transition entry if the gated-mode set changed since the last assessment; otherwise no-op.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>Which logging action was taken.</returns>
    public AdaptationLogAction OnAssessment(StruggleAssessment assessment)
    {
        IReadOnlySet<FailureMode> current = GatedModes(assessment);
        if (_loggedGatedModes.SetEquals(current))
            return AdaptationLogAction.None;

        _loggedGatedModes.Clear();
        _loggedGatedModes.UnionWith(current);

        if (_feedbackGate.IsFeedbackSuppressed)
        {
            Log.Information("Adaptation suppressed (sensitive context)");
            return AdaptationLogAction.SuppressedMarker;
        }

        Log.Information(
            "Adaptation: struggle {AggregateLevel:F2}, gated modes [{Modes}], posture {Posture}",
            assessment.AggregateLevel,
            string.Join(", ", current.OrderBy(m => m)),
            _adaptation.Posture);
        return AdaptationLogAction.Logged;
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the log.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>Unsubscribes from the coordinator's assessment stream.</summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationLogTests"`
Expected: PASS (6 tests). The `Serilog.Log.Information` calls are silent no-ops under the default test `Log.Logger`.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AdaptationLogAction.cs src/EyeCtrl.App/Services/AdaptationLog.cs tests/EyeCtrl.App.Tests/Services/AdaptationLogTests.cs
git commit -m "feat(adaptation): add AdaptationLog (gated-mode transition logging, headless)"
```

---

## Task 2: Drift notice logic (`IDriftNoticePresenter` + `DriftNoticeService`)

Show a report-only drift notice once per drift-gated episode, gated by `IFeedbackGate`. Never touches calibration.

**Files:**
- Create: `src/EyeCtrl.App/Services/IDriftNoticePresenter.cs`, `src/EyeCtrl.App/Services/DriftNoticeService.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/DriftNoticeServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/DriftNoticeServiceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using NFluent;

public class DriftNoticeServiceTests
{
    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    private sealed class FakeDriftNoticePresenter : IDriftNoticePresenter
    {
        public int ShowCalls { get; private set; }
        public void ShowDriftNotice() => ShowCalls++;
    }

    // Assessment with Drift gated (or not) — the service keys off ForMode(Drift).GatePassed.
    private static StruggleAssessment Drift(bool gated) => new(
        new[] { new FailureModeAssessment(FailureMode.Drift, Score: 0.6, Confidence: 1.0, GatePassed: gated, Context: null) },
        gated ? 0.6 : 0.0);

    private static StruggleAssessment NoDrift() => StruggleAssessment.None;

    private static (DriftNoticeService Svc, FakeFeedbackGate Gate, FakeDriftNoticePresenter Presenter) Make()
    {
        var gate = new FakeFeedbackGate();
        var presenter = new FakeDriftNoticePresenter();
        return (new DriftNoticeService(gate, presenter), gate, presenter);
    }

    [Fact]
    public void DriftGateOnset_ShowsNoticeOnce()
    {
        var (svc, _, presenter) = Make();
        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue();
        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void SustainedDriftGate_DoesNotReShow()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(Drift(gated: true));   // shows
        svc.OnAssessment(Drift(gated: true));   // same episode
        svc.OnAssessment(Drift(gated: true));
        Check.That(presenter.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void NewDriftEpisode_ShowsAgain()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(Drift(gated: true));   // episode 1 → shows
        svc.OnAssessment(Drift(gated: false));  // episode ends (re-arm)
        Check.That(svc.OnAssessment(Drift(gated: true))).IsTrue(); // episode 2 → shows again
        Check.That(presenter.ShowCalls).IsEqualTo(2);
    }

    [Fact]
    public void SensitiveContext_SuppressesTheNotice()
    {
        var (svc, gate, presenter) = Make();
        gate.IsFeedbackSuppressed = true;
        Check.That(svc.OnAssessment(Drift(gated: true))).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void NoDrift_ShowsNothing()
    {
        var (svc, _, presenter) = Make();
        svc.OnAssessment(NoDrift());
        svc.OnAssessment(Drift(gated: false));
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DriftNoticeServiceTests"`
Expected: FAIL to **compile** — `IDriftNoticePresenter` / `DriftNoticeService` do not exist.

- [ ] **Step 3: Create `IDriftNoticePresenter`**

Create `src/EyeCtrl.App/Services/IDriftNoticePresenter.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// Renders the report-only drift notice. Separate from <see cref="DriftNoticeService"/> so the service's
/// once-per-episode/gating decision is headless-testable with a fake; the WPF <c>DriftNoticeWindow</c> is
/// the runtime implementation. The notice is informational only — it never modifies calibration (§8.5).
/// </summary>
public interface IDriftNoticePresenter
{
    /// <summary>Shows the auto-fading "tracking may have drifted — consider re-calibrating" banner.</summary>
    void ShowDriftNotice();
}
```

- [ ] **Step 4: Create `DriftNoticeService`**

Create `src/EyeCtrl.App/Services/DriftNoticeService.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using Serilog;

/// <summary>
/// Surfaces the drift "report" (design §7.1): when the monitor gates the <see cref="FailureMode.Drift"/>
/// mode (a cross-context coherent near-miss bias, §13.1), shows a report-only notice suggesting the user
/// consider re-calibrating — **calibration is never auto-touched** (§8.5). Rate-limited to once per
/// drift-gated episode (re-armed when Drift clears) and suppressed in sensitive contexts (§8.10). Subscribes
/// to <see cref="AdaptationCoordinator"/>'s per-tick <c>AssessmentUpdated</c> event; the decision
/// (<see cref="OnAssessment"/>) is pure and headless-testable via its <c>bool</c> return.
/// </summary>
public sealed class DriftNoticeService : IDisposable
{
    private readonly IFeedbackGate _feedbackGate;
    private readonly IDriftNoticePresenter _presenter;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose
    private bool _driftEpisodeActive;

    /// <summary>Initializes the service over the suppression seam and the notice presenter.</summary>
    /// <param name="feedbackGate">The suppression seam (§8.10); when suppressing, no visible notice is shown.</param>
    /// <param name="presenter">The notice renderer (the WPF banner at runtime, a fake in tests).</param>
    public DriftNoticeService(IFeedbackGate feedbackGate, IDriftNoticePresenter presenter)
    {
        _feedbackGate = feedbackGate;
        _presenter = presenter;
    }

    /// <summary>
    /// Shows the drift notice on the leading edge of a drift-gated episode (once per episode), unless
    /// feedback is suppressed. Re-arms when Drift is no longer gated.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>True if a notice was shown this tick.</returns>
    public bool OnAssessment(StruggleAssessment assessment)
    {
        bool driftGated = assessment.ForMode(FailureMode.Drift) is { GatePassed: true };

        if (!driftGated)
        {
            _driftEpisodeActive = false; // re-arm for the next drift episode
            return false;
        }

        if (_driftEpisodeActive)
            return false; // already handled this episode

        // Mark the episode handled regardless of suppression, so a later suppression change can't pop
        // the notice late. Consequence: if feedback is suppressed at the drift-gate ONSET, this episode
        // shows NO notice at all — even if suppression lifts mid-episode — which is the §8.10-faithful
        // trade (never leak feedback in a sensitive context; adaptation still runs silently). A fresh
        // drift episode (after Drift clears and re-gates) re-arms and can show again.
        _driftEpisodeActive = true;
        if (_feedbackGate.IsFeedbackSuppressed)
            return false;

        _presenter.ShowDriftNotice();
        Log.Information("Adaptation: drift detected — suggested re-calibration (calibration not modified, §8.5)");
        return true;
    }

    /// <summary>Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent.</summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the notice.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return;
        _coordinator = coordinator;
        coordinator.AssessmentUpdated += OnAssessmentEvent;
    }

    private void OnAssessmentEvent(StruggleAssessment assessment) => OnAssessment(assessment);

    /// <summary>Unsubscribes from the coordinator's assessment stream.</summary>
    public void Dispose()
    {
        if (_coordinator is not null)
        {
            _coordinator.AssessmentUpdated -= OnAssessmentEvent;
            _coordinator = null;
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DriftNoticeServiceTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/IDriftNoticePresenter.cs src/EyeCtrl.App/Services/DriftNoticeService.cs tests/EyeCtrl.App.Tests/Services/DriftNoticeServiceTests.cs
git commit -m "feat(adaptation): add DriftNoticeService (once-per-episode report-only drift notice, headless)"
```

---

## Task 3: `DriftNoticeWindow` (the auto-fading banner)

The runtime `IDriftNoticePresenter`: a transparent, click-through, capture-excluded banner at bottom-center that holds ~8 s then fades out. **No dwell targets** (non-interactive). Build-verified.

**Files:**
- Create: `src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml`, `src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml.cs`

- [ ] **Step 1: Create the XAML shell**

Create `src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml` (transparent `OverlayWindow`, fixed size — no `SizeToContent`, so positioning has concrete `Width`/`Height`; a centered dark banner with the notice text):

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.DriftNoticeWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        AutomationProperties.AutomationId="DriftNotice_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="560" Height="72">
    <Grid>
        <Border x:Name="Banner"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                Background="#E0202020" CornerRadius="6" Padding="18,10" Opacity="0">
            <TextBlock x:Name="Message"
                       AutomationProperties.AutomationId="DriftNotice_Message"
                       Foreground="White" FontSize="18" TextAlignment="Center"
                       Text="Tracking may have drifted — consider re-calibrating"/>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Create the code-behind**

Create `src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.App.Services;

/// <summary>
/// The report-only drift notice (design §7.1/§8.5): a bottom-center banner suggesting re-calibration that
/// holds ~8 s then fades out. It is click-through and <see cref="OverlayWindow.ExcludeFromScreenCapture"/>
/// =true, so it never intercepts gaze/mouse (no dwell targets — zero mis-activation surface) and is not
/// baked into the magnifier / action-menu capture. It only ever *shows a message* — it never modifies
/// calibration.
/// </summary>
public partial class DriftNoticeWindow : OverlayWindow, IDriftNoticePresenter
{
    private const double MarginDip = 48;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(8);
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private DispatcherTimer? _hideTimer;
    private bool _positioned;

    /// <summary>Creates the drift-notice window (click-through + excluded from screen capture, set before init).</summary>
    public DriftNoticeWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void ShowDriftNotice()
    {
        EnsurePositioned();
        // Cancel any in-flight fade and re-show at full opacity. Unlike CueOverlayWindow's per-tick
        // re-flash (which needs a _fadeGeneration guard), a fade here is started ONLY by the hold-timer
        // tick, and removing a clock via BeginAnimation(dp, null) does not raise the removed clock's
        // Completed — so a re-show during a fade can't leave a stale Completed to hide the banner. No guard needed.
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Banner.Opacity = 1.0;
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        if (_hideTimer is null)
        {
            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = HoldTime };
            _hideTimer.Tick += OnHideTick;
        }
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Hide();
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width/Height
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - MarginDip;
        _positioned = true;
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. `DriftNoticeWindow` resolves `OverlayWindow`, `IDriftNoticePresenter` (`EyeCtrl.App.Services`), and the generated `Banner` field; the `.xaml` is auto-included as a `Page`.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml.cs
git commit -m "feat(adaptation): add DriftNoticeWindow (non-interactive auto-fading drift banner)"
```

---

## Task 4: DI registration + `App.OnStartup` wiring

Register the log + drift services (+ the banner window) and start them after the 1b-⑤b cue service / reset handler.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`, `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register the log + drift services**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, immediately **after** the 1b-⑤b feedback registrations (and before `return services.BuildServiceProvider();`), add:

```csharp
        // 1b-⑤c drift report + adaptation log. Both subscribe to the coordinator's AssessmentUpdated event
        // and consult the same IFeedbackGate (1b-⑤b). The drift banner is excluded from magnifier capture
        // and non-interactive; both are started in App.OnStartup after the cue service.
        services.AddSingleton(sp => new AdaptationLog(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>()));
        services.AddSingleton<DriftNoticeWindow>();
        services.AddSingleton<IDriftNoticePresenter>(sp => sp.GetRequiredService<DriftNoticeWindow>());
        services.AddSingleton(sp => new DriftNoticeService(
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<IDriftNoticePresenter>()));
```

(No new `using`s: `EyeCtrl.App.Overlays` (for `DriftNoticeWindow`) and `EyeCtrl.Core.Settings` (for `EyeCtrlSettings`) are already imported; the feedback types are same-namespace `EyeCtrl.App.Services`.)

- [ ] **Step 2: Start the services in `App.OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, immediately **after** the 1b-⑤b lines that start the cue service + reset handler (which resolved `var adaptationCoordinator`), add:

```csharp
            // 1b-⑤c: start the drift report + adaptation log (same coordinator event as the cue).
            _serviceProvider.GetRequiredService<AdaptationLog>().Start(adaptationCoordinator);
            _serviceProvider.GetRequiredService<DriftNoticeService>().Start(adaptationCoordinator);
```

(Reuses the `adaptationCoordinator` local introduced by 1b-⑤b in the same `OnStartup` scope. If ⑤b did not keep that local, resolve it again: `var adaptationCoordinator = _serviceProvider.GetRequiredService<AdaptationCoordinator>();` — same singleton.)

- [ ] **Step 3: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. The registrations resolve their dependencies (`EyeCtrlSettings`/`IFeedbackGate` pre-existing/⑤b; `IDriftNoticePresenter`/`DriftNoticeWindow` from Tasks 2–3); `AdaptationLog.Start`/`DriftNoticeService.Start` match the call sites.

- [ ] **Step 4: Run the headless App + Core suites (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS — `AdaptationLogTests` (6) + `DriftNoticeServiceTests` (5) + the existing headless suites. The new singletons are inert until the coordinator reports gated struggle (which needs `AdaptationSettings.Enabled`, off by default), so no existing behaviour changes. (Full DI resolution of the banner window + the running services is exercised only when the app launches — the UI-test tier.)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): register + start the adaptation log and drift notice services"
```

---

## Self-Review

**Spec coverage (this slice):**
- §10 session log — `AdaptationLog` writes structured Serilog entries (aggregate level, gated modes, posture) on gated-mode transitions, with the coarse `"adaptation suppressed (sensitive context)"` marker when the gate suppresses. The `adaptation-log.jsonl` file + per-knob `{oldEffective,newEffective}` detail + log-review are explicitly deferred to Phase 2 (documented — the provider `Changed` event is payloadless and the controller has no change-feed). ✓
- §7.1 drift → report — `DriftNoticeService` surfaces a user-visible note when Drift is gated; **report only**. ✓
- §8.5 calibration off-limits — the notice only shows a message; nothing in this slice reads or writes calibration. ✓ (asserted by construction + documented on `IDriftNoticePresenter`/`DriftNoticeWindow`.)
- §8.10 no feedback leakage — both services consult the single `IFeedbackGate`; the log emits the coarse marker and the drift notice is skipped when suppressing; 1b-⑥ swaps the gate. ✓
- §9.1 I/O off the hot path — the log writes only on gated-mode transitions (a handful per session), from the dispatcher thread (never the 30–60 Hz gaze poll thread), via the existing synchronous rolling file sink (`LoggingInitializer.cs:25-29`); the drift notice fires at most once per episode. The spec's async/batched `adaptation-log.jsonl` writes are the Phase-2 upgrade; the interim safety rests on the transitions-only + dispatcher-thread argument, not on sink buffering. ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `AdaptationLogAction` (None/Logged/SuppressedMarker), `AdaptationLog(AdaptationSettings, IFeedbackGate)` + `GatedModes(StruggleAssessment) → IReadOnlySet<FailureMode>` + `OnAssessment(StruggleAssessment) → AdaptationLogAction` + `Start(AdaptationCoordinator)`, `IDriftNoticePresenter.ShowDriftNotice()`, `DriftNoticeService(IFeedbackGate, IDriftNoticePresenter)` + `OnAssessment(StruggleAssessment) → bool` + `Start(AdaptationCoordinator)`, and `DriftNoticeWindow : OverlayWindow, IDriftNoticePresenter` are used identically across the definitions, tests, DI registrations, and `OnStartup`. `AdaptationCoordinator.AssessmentUpdated`, `StruggleAssessment.Modes`/`AggregateLevel`/`ForMode`/`None`, `FailureModeAssessment(Mode,Score,Confidence,GatePassed,Context)`, `FailureMode.Drift`, `IFeedbackGate.IsFeedbackSuppressed`, and `AdaptationSettings.Posture` match the pre-flight-confirmed 1b-⑤b/③a/② surfaces.

**Verified facts relied on (from source, this session):**
- `EffectiveSettingsProvider.Changed` is `public event Action? Changed;` (payloadless) — 1b-① plan `:225`, invoked `Changed?.Invoke()` — so per-knob `{knob,old,new}` logging isn't derivable from it; the gated-mode-set transition is the available granularity.
- `EyeCareBannerWindow.ShowAsync` wires two gaze **dwell** targets and returns `Task<bool>` (`EyeCareBannerWindow.xaml.cs:44-66`) — i.e. it is an interactive accept/snooze prompt; rejected for a report-only notice (would add mis-activation surface + eye-care coupling), hence the dedicated non-interactive `DriftNoticeWindow`.
- Serilog is used via static `Serilog.Log` in services (`TrayIconManager.cs:87`, `using Serilog;`); MEL→Serilog bridge at `AppBootstrapper.cs:52`. In tests the default `Log.Logger` makes `Log.Information` a silent no-op, so `OnAssessment` is tested via its return value.
- `OverlayWindow` init-prop + capture-exclusion + `RaiseToTop` surface (`OverlayWindow.cs:32,42,44-54,63`); the 1b-⑤b `CueOverlayWindow` is the DI-registered capture-excluded presenter precedent.

**Honest testability limits:** headless-unit-tested (App-service tier, no STA) — `AdaptationLog.OnAssessment` (6 tests: idle-silence, onset/add/clear transitions logged, sustained-set not re-logged, sensitive→coarse marker) and `DriftNoticeService.OnAssessment` (5 tests: onset shows once, sustained no-reshow, new episode re-shows, sensitive suppresses, never-drift silent), both over fake `IFeedbackGate`/`IDriftNoticePresenter` and directly-constructed assessments. **Build-verified only** (correctly per the App test tiers): the `DriftNoticeWindow` (transparent shell, capture exclusion, bottom-center positioning, the hold-timer + fade animation run under a dispatcher/STA); the `Start` subscription wiring; the static `Serilog.Log` output format; and the full DI-container resolution (instantiates STA WPF overlays). The banner text, hold (8 s) / fade (1 s) durations, bottom-center placement, and font size are tunable starting values.

**Deferred (documented above, not gaps):** the `adaptation-log.jsonl` file + per-knob detail + log-review (Phase 2); the real sensitive-context `IFeedbackGate` (1b-⑥); the caregiver alert (Phase 3); drift vector/magnitude in the notice (not carried by `FailureModeAssessment`).
