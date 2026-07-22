# Struggle Detection — Phase 1b-⑤b Feedback Cue & Manual Escape — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first user-facing half of the adaptation feedback surface — a subtle, auto-fading, capture-excluded **cue overlay** whose intensity tracks the live struggle level, and the **always-reachable manual escape** (a toolbar reset button + tray item that clear all adaptation to baseline) — plus the two shared seams the rest of the surface reuses: a per-tick `AssessmentUpdated` event on the `AdaptationCoordinator` and a single `IFeedbackGate` suppression seam. **Zero behaviour change to the gaze pipeline** (§9.1): the cue only appears when the coordinator reports gated struggle (which requires `AdaptationSettings.Enabled`, off by default), and the escape controls are inert no-ops at baseline.

**Architecture:** The coordinator (1b-⑤a) owns the ~4 Hz tick cadence and the `LastAssessment` lifecycle (it clears to `StruggleAssessment.None` on every non-assessing tick). This slice adds a per-tick **push event** so the feedback surface consumes one signal on one cadence rather than polling; because the event carries `None` on non-assessing ticks, the cue fades naturally on pause/disconnect with no stale-value gating. The cue's **decision logic is pure and headless-testable** (`ComputeCueState` + `AdaptationCueService.OnAssessment` over a fake `ICuePresenter`/`IFeedbackGate`, the `ToolbarAutoHideController` test tier); the WPF `CueOverlayWindow` is build-verified. The manual escape is a `ResetAdaptationMessage` whose **dedicated headless-testable handler** (`AdaptationResetHandler`) calls `coordinator.Reset()` — deliberately a small standalone service rather than threading the coordinator through the ~1500-line, ctor-less `EventCoordinator`. `IFeedbackGate` is a no-op seam here (`AllowAllFeedbackGate`); 1b-⑥ replaces its registration with the UIA sensitive-context implementation, gating the cue (and 1b-⑤c's report/log) at one point (§8.10).

**Tech Stack:** C# / .NET 10; `EyeCtrl.App` (WPF), `EyeCtrl.ViewModels`; xUnit v3 + NFluent (`Check.That(...)`). Headless App-service/ViewModel tests (no STA/WPF) for the decision logic; WPF window + toolbar/tray visuals are build-verified.

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §10 (on-screen cue: subtle corner badge, on-canvas, **excluded from magnifier capture**, non-interactive, auto-fading ~2 s, intensity scales with the assessment, gated on `ShowCue`; manual override: toolbar reset button mirrored in the tray), §8.6 (always-reachable escape), §8.10 (no feedback leakage — a single suppression seam), §9.1 (cue is a cheap fading badge; disabled = current cost). Deferred to **1b-⑤c**: the drift report (reuse the EyeCare banner, rate-limited) and the structured Serilog adaptation log; to **1b-⑥**: the real `IFeedbackGate`.

**Depends on:** **Phase 1a + 1b-①/②/③(all)/④(all)/⑤a implemented.** From those slices: `AdaptationCoordinator` (App, `EyeCtrl.App.Services`) with `StruggleAssessment LastAssessment { get; }`, `void Reset()`, `void Start(GazeEngine)`, private `OnTimerTick(object?, EventArgs)`, ctor `(StruggleMonitor, AdaptationController, AdaptationSettings, IGazeProvider, InteractionManager)`, and its DI registration + `App.OnStartup` `.Start(gazeEngine)` line (**1b-⑤a**); `StruggleAssessment` with ctor `(IReadOnlyList<FailureModeAssessment>, double aggregateLevel)`, `double AggregateLevel` (in `[0, 1]`), static `None`, and `FailureModeAssessment` (**1b-③a**); `AdaptationSettings.ShowCue` (bool, default `true`) (**1b-②**); `BufferingTelemetrySink`, `StruggleMonitor`, `AdaptationController`, `EffectiveSettingsProvider`, `AdaptationKnob`, `DwellOutcome`/`OutcomeKind`/`AbandonReason`/`DwellContext` (**1a/1b-①/③/④**) — used only in the Task-4 reset-handler integration test. Written against the **post-1a/1b-①..⑤a** source.

**Executor pre-flight (do this FIRST — MANDATORY; the struggle-detection slices are plans-only, `EyeCtrl.Core.Adaptation` and `AdaptationCoordinator` do not exist in a fresh tree):** confirm against the *merged* source:
1. `AdaptationCoordinator` (`src/EyeCtrl.App/Services/AdaptationCoordinator.cs`, namespace `EyeCtrl.App.Services`, 1b-⑤a): `public StruggleAssessment LastAssessment { get; private set; }`; `public void Reset()`; `public void Start(GazeEngine)`; and **`private void OnTimerTick(object? sender, EventArgs e) => Tick();`** — Task 1 edits exactly this method + adds a field, so confirm the current one-line body. Ctor `(StruggleMonitor, AdaptationController, AdaptationSettings, IGazeProvider, InteractionManager)`; `public CoordinatorTickResult Tick()` and `public void OnGazeSample(GazePoint)` (used by the Task-4 test). If 1b-⑤a review renamed `OnTimerTick` or inlined `Tick`, adapt the Task-1 edit accordingly.
2. `StruggleAssessment` (`EyeCtrl.Core.Adaptation`, 1b-③a): reference type, ctor `(IReadOnlyList<FailureModeAssessment> modes, double aggregateLevel)`, `double AggregateLevel` documented in `[0, 1]`, static `None`. `FailureModeAssessment` is a `readonly record struct` (constructed only in these tests via `Array.Empty<FailureModeAssessment>()`, so its exact fields are not needed here).
3. `AdaptationSettings.ShowCue` (`EyeCtrl.Core.Settings`, 1b-②): `bool`, default `true`, reachable as `EyeCtrlSettings.Adaptation.ShowCue`.
4. **1b-⑤a runtime wiring is present:** `AppBootstrapper.BuildServiceProvider` registers `StruggleMonitor`/`AdaptationController`/`AdaptationCoordinator` via `services.AddSingleton(sp => new X(...))` factory lambdas immediately before `return services.BuildServiceProvider();`, with `using EyeCtrl.Core.Adaptation;` present; and `App.OnStartup` contains `_serviceProvider.GetRequiredService<AdaptationCoordinator>().Start(gazeEngine);` after `gazeEngine.Start()`. Task 5 inserts adjacent to these. `App.xaml.cs` already has `using EyeCtrl.App.Services;` (it references `AdaptationCoordinator`, `TrayIconManager`, `EventCoordinator`). If 1b-⑤a placed these differently, insert relative to whatever it merged.
5. **Pre-existing App source (verified this session against HEAD `254fae1`):**
   - `OverlayWindow` (`src/EyeCtrl.App/Overlays/OverlayWindow.cs:7,32,42,44-54`): `public class OverlayWindow : Window` with **`init`-only** `bool IsClickThrough` / `bool ExcludeFromScreenCapture`; `OnSourceInitialized` applies click-through (`WS_EX_TRANSPARENT` + non-activating) and `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`; `public void RaiseToTop()`. Set the init props in the subclass ctor **before** `InitializeComponent()` (pattern: `CursorCircleWindow.xaml.cs:14-18`).
   - `CursorCircleWindow.xaml` — the transparent `OverlayWindow` shell: `WindowStyle="None" AllowsTransparency="True" Background="Transparent" Topmost="True" ShowInTaskbar="False"` + a `Canvas` with a hidden UIA-anchor `TextBlock` (`CursorCircleWindow.xaml:6-19`).
   - `ScreenCaptureService.CaptureRegion` uses GDI `Graphics.CopyFromScreen` (`ScreenCaptureService.cs:37`), called by the magnifier (`ManualSelectionOverlayWindow.xaml.cs:63`) and action-menu preview (`ActionMenuWindow.xaml.cs:93`) — so the cue window MUST set `ExcludeFromScreenCapture = true` or it is baked into both grabs.
   - `ToolbarViewModel` (`src/EyeCtrl.ViewModels/ToolbarViewModel.cs`): ctor `(EyeCtrlSettings settings, IMessenger messenger)` (**confirm 1a-② did not add a required sink param — it was made optional; the 2-arg call must still compile**); the `(string Id, string Symbol)[]` button array at `:38-47`; the `switch (targetId)` in `protected override void OnTargetActivated(string)` at `:100-112`; `public void SimulateButtonActivated(string) => OnTargetActivated(...)` at `:93`; `Messenger` is the protected property from `DwellOverlayViewModelBase`.
   - `ToolbarWindow.xaml.cs` (`src/EyeCtrl.App/Overlays/`): `public void Initialize(double, int)` at `:33` builds the `(string Id, string Symbol)[]` at `:41-47` and `foreach ... AddButton(id, symbol)` at `:49-50`; `private void AddButton(string id, string symbol)` at `:56`.
   - `TrayIconManager` (`src/EyeCtrl.App/Services/`): ctor `(InteractionManager, Dispatcher, IMessenger messenger, Action shutdownCallback, Action? showToolbarCallback = null)` with fields `_messenger`/`_dispatcher`; the Settings item pattern `settingsItem.Click += (_, _) => dispatcher.BeginInvoke(() => _messenger.Send(new SettingsRequestedMessage()));` at `:43-45`; menu assembly at `:50-64`; `using EyeCtrl.ViewModels.Messages;` present.
   - `InteractionMessages.cs` (`src/EyeCtrl.ViewModels/Messages/`, namespace `EyeCtrl.ViewModels.Messages`): parameterless messages are `public record Xxx;` (e.g. `SettingsRequestedMessage` `:99`, `SettingsChangedMessage` `:103`).
   - `ToolbarViewModelTests` (`tests/EyeCtrl.ViewModels.Tests/`): `WeakReferenceMessenger _messenger = new()`, `EyeCtrlSettings _settings = new()`; `InitializesWith7Buttons` asserts `vm.Buttons.Count == 7` (Task 4 updates to 8); `_messenger.Register<Msg>(recipient, (_, _) => ...)` + `vm.SimulateButtonActivated("Id")`.
   - `IMessenger` is registered as `WeakReferenceMessenger.Default` (`AppBootstrapper.cs:55`).

If any differ, adjust before proceeding.

**Docs / warnings note:** `EyeCtrl.App` does not enforce CS1591 as strictly as Core, but every public member added here is documented anyway (these are public services). WPF SDK auto-globs `*.xaml` as `Page`, so `CueOverlayWindow.xaml` needs no `.csproj` edit (as `CursorCircleWindow.xaml` needs none).

---

## Scope boundary (explicit — deferred pieces land in 1b-⑤c / 1b-⑥)

This slice is the **cue + the manual escape + the two shared seams**. Deferred:
- **The drift report** (§7.1 "report") — a rate-limited, report-only note (calibration NEVER auto-touched, §8.5) reusing the EyeCare banner → **1b-⑤c**. It will subscribe to the same `AssessmentUpdated` event added here and consult the same `IFeedbackGate`.
- **The adaptation log** (§10) — a structured Serilog action log (JSONL + review UI deferred to Phase 2) → **1b-⑤c**.
- **The real feedback gate** — `IFeedbackGate` here is the no-op `AllowAllFeedbackGate`; 1b-⑥'s UIA `ISensitiveContextDetector`-backed gate replaces the DI registration, suppressing the cue (and ⑤c's report/verbose log) in sensitive contexts (§8.10). No detector is built here.

Other deferrals (unchanged from prior slices):
- **Only `FixationRadius` routes observably** (1b-①); the cue reflects the *whole* assessment's `AggregateLevel`, independent of which knobs are wired.
- **Cue position** is a fixed top-right corner (a tunable starting value); a toolbar-edge-aware placement is a later refinement.

---

## File Structure

**Create (App):**
- `src/EyeCtrl.App/Services/IFeedbackGate.cs` — the suppression seam interface + the no-op `AllowAllFeedbackGate` (the single §8.10 seam 1b-⑥ replaces).
- `src/EyeCtrl.App/Services/CueState.cs` — `readonly record struct CueState(bool Visible, double Intensity)`.
- `src/EyeCtrl.App/Services/ICuePresenter.cs` — the cue-rendering seam (`void Show(double intensity)`), so the cue service's decisions are headless-testable.
- `src/EyeCtrl.App/Services/AdaptationCueService.cs` — subscribes to the coordinator's `AssessmentUpdated`, computes `CueState`, drives the presenter.
- `src/EyeCtrl.App/Services/AdaptationResetHandler.cs` — registers `ResetAdaptationMessage` → `coordinator.Reset()`.
- `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml` + `.xaml.cs` — the transparent, click-through, capture-excluded corner badge implementing `ICuePresenter`.

**Modify:**
- `src/EyeCtrl.App/Services/AdaptationCoordinator.cs` (1b-⑤a) — add the `AssessmentUpdated` event + raise it in `OnTimerTick`.
- `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` — add `ResetAdaptationMessage`.
- `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` — add the `ResetAdaptation` button (tuple + switch arm).
- `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — render the `ResetAdaptation` button (tuple).
- `src/EyeCtrl.App/Services/TrayIconManager.cs` — add the "Reset adaptation" tray item.
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` + `src/EyeCtrl.App/App.xaml.cs` — DI-register + start the cue service, reset handler, gate, and cue window.

**Test (create):**
- `tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs` — headless cue decision tests.
- `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs` — headless reset-message → baseline integration test.

**Test (modify):**
- `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` — button count 7 → 8 + the reset-button message test.

**Zero-behaviour-change guarantee:** the cue only calls `Show` when `AdaptationCueService.OnAssessment` sees `AggregateLevel > 0` (which requires a gated failure mode, hence `AdaptationSettings.Enabled`, off by default) **and** `ShowCue` **and** a non-suppressing gate; until then the cue window is constructed-but-hidden. `AllowAllFeedbackGate` never suppresses (identical to no gate). The escape controls send a message whose handler calls `Reset()`, a no-op at baseline. The `AssessmentUpdated` event has no subscribers on the gaze hot path. The manual toolbar/tray escape is intentionally **always present** (not gated on `Enabled`) per §8.6 "always-reachable" — it is static UI chrome, not a gaze-pipeline cost (§9.1's "disabled = current cost" governs the pipeline), and `Reset()` is safe when there is nothing to reset. (Ratified in the round-1 review, which flagged the gaze-only mis-activation surface of an always-on toolbar dwell target: §9.1 governs the gaze-pipeline cost only and §8.6 explicitly wants a large toolbar button mirrored in the tray, so always-present is spec-faithful. Gating the toolbar button on `Enabled` while keeping the tray item always-on — via a settings-reactive `ToolbarViewModel` update — remains a clean future option if the extra always-on dwell target proves a nuisance.)

---

## Task 1: Shared seams — coordinator `AssessmentUpdated` event + `IFeedbackGate`

Add the per-tick push event the feedback surface subscribes to, and the single suppression seam 1b-⑥ replaces.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AdaptationCoordinator.cs`
- Create: `src/EyeCtrl.App/Services/IFeedbackGate.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs` (created here for the gate test; extended in Task 2)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs` (`tests/Directory.Build.props:27-28` already supplies global `using Xunit;` **and** `using NFluent;`, so neither is strictly required; the explicit `using NFluent;` below just matches the sibling `ToolbarAutoHideControllerTests` / ⑤a `AdaptationCoordinatorTests` style — redundant but consistent, and a local using duplicating a global one is CS8019-suppressed by default):

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;

public class AdaptationCueServiceTests
{
    [Fact]
    public void AllowAllFeedbackGate_NeverSuppresses()
    {
        Check.That(new AllowAllFeedbackGate().IsFeedbackSuppressed).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCueServiceTests"`
Expected: FAIL to **compile** — `AllowAllFeedbackGate` does not exist.

- [ ] **Step 3: Create `IFeedbackGate` + `AllowAllFeedbackGate`**

Create `src/EyeCtrl.App/Services/IFeedbackGate.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// The single seam that suppresses user-visible adaptation feedback (the cue now; the drift report and
/// verbose log in 1b-⑤c) in sensitive contexts (design §8.10). 1b-⑥ replaces the registered
/// implementation with one backed by a UIA <c>ISensitiveContextDetector</c> and
/// <c>AdaptationSettings.SuppressInSensitiveContexts</c>; adaptation itself keeps running silently.
/// </summary>
public interface IFeedbackGate
{
    /// <summary>True when visible/loggable feedback must be suppressed (e.g. a password field is focused).</summary>
    bool IsFeedbackSuppressed { get; }
}

/// <summary>
/// The default no-op gate: feedback is never suppressed. Used until 1b-⑥ supplies the sensitive-context
/// implementation. Behaviourally identical to having no gate.
/// </summary>
public sealed class AllowAllFeedbackGate : IFeedbackGate
{
    /// <inheritdoc />
    public bool IsFeedbackSuppressed => false;
}
```

- [ ] **Step 4: Add the `AssessmentUpdated` event to `AdaptationCoordinator`**

In `src/EyeCtrl.App/Services/AdaptationCoordinator.cs`, add the event immediately **after** the `LastAssessment` property:

```csharp
    /// <summary>
    /// Raised once per timer tick (App runtime), carrying <see cref="LastAssessment"/> — the current
    /// assessment, or <see cref="StruggleAssessment.None"/> whenever the tick did not assess (disabled,
    /// paused, disconnected, awaiting-gaze, or a reset). The 1b-⑤b/⑤c feedback surface subscribes to this
    /// single per-tick signal on the coordinator's cadence; because it carries None on non-assessing
    /// ticks, the cue fades naturally without a subscriber having to gate away a stale value.
    /// </summary>
    public event Action<StruggleAssessment>? AssessmentUpdated;
```

Then change `OnTimerTick` from the 1b-⑤a one-liner to raise the event after the tick:

```csharp
    private void OnTimerTick(object? sender, EventArgs e)
    {
        Tick();
        AssessmentUpdated?.Invoke(LastAssessment);
    }
```

(`Tick()` and `LastAssessment` are unchanged; the event fires on the dispatcher thread after each tick. Headless tests call `Tick()` directly and do not exercise `OnTimerTick`, so 1b-⑤a's tests are unaffected.)

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCueServiceTests"`
Expected: PASS (1 test). `AdaptationCoordinator` still compiles (the event is additive; `OnTimerTick` change is behaviour-preserving with no subscribers).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/IFeedbackGate.cs src/EyeCtrl.App/Services/AdaptationCoordinator.cs tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs
git commit -m "feat(adaptation): add AssessmentUpdated push event + IFeedbackGate suppression seam"
```

---

## Task 2: Cue decision logic — `CueState`, `ICuePresenter`, `AdaptationCueService`

The pure, headless-testable heart of the cue: map an assessment (+ `ShowCue` + the gate) to a `CueState`, and drive an `ICuePresenter` when visible.

**Files:**
- Create: `src/EyeCtrl.App/Services/CueState.cs`, `src/EyeCtrl.App/Services/ICuePresenter.cs`, `src/EyeCtrl.App/Services/AdaptationCueService.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Replace the body of `tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs` (keep `AllowAllFeedbackGate_NeverSuppresses`) with:

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;
using NFluent;

public class AdaptationCueServiceTests
{
    private sealed class FakeCuePresenter : ICuePresenter
    {
        public int ShowCalls { get; private set; }
        public double LastIntensity { get; private set; } = double.NaN;
        public void Show(double intensity) { ShowCalls++; LastIntensity = intensity; }
    }

    private sealed class FakeFeedbackGate : IFeedbackGate
    {
        public bool IsFeedbackSuppressed { get; set; }
    }

    // Builds an assessment with a given aggregate level (Modes are irrelevant to the cue, which reads
    // only AggregateLevel). StruggleAssessment ctor is (IReadOnlyList<FailureModeAssessment>, double).
    private static StruggleAssessment Level(double aggregate) =>
        new(Array.Empty<FailureModeAssessment>(), aggregate);

    private static (AdaptationCueService Svc, AdaptationSettings Adaptation, FakeFeedbackGate Gate, FakeCuePresenter Presenter) Make()
    {
        var settings = new EyeCtrlSettings();     // ShowCue defaults true (1b-②)
        var gate = new FakeFeedbackGate();
        var presenter = new FakeCuePresenter();
        var svc = new AdaptationCueService(settings.Adaptation, gate, presenter);
        return (svc, settings.Adaptation, gate, presenter);
    }

    [Fact]
    public void AllowAllFeedbackGate_NeverSuppresses()
    {
        Check.That(new AllowAllFeedbackGate().IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void ComputeCueState_VisibleWithIntensityFromAggregate()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.6), showCue: true, feedbackSuppressed: false);
        Check.That(s.Visible).IsTrue();
        Check.That(s.Intensity).IsEqualTo(0.6);
    }

    [Fact]
    public void ComputeCueState_HiddenWhenLevelZero()
    {
        var s = AdaptationCueService.ComputeCueState(StruggleAssessment.None, showCue: true, feedbackSuppressed: false);
        Check.That(s.Visible).IsFalse();
        Check.That(s.Intensity).IsEqualTo(0.0);
    }

    [Fact]
    public void ComputeCueState_HiddenWhenShowCueOff()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.9), showCue: false, feedbackSuppressed: false);
        Check.That(s.Visible).IsFalse();
    }

    [Fact]
    public void ComputeCueState_HiddenWhenSuppressed()
    {
        var s = AdaptationCueService.ComputeCueState(Level(0.9), showCue: true, feedbackSuppressed: true);
        Check.That(s.Visible).IsFalse();
    }

    [Fact]
    public void OnAssessment_ShowsPresenter_WhenVisible()
    {
        var (svc, _, _, presenter) = Make();
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsTrue();
        Check.That(presenter.ShowCalls).IsEqualTo(1);
        Check.That(presenter.LastIntensity).IsEqualTo(0.5);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenShowCueOff()
    {
        var (svc, adaptation, _, presenter) = Make();
        adaptation.ShowCue = false;
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenSuppressed()
    {
        var (svc, _, gate, presenter) = Make();
        gate.IsFeedbackSuppressed = true;
        var state = svc.OnAssessment(Level(0.5));
        Check.That(state.Visible).IsFalse();
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }

    [Fact]
    public void OnAssessment_DoesNotShow_WhenLevelZero()
    {
        var (svc, _, _, presenter) = Make();
        svc.OnAssessment(StruggleAssessment.None);
        Check.That(presenter.ShowCalls).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCueServiceTests"`
Expected: FAIL to **compile** — `CueState`, `ICuePresenter`, `AdaptationCueService` do not exist.

- [ ] **Step 3: Create `CueState`**

Create `src/EyeCtrl.App/Services/CueState.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// The cue's presentation decision for one assessment: whether the badge should be shown and, if so,
/// how intense it should be (design §10 — intensity scales with the struggle level).
/// </summary>
/// <param name="Visible">True when the badge should be shown this tick.</param>
/// <param name="Intensity">Struggle intensity in [0, 1] driving the badge's peak opacity; 0 when hidden.</param>
public readonly record struct CueState(bool Visible, double Intensity);
```

- [ ] **Step 4: Create `ICuePresenter`**

Create `src/EyeCtrl.App/Services/ICuePresenter.cs`:

```csharp
namespace EyeCtrl.App.Services;

/// <summary>
/// Renders the adaptation cue. Kept separate from <see cref="AdaptationCueService"/> so the service's
/// show/hide decisions are headless-testable with a fake presenter; the WPF <c>CueOverlayWindow</c> is
/// the runtime implementation.
/// </summary>
public interface ICuePresenter
{
    /// <summary>Flashes the cue badge at the given intensity ([0, 1]) and (re)starts its auto-fade (~2 s).</summary>
    /// <param name="intensity">Struggle intensity driving the badge's peak opacity.</param>
    void Show(double intensity);
}
```

- [ ] **Step 5: Create `AdaptationCueService`**

Create `src/EyeCtrl.App/Services/AdaptationCueService.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

/// <summary>
/// Drives the on-screen adaptation cue (design §10). Subscribes to <see cref="AdaptationCoordinator"/>'s
/// per-tick <c>AssessmentUpdated</c> event and, when the current assessment shows gated struggle, flashes
/// a subtle corner badge whose intensity scales with the aggregate level. The badge is suppressed when
/// <see cref="AdaptationSettings.ShowCue"/> is off or the <see cref="IFeedbackGate"/> is suppressing
/// (sensitive contexts, §8.10). No explicit <c>Enabled</c> check is needed: when adaptation is disabled
/// the coordinator reports <see cref="StruggleAssessment.None"/> (aggregate 0 ⇒ hidden). The decision
/// logic (<see cref="ComputeCueState"/>/<see cref="OnAssessment"/>) is pure and headless-testable; only
/// <see cref="Start"/>'s event subscription is runtime.
/// </summary>
public sealed class AdaptationCueService : IDisposable
{
    private readonly AdaptationSettings _adaptation;
    private readonly IFeedbackGate _feedbackGate;
    private readonly ICuePresenter _presenter;
    private AdaptationCoordinator? _coordinator; // retained to unsubscribe in Dispose

    /// <summary>Initializes the cue service over its settings gate, suppression seam, and presenter.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.ShowCue"/> gates the cue.</param>
    /// <param name="feedbackGate">The suppression seam (§8.10); the no-op gate never suppresses.</param>
    /// <param name="presenter">The cue renderer (the WPF window at runtime, a fake in tests).</param>
    public AdaptationCueService(AdaptationSettings adaptation, IFeedbackGate feedbackGate, ICuePresenter presenter)
    {
        _adaptation = adaptation;
        _feedbackGate = feedbackGate;
        _presenter = presenter;
    }

    /// <summary>
    /// Pure cue decision: visible (with intensity = the aggregate level) only when the cue is enabled,
    /// not suppressed, and the assessment shows gated struggle (<c>AggregateLevel &gt; 0</c>).
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <param name="showCue">Whether the cue is enabled (<see cref="AdaptationSettings.ShowCue"/>).</param>
    /// <param name="feedbackSuppressed">Whether feedback is suppressed (<see cref="IFeedbackGate.IsFeedbackSuppressed"/>).</param>
    /// <returns>The presentation decision.</returns>
    public static CueState ComputeCueState(StruggleAssessment assessment, bool showCue, bool feedbackSuppressed)
    {
        if (!showCue || feedbackSuppressed)
            return new CueState(false, 0.0);

        double level = assessment.AggregateLevel;
        if (level <= 0.0)
            return new CueState(false, 0.0);

        return new CueState(true, Math.Clamp(level, 0.0, 1.0));
    }

    /// <summary>
    /// Handles one assessment: computes the <see cref="CueState"/> against the live settings/gate and
    /// flashes the badge when visible. Returns the state for test assertions.
    /// </summary>
    /// <param name="assessment">The current struggle assessment.</param>
    /// <returns>The presentation decision taken.</returns>
    public CueState OnAssessment(StruggleAssessment assessment)
    {
        CueState state = ComputeCueState(assessment, _adaptation.ShowCue, _feedbackGate.IsFeedbackSuppressed);
        if (state.Visible)
            _presenter.Show(state.Intensity);
        return state;
    }

    /// <summary>
    /// Subscribes to the coordinator's per-tick assessment stream. Call once on the UI thread; idempotent
    /// (a second call is a no-op, so it never double-subscribes).
    /// </summary>
    /// <param name="coordinator">The coordinator whose <c>AssessmentUpdated</c> event drives the cue.</param>
    public void Start(AdaptationCoordinator coordinator)
    {
        if (_coordinator is not null)
            return; // already started
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

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationCueServiceTests"`
Expected: PASS (9 tests).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/CueState.cs src/EyeCtrl.App/Services/ICuePresenter.cs src/EyeCtrl.App/Services/AdaptationCueService.cs tests/EyeCtrl.App.Tests/Services/AdaptationCueServiceTests.cs
git commit -m "feat(adaptation): add AdaptationCueService cue decision logic (headless)"
```

---

## Task 3: `CueOverlayWindow` (the WPF corner badge)

The runtime `ICuePresenter`: a transparent, click-through, **capture-excluded** overlay showing a subtle amber badge in the top-right corner that fades over ~2 s and re-flashes while struggle persists. Build-verified (STA/WPF, not headless-unit-tested — the App test tier).

**Files:**
- Create: `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml`, `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml.cs`

- [ ] **Step 1: Create the XAML shell**

Create `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml` (mirrors `CursorCircleWindow.xaml`: transparent `OverlayWindow`, fixed size, a `Canvas` with the badge + a hidden UIA-anchor `TextBlock`):

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.CueOverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        AutomationProperties.AutomationId="AdaptationCue_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="64" Height="64">
    <Canvas>
        <!-- Amber "struggle" badge; Opacity is animated in code-behind (0 = invisible at rest). -->
        <Ellipse x:Name="Badge"
                 Width="40" Height="40"
                 Canvas.Left="12" Canvas.Top="12"
                 Fill="#FFB300" Opacity="0"/>
        <!-- Hidden TextBlock UIA anchor — WPF transparent overlay windows only expose TextBlock nodes
             in the UIA tree; Ellipse/Canvas are collapsed. -->
        <TextBlock AutomationProperties.AutomationId="AdaptationCue_Badge"
                   Opacity="0" IsHitTestVisible="False"
                   Canvas.Left="0" Canvas.Top="0"/>
    </Canvas>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Create the code-behind**

Create `src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.App.Services;

/// <summary>
/// The adaptation cue overlay (design §10): a subtle top-right corner badge whose peak opacity scales
/// with struggle intensity and which auto-fades over ~2 s. It is click-through and
/// <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true, so it is never baked into the magnifier /
/// action-menu <c>CopyFromScreen</c> grabs and never intercepts gaze/mouse (non-interactive).
/// </summary>
public partial class CueOverlayWindow : OverlayWindow, ICuePresenter
{
    private const double MarginDip = 24;         // gap from the work-area corner
    private const double MinPeakOpacity = 0.25;  // faintly visible even at low intensity ("subtle")
    private static readonly Duration FadeDuration = new(TimeSpan.FromSeconds(2));

    // Bumped on each Show so a superseded fade's Completed handler is ignored (WPF may or may not raise
    // Completed on a replaced animation; the generation check makes the hide fire only for the latest).
    private int _fadeGeneration;
    private bool _positioned;

    /// <summary>Creates the cue window (click-through + excluded from screen capture, set before init).</summary>
    public CueOverlayWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep the cue out of the magnifier/action-menu preview capture (§10)
        InitializeComponent();
    }

    /// <inheritdoc />
    public void Show(double intensity)
    {
        EnsurePositioned();
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        double peak = MinPeakOpacity + (1.0 - MinPeakOpacity) * Math.Clamp(intensity, 0.0, 1.0);
        int generation = ++_fadeGeneration;
        var fade = new DoubleAnimation(peak, 0.0, FadeDuration) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            // Only the latest fade completing (struggle stopped) hides the window; a re-flash bumped the
            // generation, so its predecessor's Completed is a no-op and the badge stays up while struggling.
            if (generation == _fadeGeneration)
                Hide();
        };
        Badge.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width
        Left = area.Right - Width - MarginDip;
        Top = area.Top + MarginDip;
        _positioned = true;
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. `CueOverlayWindow` resolves `OverlayWindow` (base), `ICuePresenter` (`EyeCtrl.App.Services`), and the generated `Badge` field from the XAML; the `*.xaml` is auto-included as a `Page` by the WPF SDK (no `.csproj` edit).

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml src/EyeCtrl.App/Overlays/CueOverlayWindow.xaml.cs
git commit -m "feat(adaptation): add CueOverlayWindow (capture-excluded fading corner badge)"
```

---

## Task 4: Manual escape — `ResetAdaptationMessage`, `AdaptationResetHandler`, toolbar button, tray item

The always-reachable §8.6 escape: a toolbar reset button and a tray item that send `ResetAdaptationMessage`, whose dedicated handler clears all adaptation to baseline via `coordinator.Reset()`.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, `src/EyeCtrl.App/Services/TrayIconManager.cs`
- Create: `src/EyeCtrl.App/Services/AdaptationResetHandler.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs` (create), `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` (modify)

- [ ] **Step 1: Add `ResetAdaptationMessage`**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, add immediately **after** `public record SettingsChangedMessage;` (`:103`):

```csharp
/// <summary>Sent by the toolbar reset button and tray item to clear all struggle-adaptation back to
/// baseline — the always-reachable escape (design §8.6). Handled by <c>AdaptationResetHandler</c>.</summary>
public record ResetAdaptationMessage;
```

- [ ] **Step 2: Write the failing reset-handler test**

Create `tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs`. This drives a real coordinator→monitor→controller→provider pipeline to an observable tolerance relaxation, then confirms the message path clears it (mirrors 1b-⑤a's `Reset_ClearsMomentaryAdaptation`, but through the messenger):

```csharp
namespace EyeCtrl.App.Tests.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;

public class AdaptationResetHandlerTests
{
    private const long Sec = 1_000_000;

    // Minimal IGazeProvider fake (only IsConnected is exercised; GazePointReceived via no-op accessors
    // to avoid CS0067; Capabilities/TrackStatus* are default-implemented on the interface).
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public bool IsConnected { get; set; } = true;
        event Action<GazePoint>? IGazeProvider.GazePointReceived { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static (AdaptationCoordinator Coord, EffectiveSettingsProvider Provider, BufferingTelemetrySink Sink) Make()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        var coord = new AdaptationCoordinator(monitor, controller, settings.Adaptation, new FakeGazeProvider(), new InteractionManager());
        return (coord, provider, sink);
    }

    private static DwellOutcome Abandon(long ts, double progress) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
        new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30, null);

    private static void DriveToRelaxedTolerance(AdaptationCoordinator coord, BufferingTelemetrySink sink)
    {
        coord.Tick(); // ResetOnEnable (now == Unseeded → sessionStart excludes nothing)
        for (int i = 0; i < 4; i++)
            sink.Report(Abandon(i * 10 * Sec, progress: 0.8)); // gate SustainFailure
        coord.OnGazeSample(new GazePoint(0, 0, 40 * Sec, true)); coord.Tick(); // anchor relaxSince
        coord.OnGazeSample(new GazePoint(0, 0, 43 * Sec, true)); coord.Tick(); // 3 s sustain → relax
    }

    [Fact]
    public void ResetMessage_ClearsMomentaryAdaptation()
    {
        var (coord, provider, sink) = Make();
        var messenger = new WeakReferenceMessenger();
        using var handler = new AdaptationResetHandler(messenger, coord);
        handler.Start();

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // the toolbar/tray escape
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void AfterDispose_MessageIsIgnored()
    {
        var (coord, provider, sink) = Make();
        var messenger = new WeakReferenceMessenger();
        var handler = new AdaptationResetHandler(messenger, coord);
        handler.Start();
        handler.Dispose(); // unregisters

        DriveToRelaxedTolerance(coord, sink);
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();

        messenger.Send(new ResetAdaptationMessage()); // no live handler → no reset
        Check.That(provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50) > 50.0).IsTrue();
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationResetHandlerTests"`
Expected: FAIL to **compile** — `AdaptationResetHandler` does not exist.

- [ ] **Step 4: Create `AdaptationResetHandler`**

Create `src/EyeCtrl.App/Services/AdaptationResetHandler.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Wires the manual escape (design §8.6): registers for <see cref="ResetAdaptationMessage"/> (sent by the
/// toolbar reset button and the tray item) and clears all momentary adaptation back to baseline via
/// <see cref="AdaptationCoordinator.Reset"/>. A dedicated small service rather than a handler inside the
/// ctor-less, ~1500-line <c>EventCoordinator</c>, so the reset path is self-contained and headless-testable.
/// </summary>
public sealed class AdaptationResetHandler : IDisposable
{
    private readonly IMessenger _messenger;
    private readonly AdaptationCoordinator _coordinator;

    /// <summary>Initializes the handler over the app messenger and the coordinator it resets.</summary>
    /// <param name="messenger">The app messenger (<c>WeakReferenceMessenger.Default</c> at runtime).</param>
    /// <param name="coordinator">The coordinator whose <see cref="AdaptationCoordinator.Reset"/> is invoked.</param>
    public AdaptationResetHandler(IMessenger messenger, AdaptationCoordinator coordinator)
    {
        _messenger = messenger;
        _coordinator = coordinator;
    }

    /// <summary>Registers the reset-message subscription. Call once at startup.</summary>
    public void Start() =>
        _messenger.Register<AdaptationResetHandler, ResetAdaptationMessage>(this, static (h, _) => h._coordinator.Reset());

    /// <summary>Unregisters the subscription.</summary>
    public void Dispose() => _messenger.UnregisterAll(this);
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationResetHandlerTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Update the toolbar ViewModel tests (count 7 → 8 + reset-message test)**

In `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`, change `InitializesWith7Buttons` to reflect the new button:

```csharp
    [Fact]
    public void InitializesWith8Buttons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.Buttons.Count).IsEqualTo(8);
    }
```

and add a new test (mirrors `PauseButton_SendsPauseToggledMessage`):

```csharp
    [Fact]
    public void ResetAdaptationButton_SendsResetAdaptationMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<ResetAdaptationMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("ResetAdaptation");

        Check.That(received).IsTrue();
    }
```

- [ ] **Step 7: Run to verify the ViewModel tests fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: FAIL — `InitializesWith8Buttons` sees 7, and `ResetAdaptationButton_SendsResetAdaptationMessage` never receives (the button/arm don't exist yet).

- [ ] **Step 8: Add the `ResetAdaptation` button to `ToolbarViewModel`**

In `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, add a new entry as the **last element** of the `(string Id, string Symbol)[]` array in the constructor (`:38-47`), immediately after the `("QuickActions", "⚡")` line — keep the existing seven entries and their `\u` escapes byte-for-byte unchanged, and write the new entry's symbol as the `\u`-escaped code point **U+21BA** (the ↺ glyph), matching the existing entries' escape style (the code block shows the glyph for readability):

```csharp
            ("QuickActions", "⚡"),  // ⚡
            ("ResetAdaptation", "↺"),  // ↺ reset struggle-adaptation to baseline (§8.6 escape)
```

and add the switch arm in `OnTargetActivated` (`:100-112`), immediately after the `QuickActions` arm:

```csharp
            case "QuickActions": Messenger.Send(new QuickActionsToggledMessage()); break;
            case "ResetAdaptation": Messenger.Send(new ResetAdaptationMessage()); break;
```

- [ ] **Step 9: Run to verify the ViewModel tests pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: PASS (`InitializesWith8Buttons` + `ResetAdaptationButton_SendsResetAdaptationMessage` + the existing tests).

- [ ] **Step 10: Render the button in `ToolbarWindow` + add the tray item**

In `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`, add a new entry as the **last element** of the `(string Id, string Symbol)[]` array in `Initialize` (`:41-47`), immediately after the `("Settings", "⚙")` line — keep the existing four entries and their `\u` escapes byte-for-byte unchanged, and write the new entry's symbol as the `\u`-escaped code point **U+21BA** (the ↺ glyph), matching the file's escape style (the code block shows the glyph for readability). This makes 5 rendered buttons:

```csharp
            ("Settings", "⚙"),    // ⚙ Gear
            ("ResetAdaptation", "↺"),  // ↺ Reset struggle-adaptation (§8.6 always-reachable escape)
```

In `src/EyeCtrl.App/Services/TrayIconManager.cs`, add the tray item after the `settingsItem` declaration (`:43-45`, mirroring its message-send pattern):

```csharp
        var resetAdaptationItem = new ToolStripMenuItem("Reset adaptation");
        resetAdaptationItem.Click += (_, _) => dispatcher.BeginInvoke(() =>
            _messenger.Send(new ResetAdaptationMessage()));
```

and add it to the menu immediately after `menu.Items.Add(settingsItem);` (`:52`):

```csharp
        menu.Items.Add(settingsItem);
        menu.Items.Add(resetAdaptationItem);
```

- [ ] **Step 11: Build to verify the App compiles**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. `ToolbarWindow`/`TrayIconManager` reference `ResetAdaptationMessage` (`using EyeCtrl.ViewModels.Messages;` already present in both). (The toolbar now renders 5 buttons. The two relevant app-launch checks are class-level `[Trait("Category","UI")]` (`AppLaunchTests.cs:9`) and excluded from the headless subset — and both are already stale vs current source, independent of this change: `Toolbar_HasFiveButtons` (`:38-47`) asserts a count of 5 while today's View renders 4, so after this change the count coincidentally matches; `Toolbar_ButtonsHaveCorrectIds` (`:50-69`) expects `ManualSelection`/`Calibrate` IDs the current View never renders. Neither runs in CI; that pre-existing UI-test drift is out of scope here.)

- [ ] **Step 12: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.App/Services/AdaptationResetHandler.cs src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs src/EyeCtrl.App/Services/TrayIconManager.cs tests/EyeCtrl.App.Tests/Services/AdaptationResetHandlerTests.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "feat(adaptation): add manual reset escape (toolbar button + tray item + handler)"
```

---

## Task 5: DI registration + `App.OnStartup` wiring

Register the gate, cue window, cue service, and reset handler, and start the cue service + reset handler after the coordinator starts.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`, `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register the feedback services**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, immediately **after** the 1b-⑤a `AdaptationCoordinator` registration (and before `return services.BuildServiceProvider();`), add:

```csharp
        // 1b-⑤b feedback & manual-escape surface. AllowAllFeedbackGate is the no-op suppression seam
        // 1b-⑥ replaces with the sensitive-context gate (§8.10). CueOverlayWindow is excluded from
        // magnifier capture and doubles as the ICuePresenter. The cue service + reset handler are started
        // in App.OnStartup (after the coordinator starts).
        services.AddSingleton<IFeedbackGate, AllowAllFeedbackGate>();
        services.AddSingleton<CueOverlayWindow>();
        services.AddSingleton<ICuePresenter>(sp => sp.GetRequiredService<CueOverlayWindow>());
        services.AddSingleton(sp => new AdaptationCueService(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<ICuePresenter>()));
        services.AddSingleton(sp => new AdaptationResetHandler(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<AdaptationCoordinator>()));
```

(No new `using`s: `EyeCtrl.App.Overlays` for `CueOverlayWindow` and `CommunityToolkit.Mvvm.Messaging` for `IMessenger` are already imported; the feedback types are same-namespace (`EyeCtrl.App.Services`); `EyeCtrlSettings` is `EyeCtrl.Core.Settings`, present.)

- [ ] **Step 2: Start the cue service + reset handler in `App.OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, immediately **after** the 1b-⑤a line `_serviceProvider.GetRequiredService<AdaptationCoordinator>().Start(gazeEngine);`, add:

```csharp
            // 1b-⑤b: start the adaptation feedback surface. The cue subscribes to the coordinator's
            // per-tick AssessmentUpdated event; the reset handler registers the manual toolbar/tray escape.
            // Resolving the cue service constructs CueOverlayWindow on this (STA) thread; the container
            // holds all three for the process lifetime, so no explicit fields are kept.
            var adaptationCoordinator = _serviceProvider.GetRequiredService<AdaptationCoordinator>();
            _serviceProvider.GetRequiredService<AdaptationCueService>().Start(adaptationCoordinator);
            _serviceProvider.GetRequiredService<AdaptationResetHandler>().Start();
```

- [ ] **Step 3: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. The registrations resolve their dependencies (`EyeCtrlSettings`/`IMessenger`/`AdaptationCoordinator` pre-existing or 1b-⑤a; `IFeedbackGate`/`ICuePresenter`/`CueOverlayWindow` from Tasks 1–3); `AdaptationCueService.Start(AdaptationCoordinator)` and `AdaptationResetHandler.Start()` match the call sites.

- [ ] **Step 4: Run the headless App + ViewModels + Core suites (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS — `AdaptationCueServiceTests` (9) + `AdaptationResetHandlerTests` (2) + the updated `ToolbarViewModelTests` + the existing headless suites. The new registrations are inert until the coordinator reports gated struggle (which needs `AdaptationSettings.Enabled`, off by default), so no existing behaviour changes. (Full DI-container resolution of the cue window + the running `DispatcherTimer`/event are exercised only when the app launches — the UI-test tier — since building the container instantiates STA WPF overlays.)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): register + start the cue service and manual-reset handler"
```

---

## Self-Review

**Spec coverage (this slice):**
- §10 on-screen cue — subtle corner badge (`CueOverlayWindow`), **on-canvas + excluded from magnifier capture** (`ExcludeFromScreenCapture = true`, verified against `ScreenCaptureService` GDI `CopyFromScreen` + both callers), **non-interactive** (click-through + non-activating via `OverlayWindow`), **auto-fading ~2 s** (2 s `DoubleAnimation` restarted per struggle tick, hides on genuine completion), **intensity scales with the assessment** (`AggregateLevel` → peak opacity), **gated on `ShowCue`**. ✓
- §10 manual override — toolbar reset button (`ToolbarViewModel` + `ToolbarWindow`) **mirrored in the tray** (`TrayIconManager`), both sending `ResetAdaptationMessage` → `AdaptationResetHandler` → `coordinator.Reset()`. ✓
- §8.6 always-reachable escape — the toolbar button + tray item are always present (documented decision: static UI chrome, not a pipeline cost; `Reset()` is a safe baseline no-op). ✓
- §8.10 no feedback leakage — the cue consults a single `IFeedbackGate` seam; `AllowAllFeedbackGate` is the no-op default; 1b-⑥ swaps the registration for the sensitive-context gate. ✓
- §9.1 cue is a cheap fading badge / disabled = current cost — the cue only shows on gated struggle (needs `Enabled`); the `AssessmentUpdated` event fires on the dispatcher (not the gaze hot path); `Show` is one animation begin. ✓
- Push-event design (brainstorm decision) — `AdaptationCoordinator.AssessmentUpdated` raised per tick, carrying `LastAssessment` (None on non-assessing ticks ⇒ the cue fades naturally, no stale-value gating). ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `IFeedbackGate.IsFeedbackSuppressed`, `CueState(bool Visible, double Intensity)`, `ICuePresenter.Show(double)`, `AdaptationCueService(AdaptationSettings, IFeedbackGate, ICuePresenter)` + `ComputeCueState(StruggleAssessment, bool, bool)` + `OnAssessment(StruggleAssessment) → CueState` + `Start(AdaptationCoordinator)`, `AdaptationResetHandler(IMessenger, AdaptationCoordinator)` + `Start()`/`Dispose()`, `CueOverlayWindow : OverlayWindow, ICuePresenter` with `Show(double)`, and `ResetAdaptationMessage` are used identically across the definitions, the tests, the DI registrations, and the `OnStartup` calls. `AdaptationCoordinator.AssessmentUpdated`/`LastAssessment`/`Reset`/`OnTimerTick`, `StruggleAssessment(IReadOnlyList<FailureModeAssessment>, double)`/`AggregateLevel`/`None`, `AdaptationSettings.ShowCue`, `EffectiveSettingsProvider.Effective(AdaptationKnob, DwellContext, double)`, `BufferingTelemetrySink(int)`/`Report`, and the `ToolbarViewModel`/`ToolbarWindow`/`TrayIconManager` touch points match the pre-flight-confirmed upstream plans and the verified HEAD source.

**Verified facts relied on (from source, this session):**
- `OverlayWindow` init props + `OnSourceInitialized` capture/click-through wiring, and the six existing `ExcludeFromScreenCapture` opt-ins; `CursorCircleWindow` sets both init props in the ctor before `InitializeComponent()` (`OverlayWindow.cs:32,42,44-54`; `CursorCircleWindow.xaml.cs:14-18`).
- `ScreenCaptureService.CaptureRegion` GDI `CopyFromScreen` (`:37`), called by the magnifier and action-menu preview — the load-bearing reason the cue must exclude itself.
- `ToolbarViewModel` button array + `OnTargetActivated` switch + `SimulateButtonActivated` test seam + 2-arg ctor; `ToolbarWindow.Initialize` renders a 4-entry array via `AddButton`; `TrayIconManager` `_messenger.Send(new SettingsRequestedMessage())` click pattern; `InteractionMessages` `public record` style; `ToolbarViewModelTests` `WeakReferenceMessenger` + count-7 assertion; `IMessenger` = `WeakReferenceMessenger.Default` (`AppBootstrapper.cs:55`).
- 1b-⑤a `AdaptationCoordinator` surface (`LastAssessment`, `Reset`, `Start(GazeEngine)`, `OnTimerTick => Tick()`) + its DI registration and `OnStartup` `.Start(gazeEngine)` line; 1b-③a `StruggleAssessment` shape; 1b-② `AdaptationSettings.ShowCue`.

**Honest testability limits:** headless-unit-tested (App-service/ViewModel tier, no STA) — `AllowAllFeedbackGate`; the cue decision logic (`ComputeCueState` + `OnAssessment` over a fake `ICuePresenter`/`IFeedbackGate`, 8 tests); the reset path end-to-end (`ResetAdaptationMessage` → `coordinator.Reset()` clearing a real relaxed tolerance to baseline, + the disposed-handler no-op, 2 tests); the toolbar button (count 8 + message send, ViewModels tier). **Build-verified only** (correctly per the App test tiers): the `CueOverlayWindow` (transparent shell, capture exclusion, corner positioning, and the fade animation fire under a running dispatcher / STA); the `AssessmentUpdated` event *firing* (raised only from `OnTimerTick` under the timer — its downstream consumption is headless-tested via `OnAssessment`); the `AdaptationCueService.Start`/`AdaptationResetHandler.Start` subscription wiring; the toolbar/tray visuals; and the full DI-container resolution (building the container instantiates STA WPF overlays). Cue position (top-right), badge colour (`#FFB300`), min peak opacity (0.25), and fade duration (2 s) are tunable starting values.

**Deferred (documented above, not gaps):** the drift report and structured Serilog adaptation log (1b-⑤c — both subscribe to `AssessmentUpdated` and consult `IFeedbackGate`); the real sensitive-context `IFeedbackGate` (1b-⑥); observable routing of dwell/smoothing/geometry (their routing slices); a toolbar-edge-aware cue position (later refinement).
