# First-Run Introduction Guide — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Rev 4** — external plan-review r3 processed (verdict FIX-FIRST: 6×S1, 5×S2; all folded in). Rev 3's corrections: **the plan is self-contained again** (every task's code inline — r3 F1); **every card's buttons come from ONE unified choice list** (step buttons + chrome Close + theme chips + Theme's Continue) registered AND visualized together — rev 3's `ShowStep` registered buttons then wiped them, and `guide_close`/chips had no visuals (r3 F5); the safety chain's ctor arg order holds; `ShutdownStarted` handler has the `EventHandler` signature; German rows use the Sie form; `DwellSlider` is replaced by −/+ chrome cards + a value read-out (it is an `IDwellComposite`, not a control); `EdgeWord` is an instance method over literal keys; the unwind is complete and re-entry-safe; the ring shows for step 5; `chromeClose` covers step 7.

**Goal:** Build the first-run introductory guide exactly as specified in `docs/superpowers/specs/2026-11-16-first-run-guide-design.md` (rev 2.4, SHIP): an eight-step gaze-driven first-run experience that absorbs the safety setup and holds seven `GuideOpen` claims.

**Architecture:** A headless `FirstRunGuideFlow` state machine drives a per-run `FirstRunGuideWindow` through `IGuideSurface`. `SettingsUiCoordinator` builds the per-run trio behind the renamed `FirstRunGuideRequestedMessage`, acquires the seven claims at the actual `Show()` inside the latched `SplashThreadHandle.WhenExited` continuation, wires gaze input through ONE choice-id switch, and releases/restores on close. The safety questions run through the existing `SafetySetupFlow` via `StartQuestions()`, begun from the transition card's Continue.

**Tech Stack:** .NET 10 WPF (net10.0-windows), CommunityToolkit.Mvvm, NFluent 3.1.0 + xUnit v3, Serilog.

## Global Constraints

- **Worktree:** every command runs inside `.worktrees/first-run-guide`. Test commands use ONE project per invocation. Before committing code: `node .gitnexus/run.cjs detect-changes --scope all`.
- **Assertions:** NFluent 3.1.0, xUnit v3, private nested fakes only. The repo has no `[Category]` attribute — UI-gated tests use `[Trait("Category", "UI")]`.
- **No visible windows in App tests:** STA tests that show the guide window call `InvisibleTestWindow.Prepare(window)`; never write/animate `Opacity` after `Show`.
- **Style:** file-scoped namespaces, Allman, `_camelCase`, nullable enabled.
- **Strings:** `guide.*` keys set `where:"guide"`; schema/validator `SURFACES` exact parity; **`strings.json` keys sorted** (`strings_validate.py:33`); **every key referenced as a quoted literal under `src/`** (`validate_unused_keys` :629-655); German rows `{"text", "src"}` only, **Sie form** (`strings_validate.py:43` rejects du-form), identical values into `localization/strings.identical-allowlist.json`; `--refresh-de-src` after EN changes.
- **Privacy:** guide log lines carry step ids only.
- **Spec §10 ledger:** settled — escalate, don't improvise.
- **Dwell geometry (verified):** choice targets register AT THE ORIGIN + `Hold(id)`; centres pushed afterwards (`UpdateTargetCenter` + `latch.Positioned`, `SettingsWindowViewModel.cs:186-215`); radius 70 DIP (`SettingsWindow.ModalBtnRadius` is an App-internal — the VM declares its own const); margin 35 DIP (`LatchReleaseMarginDip`); chrome dwell = `ToolbarButtonDwellMs`; the practice target is the one `ActivationButtonDwellMs` consumer; the detector caps per-gap credit (~150 ms, `DwellDetector.cs:403-415`) — tests feed real 30 ms samples.

---

### Task 1: `GuideOpen` seams — suppressor member, modal flag + event wiring, offers, coordinator exposure

**Files:**
- Modify: `src/EyeCtrl.Core/FixationSuppressor.cs`, `src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs`, `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs` (new)

**Interfaces:** Produces `FixationSuppressor.Guide`; `ModalInteractionState.GuideOpen` + `GuideOpenChanged` (volatile flag, per-subscriber try/catch raise); `ActivationOfferCoordinator` recomputes on the flip; `EventCoordinator.IsGuideOpen` + `GuideOpenChanged` forwarders.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Coordinators;
using NFluent;
using Xunit;

public class ModalInteractionStateTests
{
    [Fact]
    public void GuideOpen_RaisesChanged_OncePerRealFlip()
    {
        var modal = new ModalInteractionState();
        var flips = new List<bool>();
        modal.GuideOpenChanged += flips.Add;

        modal.GuideOpen = true;
        modal.GuideOpen = true;   // no change: no event
        modal.GuideOpen = false;

        Check.That(flips).HasSize(2);
        Check.That(flips[0]).IsTrue();
        Check.That(flips[1]).IsFalse();
    }

    [Fact]
    public void GuideOpen_AThrowingSubscriber_DoesNotStopTheOthers()
    {
        var modal = new ModalInteractionState();
        var seen = new List<bool>();
        modal.GuideOpenChanged += _ => throw new InvalidOperationException("boom");
        modal.GuideOpenChanged += seen.Add;

        modal.GuideOpen = true;

        Check.That(seen).HasSize(1);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` → FAIL.

- [ ] **Step 3: Implement** — `FixationSuppressor.cs` gains `Guide,` (with its doc comment); `ModalInteractionState.cs` gains the volatile `_guideOpen` + `GuideOpen` property (flip → `RaiseGuideOpenChanged`) + `GuideOpenChanged` event + the per-subscriber try/catch raise (the exact code pattern of `ProbeOpenChanged`, r2-verified); `ActivationOfferCoordinator.cs` subscribes `_modal.GuideOpenChanged += OnGuideOpenChanged;` beside :113, unsubscribes beside :367, adds `private void OnGuideOpenChanged(bool _) => RecomputeSuspension();`, and `RecomputeSuspension` adds `if (_modal.GuideOpen) reasons.Add("guide");` after the ProbeOpen check; `EventCoordinator.cs` gains beside the `IsSettingsOpen`/`SettingsOpenChanged` forwarders:

```csharp
    /// <summary>Whether the first-run guide owns the screen (forwards
    /// <see cref="ModalInteractionState.GuideOpen"/>).</summary>
    internal bool IsGuideOpen => _modal.GuideOpen;

    /// <summary>Raised after <see cref="IsGuideOpen"/> flips; subscribers read
    /// <see cref="IsGuideOpen"/>, not the argument.</summary>
    internal event Action<bool>? GuideOpenChanged
    {
        add => _modal.GuideOpenChanged += value;
        remove => _modal.GuideOpenChanged -= value;
    }
```

- [ ] **Step 4: Run to verify pass** — the two filters from rev 2 → PASS.

- [ ] **Step 5: Commit** — `feat(guide): GuideOpen modal flag + FixationSuppressor.Guide + offers subscription and reason` (files listed above).

---

### Task 2: `SafetySetupFlow.StartQuestions()` + flight-kind guard

**Files:** Modify `src/EyeCtrl.App/Services/SupervisionToggleService.cs`, `src/EyeCtrl.App/Services/SafetySetupFlow.cs`; Test extend `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs`.

**Interfaces:** Produces `StartQuestions()` (`_active = true; _pendingFailure = null; Advance(0);`) + `IsEnablingInFlight => _inFlight && _kind == RequestKind.Enabling`; `RequestCrashRecovery` gains `if (_supervision.Enabled && !_supervision.InFlight) return;` and attaches only when `IsEnablingInFlight`.

- [ ] **Step 1: Write the failing tests** (the harness exposes `Top`/`Pick(id)` → `.OnChosen!.Invoke()`; `_cards` is `List<(string Message, IReadOnlyList<ModalChoice> Choices)>`; helpers `WorkingSupervision`, `WorkingAsyncSupervision`, `FailingAsyncSupervision` exist):

```csharp
    [Fact]
    public void StartQuestions_AskesQuestion0_WithoutTheIntroCard()
    {
        var flow = Flow();
        flow.StartQuestions();

        Check.That(_cards).HasSize(1);
        Check.That(Top.Choices.Any(c => c.Id == "safety_yes")).IsTrue();
        Check.That(Top.Choices.Any(c => c.Id == "safety_start")).IsFalse();
    }

    [Fact]
    public void StartQuestions_Completes_AndSetsTheFlag()
    {
        var flow = Flow();
        flow.StartQuestions();
        for (var i = 0; i < 4; i++)
            Pick("safety_yes");                 // questions 0–3: Yes/No
        Pick("safety_basic");                   // question 4: Basic/All

        Check.That(_settings.General.SafetySetupDone).IsTrue();
        Check.That(_ended[^1]).IsTrue();
    }

    [Fact]
    public void CrashRecovery_Yes_WhileAlreadyEnabled_IsANoOpSuccess()
    {
        // Regression pin: passes on current code too — RequestEnable no-ops on Enabled.
        _settings.Supervision.Enabled = true;
        var (toggle, persisted) = WorkingSupervision(_settings);
        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        Pick("safety_yes");

        Check.That(_cards.Count).IsEqualTo(2);
        Check.That(persisted).IsEmpty();
    }

    /// <summary>Whose ENSURE actually takes flight: supervision ON (the ensure's gate) and a
    /// supervisor NOT connected (its other gate — r1 F2).</summary>
    private SupervisionToggleService EnsuringInFlightSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings, () => { }, () => true, _ => { }, () => { }, () => true),
            new SupervisorStartupEnsure(settings, () => { }, () => true, () => true),
            persistEnabled: _ => { }, supervisorInstalled: () => true, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: _background.Add, postToUi: w => w());

    [Fact]
    public void CrashRecovery_Yes_WithAnUnrelatedFlightInFlight_DoesNotWait()
    {
        _settings.Supervision.Enabled = true;
        var toggle = EnsuringInFlightSupervision(_settings);
        toggle.RequestEnsure();
        Check.That(toggle.InFlight).IsTrue();

        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        Pick("safety_yes");

        Check.That(_cards.Count).IsEqualTo(2);   // question 1 up: not attached to the flight
    }

    [Fact]
    public void StartQuestions_CanBeAbandoned_ByWindowClose()
    {
        var flow = Flow();
        flow.StartQuestions();
        Pick("safety_yes");
        flow.Abandon();

        Check.That(_ended).IsEmpty();
        Check.That(_settings.General.SafetySetupDone).IsFalse();
    }
```

- [ ] **Step 2: Run → FAIL. Step 3: Implement** — `IsEnablingInFlight` beside `InFlight` (:88); `RequestCrashRecovery` per the guard above; `StartQuestions()` after `Start()` (doc comment notes the accepted residual: a Yes during an in-flight Disabling leaves supervision off). **Step 4:** the filter → PASS. **Step 5:** commit `feat(guide): SafetySetupFlow.StartQuestions + supervision flight-kind guard`.

---

### Task 3: `SplashThreadHandle.WhenExited` — latched, shutdown-subscribed continuation

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs` (new, STA, no UI trait)

**Interfaces:** Produces `WhenExited(Action)` — latched; fires on the REGISTERING thread's dispatcher; the ctor subscribes `_splashDispatcher.ShutdownStarted` with the `EventHandler` signature (r3 F2) so a faulted splash thread still releases; `HasShutdownStarted` also checked at registration.

- [ ] **Step 1: Write the failing tests** — the two tests from rev 3 (the real second-dispatcher shutdown test with `ManualResetEventSlim` + thread join + pump; the attach/fire-once test with drains). Verified shapes: `ISplashWindow` fake implementing all members; `RaiseExit()` helper.

- [ ] **Step 2: Run → FAIL. Step 3: Implement** — the latch fields + `WhenExited` + `OnWindowExitFinished` from rev 3, and the constructor:

```csharp
    internal SplashThreadHandle(Dispatcher splashDispatcher, ISplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
        window.ExitFinished += OnWindowExitFinished;              // fires on the splash thread
        splashDispatcher.ShutdownStarted += (_, _) => OnWindowExitFinished();  // EventHandler (r3 F2)
    }
```

- [ ] **Step 4: Run → PASS** (the splash filter). **Step 5:** commit `feat(guide): SplashThreadHandle.WhenExited — latched, shutdown-subscribed continuation`.

---

### Task 4: The `guide` string surface — schema, validator, sorted keys, German

**Files:**
- Modify: `StringTableSchema.cs`, `tools/strings_validate.py`, `strings.json`, `src/EyeCtrl.App/Localization/strings.de.json` (+ manifest), `localization/strings.identical-allowlist.json`

**Interfaces:** Produces the `guide.*` keys below. `strings.json` keys SORTED; every key referenced as a literal in Task 12's switches; German rows `{"text", "src"}`, Sie form; identical values allowlisted.

- [ ] **Step 1:** `guide` into both `Surfaces`/`SURFACES` lists (after `"eye-care",`).

- [ ] **Step 2: English keys, SORTED** — the rev 3 sorted block stands (32 keys: `guide.calibrate.body/now/skip/title`, `guide.chrome.close`, `guide.done.body/finish/title`, `guide.dwell.body/skip/title`, `guide.dwelltime.body/continue/title`, `guide.later`, `guide.safety.begin/body/title`, `guide.theme.body/continue/title`, `guide.toolbar.body/continue/edge.bottom/edge.left/edge.right/edge.top/skip/success/title`, `guide.welcome.body/start/title`; fits: titles `modal460`, buttons `modal170`, prose `prose`; `guide.toolbar.body` uses positional `{0}` with `args: ["0"]`).

- [ ] **Step 3: German rows, Sie form** (r3 F3 — the validator rejects du-form at :43):

```json
  "guide.calibrate.body": { "text": "Stellen Sie sicher, dass der Cursor Ihren Augen folgt. Sie können später auch über Einstellungen oder die Symbolleiste kalibrieren.", "src": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar." },
  "guide.welcome.start": { "text": "Start", "src": "Start" },
  "guide.welcome.title": { "text": "Willkommen bei EyeCtrl", "src": "Welcome to EyeCtrl" }
```

(translate every key in the Sie form; `"guide.welcome.start"`'s identical value goes into `localization/strings.identical-allowlist.json`.)

- [ ] **Step 4: Manifest + refresh** — `"guide"` between `"eye-care"` and `"language-recovery"`; `python -I tools/strings_validate.py --refresh-de-src`.
- [ ] **Step 5: Parity gates** → clean. **Step 6: Commit** `feat(guide): guide string surface — sorted EN keys, Sie-form DE rows, identical allowlist`.

---

### Task 5: `FirstRunGuideFlow` + `FirstRunGuideHost` — the headless state machine

**Files:**
- Create: `src/EyeCtrl.App/Services/FirstRunGuideFlow.cs` + `FirstRunGuideHost.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` (new)

**Interfaces:** Produces `FirstRunGuideStep`; `GuideExitReason { Finished, Abandoned }`; `IGuideSurface` (`ShowStep(FirstRunGuideStep)`, `ShowSafetyCard(string, IReadOnlyList<ModalChoice>)`, `HideCard()`, `ShowCard()`, `Close()`); `FirstRunGuideFlow` — factory `Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow>` with the flow routing `showModal` to the surface; `BeginSafetyQuestions()` (the transition card's Continue — `Enter(Safety)` builds the chain but does NOT start it); `Start/Advance/Skip/Finish/Close/NotifyTaskSucceeded/NotifyCalibrationEnded/NotifySafetyEnded/ForwardSafetyChoice/WindowClosed/IsOnStep/LastStepId` + `StepEntered/ToolbarTaskEntered/ToolbarTaskExited/Closed`; `FirstRunGuideHost.TryBegin/End` + `FirstRunGuideLease`.

- [ ] **Step 1: Write the failing tests** — the full rev 2 test file with rev 3's two safety deltas (`FakeSurface` implements ALL `IGuideSurface` members recording `Shown`/`SafetyMessages`/`LastSafetyChoices`/`HideCount`/`ShowCount`/`CloseCount`; `Flow(...)` helper passes `(showModal, onEnded)` through):

```csharp
    [Fact]
    public void Start_ShowsWelcome_Then_AdvancesThroughAllSteps_ToFinish()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        var steps = new List<FirstRunGuideStep>();
        flow.StepEntered += steps.Add;
        GuideExitReason? exit = null;
        flow.Closed += reason => exit = reason;

        flow.Start();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Welcome);
        flow.Advance();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.MeetDwell);
        flow.NotifyTaskSucceeded();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Calibrate);
        flow.NotifyCalibrationEnded();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
        flow.NotifyTaskSucceeded();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Theme);
        flow.Advance();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Safety);
        Check.That(s.SafetyMessages).IsEmpty();            // transition card, no chain yet

        flow.BeginSafetyQuestions();                        // the transition card's Continue
        Check.That(s.SafetyMessages).HasSize(1);
        for (var i = 0; i < 4; i++)
            s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        s.LastSafetyChoices!.Single(c => c.Id == "safety_basic").OnChosen!.Invoke();
        Check.That(settings.General.SafetySetupDone).IsTrue();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Done);
        flow.Advance();                                     // Finish
        Check.That(exit).IsEqualTo(GuideExitReason.Finished);
        Check.That(s.CloseCount).IsEqualTo(1);
    }

    [Fact]
    public void SafetyCard_RoutesThroughTheSurface_AndForwardingRunsTheChoice()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Advance(); flow.NotifyTaskSucceeded(); flow.Advance();
        flow.NotifyCalibrationEnded(); flow.NotifyTaskSucceeded(); flow.Advance();

        flow.BeginSafetyQuestions();
        Check.That(s.SafetyMessages.Count).IsEqualTo(1);
        s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        Check.That(s.SafetyMessages.Count).IsEqualTo(2);
        Check.That(s.LastSafetyChoices!.Single(c => c.Id == "safety_no")).IsNotNull();
    }
```

plus (verbatim from rev 2, r2-verified): `NoTracker_OmitsCalibrate_AndFloatingToolbar_OmitsToolbarTask`, `Skip_WorksOnTasksAndCalibrate_Only` (with the leading `Advance()`), `Later_OnWelcome_AndChromeClose_AbandonWithoutTheFlag`, `Host_RefusesASecondFlow_WhileOneIsOpen`.

- [ ] **Step 2: Run → FAIL. Step 3: Implement `FirstRunGuideFlow.cs`** — the rev 2 body with the safety block NOT starting the chain:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>The guide's eight steps, in order (spec §6). Calibrate and ToolbarTask are omitted
/// when their preconditions fail (§7 steps 4/5).</summary>
internal enum FirstRunGuideStep
{
    Welcome, MeetDwell, DwellTime, Calibrate, ToolbarTask, Theme, Safety, Done,
}

/// <summary>Why the guide closed. Abandoned = Later/chrome Close/window closed — the flag stays
/// false unless the safety block completed; Finished = the Done card's Finish.</summary>
internal enum GuideExitReason { Finished, Abandoned }

/// <summary>The flow's window surface (spec §6): the flow knows nothing WPF; the wiring's
/// GuideSurface implements it and drives the window + view model.</summary>
internal interface IGuideSurface
{
    void ShowStep(FirstRunGuideStep step);
    /// <summary>The safety chain's showModal lands here (the flow passes it itself — ONE route).</summary>
    void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices);
    void HideCard();
    void ShowCard();
    void Close();
}

/// <summary>
/// The first-run guide's headless state machine (the SafetySetupFlow pattern). Owns the step
/// list, the skip policy, the task-success transitions, and the absorbed safety chain — whose
/// showModal the flow routes to the surface itself, so tests and production share one path.
/// </summary>
internal sealed class FirstRunGuideFlow
{
    private readonly EyeCtrlSettings _settings;
    private readonly bool _trackerPresent;
    private readonly bool _toolbarTaskAvailable;
    private readonly IGuideSurface _surface;
    private readonly Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow> _createSafetyFlow;
    private SafetySetupFlow? _safetyFlow;
    private FirstRunGuideStep _step;
    private bool _closed;

    /// <summary>Raised after each step change, BEFORE the surface shows it — the wiring toggles
    /// the toolbar hold here (§5 claim 6) and the ring lifecycle (spec §5).</summary>
    public event Action<FirstRunGuideStep>? StepEntered;
    public event Action? ToolbarTaskEntered;
    public event Action? ToolbarTaskExited;
    /// <summary>Raised once when the guide ends.</summary>
    public event Action<GuideExitReason>? Closed;

    public FirstRunGuideFlow(EyeCtrlSettings settings, bool trackerPresent, bool toolbarTaskAvailable,
        IGuideSurface surface,
        Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow> createSafetyFlow)
    {
        _settings = settings;
        _trackerPresent = trackerPresent;
        _toolbarTaskAvailable = toolbarTaskAvailable;
        _surface = surface;
        _createSafetyFlow = createSafetyFlow;
    }

    public void Start() => Enter(FirstRunGuideStep.Welcome);

    public void Advance()
    {
        switch (_step)
        {
            case FirstRunGuideStep.Welcome: Enter(Next(FirstRunGuideStep.MeetDwell)); break;
            case FirstRunGuideStep.DwellTime: Enter(Next(FirstRunGuideStep.Calibrate)); break;
            case FirstRunGuideStep.Theme: Enter(Next(FirstRunGuideStep.Safety)); break;
            case FirstRunGuideStep.Done: Finish(); break;   // the Done card's exit (r1 F4)
        }
    }

    public void Skip()
    {
        switch (_step)
        {
            case FirstRunGuideStep.MeetDwell: Enter(Next(FirstRunGuideStep.DwellTime)); break;
            case FirstRunGuideStep.Calibrate: Enter(Next(FirstRunGuideStep.ToolbarTask)); break;
            case FirstRunGuideStep.ToolbarTask: Enter(Next(FirstRunGuideStep.Theme)); break;
        }
    }

    /// <summary>Later (welcome) / chrome Close (any step) / the window closing externally.
    /// Idempotent — the window's Closed event re-enters after surface.Close().</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _safetyFlow?.Abandon();
        _surface.Close();
        Closed?.Invoke(GuideExitReason.Abandoned);
    }

    /// <summary>The Done card's Finish (spec §7 step 8): the mirror of Close with the Finished
    /// reason — without it the last card strands the user under all seven claims (r1 F4).</summary>
    public void Finish()
    {
        if (_closed) return;
        _closed = true;
        _surface.Close();
        Closed?.Invoke(GuideExitReason.Finished);
    }

    public void NotifyTaskSucceeded()
    {
        switch (_step)
        {
            case FirstRunGuideStep.MeetDwell: Enter(Next(FirstRunGuideStep.DwellTime)); break;
            case FirstRunGuideStep.ToolbarTask: Enter(Next(FirstRunGuideStep.Theme)); break;
        }
    }

    /// <summary>Calibration returned to Idle/Cancelled (NOT CompletedMessage — spec §7 step 4).</summary>
    public void NotifyCalibrationEnded()
    {
        if (_step == FirstRunGuideStep.Calibrate)
            Enter(Next(FirstRunGuideStep.ToolbarTask));
    }

    public void NotifySafetyEnded(bool completed)
    {
        if (_step != FirstRunGuideStep.Safety) return;
        _safetyFlow = null;
        Enter(Next(FirstRunGuideStep.Done));
    }

    /// <summary>The wiring forwards a fired chain choice's OnChosen (Yes/No/Basic/All). No-op
    /// outside the safety block.</summary>
    public void ForwardSafetyChoice(Action choice)
    {
        if (_step != FirstRunGuideStep.Safety) return;
        choice();
    }

    /// <summary>The transition card's Continue: starts the absorbed question chain (spec §7 step 7).
    /// No-op unless the guide is on the Safety step. Starting inside Enter(Safety) would register
    /// question 0's targets and ShowStep would then wipe them (r2 F8).</summary>
    public void BeginSafetyQuestions()
    {
        if (_step != FirstRunGuideStep.Safety) return;
        _safetyFlow?.StartQuestions();
    }

    public void WindowClosed() => Close();

    /// <summary>Step id for the close log line (ids only — privacy constraint).</summary>
    public string LastStepId => _step.ToString();

    /// <summary>Whether the guide is on <paramref name="step"/> right now (the summon handler's
    /// step gate — r1 F21).</summary>
    public bool IsOnStep(FirstRunGuideStep step) => _step == step;

    private FirstRunGuideStep Next(FirstRunGuideStep candidate)
    {
        // Spec §7: Calibrate omitted without a real tracker; ToolbarTask omitted floating/mouse.
        if (candidate == FirstRunGuideStep.Calibrate && !_trackerPresent)
            candidate = FirstRunGuideStep.ToolbarTask;
        if (candidate == FirstRunGuideStep.ToolbarTask && !_toolbarTaskAvailable)
            candidate = FirstRunGuideStep.Theme;
        return candidate;
    }

    private void Enter(FirstRunGuideStep step)
    {
        var previous = _step;
        _step = step;
        StepEntered?.Invoke(step);

        if (previous == FirstRunGuideStep.ToolbarTask && step != FirstRunGuideStep.ToolbarTask)
            ToolbarTaskExited?.Invoke();
        if (step == FirstRunGuideStep.ToolbarTask && previous != FirstRunGuideStep.ToolbarTask)
            ToolbarTaskEntered?.Invoke();

        if (step == FirstRunGuideStep.Safety)
        {
            // The flow owns the chain's showModal route (ONE path for tests and production). The
            // chain is created here but NOT started — the transition card's Continue calls
            // BeginSafetyQuestions() (r2 F8).
            _safetyFlow = _createSafetyFlow(
                (message, choices) => _surface.ShowSafetyCard(message, choices),
                completed => NotifySafetyEnded(completed));
        }

        _surface.ShowStep(step);
    }
}
```

`FirstRunGuideHost.cs` — the rev 2 body verbatim:

```csharp
namespace EyeCtrl.App.Services;

using System;

/// <summary>
/// The guide's one-flow gate (the SafetySetupHost pattern, minus the Settings-window coupling):
/// TryBegin fails while a guide is open; the lease's Dispose releases — on the normal Restore
/// path or on any exception in the raise wiring (r1 F14's fallback).
/// </summary>
internal sealed class FirstRunGuideHost
{
    private bool _open;

    public bool TryBegin(out FirstRunGuideLease lease)
    {
        if (_open) { lease = null!; return false; }
        _open = true;
        lease = new FirstRunGuideLease(this);
        return true;
    }

    internal void End() => _open = false;
}

/// <summary>Disposable marker so an exception in the wiring releases the gate.</summary>
internal sealed class FirstRunGuideLease : IDisposable
{
    private FirstRunGuideHost? _host;
    internal FirstRunGuideLease(FirstRunGuideHost host) => _host = host;
    public void Dispose() { _host?.End(); _host = null; }
}
```

- [ ] **Step 4: Run → PASS. Step 5: Commit** `feat(guide): headless flow + host — transition-gated safety block, Finish exit`.

---

### Task 6: `FirstRunGuideViewModel` — binding surface, gaze contract, latch protocol

**Files:**
- Create: `src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs`
- Modify: `tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs` (census row — **the file sits at the App.Tests root**, r3 F11) + `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` (expected map)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs` (new)

**Interfaces:** Produces the VM with: `IsVisible` (observable; the wiring sets true at Show, the test helper sets it too); **`DwellActiveWhilePaused => true`**; `DpiHelper` via `using EyeCtrl.Core.Services`; **`ChoiceRadiusDip = 70`** computed PER REGISTRATION (r2 F18); `ShowCard`; `ShowSafetyCard`/`SafetyChoices`; `RegisterChoice(id)` at origin + `Hold(id)` — **uniform: every chrome target (choices, guide_close, chips, dwell ±) registers this way** and fires `ChoiceActivated(id)` (or `CloseRequested` for `guide_close`) — r3 F5's unified list; `UpdateChoiceCentre` → `UpdateTargetCenter` + `latch.Positioned`; `ClearChoices`; `SetPracticeCentre`/`ReTimePracticeTarget`/`SetDwellTime` (live re-timing); `Set*Visible`; `Receive` with `_latch.Filter`; **`ChoiceProgress(id, 0..1)` + `PracticeProgress(0..1)` events** (r3 F7 — the window's progress elements bind to these); `OnChromeChoice(id)` (r3 F4 — the window's uniform activation route); `ChoiceMayDwell(id)` test probe.

- [ ] **Step 1: The gate entries** — `OverlayTelemetryWiringTests`' map gains `[nameof(FirstRunGuideViewModel)] = DwellContext.Other`; `DwellSurfaceCensusTests` (App.Tests root) gains the guide's row, classified like the other chrome-detector surfaces (e.g. `chrome-detector, DwellContext.Other, ToolbarButtonDwellMs` — match the table's column shape).

- [ ] **Step 2: Write the failing tests** — rev 3's tests (`Vm()` sets `IsVisible = true`; the latch test at `ToolbarButtonDwellMs = 300` with 11 real 30 ms samples onto the card after one elsewhere-sample) plus the progress events:

```csharp
    [Fact]
    public void PracticeProgress_Rises_WhileGazeRests()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 300;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        var progress = new List<double>();
        vm.PracticeProgress += p => progress.Add(p);

        long t = 0;
        for (var i = 0; i < 6; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        Check.That(progress).Not.IsEmpty();
        Check.That(progress[^1]).IsGreaterThan(0);
    }
```

- [ ] **Step 3: Run → FAIL. Step 4: Implement**

```csharp
namespace EyeCtrl.ViewModels;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Binding surface for the first-run guide's current step (spec §5/§6). The chrome detector
/// (base class) dwells at ToolbarButtonDwellMs and carries EVERY card button (step choices, the
/// chrome Close, the theme chips, the dwell ±) via the Settings-modal latch protocol: targets
/// register AT THE ORIGIN and are HELD (Hold(id), SettingsWindowViewModel.Register :186-195); the
/// window pushes each real centre through UpdateChoiceCentre (→ UpdateTargetCenter +
/// latch.Positioned, :208-212); the latch's filter rides every DwellDetector.Update (:166);
/// targets clear BEFORE a choice's action runs (:317-318). The practice target carries its OWN
/// detector at ActivationButtonDwellMs (two-knob split) with the forgiving-dwell grace, re-timed
/// live by SetDwellTime. Gaze routes through SampleMayDwell (gates !IsInGap, NOT IsPaused) —
/// DwellActiveWhilePaused is true, so the guide stays usable under user-pause (§9).
/// </summary>
public sealed partial class FirstRunGuideViewModel : DwellOverlayViewModelBase
{
    /// <summary>The choice cards' radius, in DIP (Settings' ModalBtnRadius is an App-internal —
    /// the guide declares its own, r2 F3). Computed PER REGISTRATION: a static would freeze the
    /// DPI (r2 F18).</summary>
    private const double ChoiceRadiusDip = 70;

    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _practiceDetector;
    private readonly LeaveBeforeArmLatch _latch;
    private readonly double _releaseMarginPhysical;
    private PhysicalPoint _practiceCentre;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _chromeCloseVisible;
    [ObservableProperty] private bool _practiceVisible;
    [ObservableProperty] private bool _sliderVisible;
    [ObservableProperty] private bool _themeChipsVisible;
    [ObservableProperty] private IReadOnlyList<ModalChoice>? _safetyChoices;

    /// <summary>The practice target fired (its own ActivationButtonDwellMs detector).</summary>
    public event Action? PracticeTargetActivated;
    /// <summary>Practice dwell progress, 0..1 (the window's fill element binds to this).</summary>
    public event Action<double>? PracticeProgress;
    /// <summary>The chrome Close (or Later) fired.</summary>
    public event Action? CloseRequested;
    /// <summary>A chrome target fired (a choice id, guide_close, chips, dwell ±).</summary>
    public event Action<string>? ChoiceActivated;
    /// <summary>Chrome dwell progress per id, 0..1 (the window's progress bars bind to this).</summary>
    public event Action<string, double>? ChoiceProgress;
    /// <summary>The dwell-time slider changed (clamped 200–2000 ms).</summary>
    public event Action<int>? DwellTimeChanged;
    /// <summary>A theme chip fired (theme name).</summary>
    public event Action<string>? ThemeChosen;

    internal bool DwellActiveWhilePausedForTest => DwellActiveWhilePaused;

    /// <summary>Whether the chrome detector may accumulate on <paramref name="id"/> right now —
    /// the latch's filter, exposed for tests.</summary>
    internal bool ChoiceMayDwell(string id) =>
        _latch.Filter is null || _latch.Filter(new DwellTarget(id, new PhysicalPoint(0, 0), 1, 0));

    public FirstRunGuideViewModel(IMessenger messenger, int dwellCooldownMs,
        EyeCtrlSettings settings, IStruggleTelemetrySink? sink,
        IDwellDetectorRegistry? dwellRegistry)
        : base(messenger, dwellCooldownMs, context: DwellContext.Other, sink: sink,
               dwellRegistry: dwellRegistry, pauseGraceSettings: settings.Dwell)
    {
        _settings = settings;
        _releaseMarginPhysical = DpiHelper.LogicalToPhysicalX(SettingsWindowViewModel.LatchReleaseMarginDip);
        _practiceDetector = new DwellDetector(_settings.Dwell.DwellCooldownMs, DwellContext.Other, sink,
            pauseGraceMs: _settings.Dwell.DwellPauseGraceMs) { OtherTargetReleasesHold = true };
        _practiceDetector.TargetActivated += _ =>
        {
            PracticeTargetActivated?.Invoke();
            Messenger.Send(new DwellActivationMessage());   // the base pattern (:96-101): cue the fire
        };
        _practiceDetector.TargetProgress += (_, p) => PracticeProgress?.Invoke(p);
        _latch = new LeaveBeforeArmLatch(DwellDetector,
            releaseMarginPhysical: () => _releaseMarginPhysical);
    }

    /// <summary>The two knob values the step-3 slider writes through (200–2000).</summary>
    public int DwellTimeMs => _settings.Dwell.ActivationButtonDwellMs;

    public void SetDwellTime(int ms)
    {
        _settings.Dwell.ActivationButtonDwellMs = Math.Clamp(ms, 200, 2000);
        ReTimePracticeTarget();
        DwellTimeChanged?.Invoke(_settings.Dwell.ActivationButtonDwellMs);
    }

    public void ShowCard(string title, string body, bool chromeClose)
    {
        Title = title;
        Body = body;
        ChromeCloseVisible = chromeClose;
    }

    /// <summary>The safety chain's card (spec §7 step 7): the body becomes the message and the
    /// choices are published for the wiring's rendering.</summary>
    public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
    {
        Body = message;
        SafetyChoices = choices;
    }

    /// <summary>Registers a chrome card AT THE ORIGIN and HOLDS it (the Settings-modal pattern:
    /// a target registered under a resting gaze must not arm — r1 F7). The window pushes the
    /// real centre through <see cref="UpdateChoiceCentre"/>; a fired target clears targets
    /// before the id is dispatched (the Settings precedent, :317-318).</summary>
    public void RegisterChoice(string id)
    {
        DwellDetector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0),
            DpiHelper.LogicalToPhysicalX(ChoiceRadiusDip), _settings.Dwell.ToolbarButtonDwellMs));
        _latch.Hold(id);
    }

    /// <summary>The view's layout/DPI push: real centre + the latch's positioned mark (the mark
    /// only sticks on an ALREADY-HELD target — so this must come after RegisterChoice; repeat on
    /// every DPI/display change, Q1).</summary>
    public void UpdateChoiceCentre(string id, PhysicalPoint centre)
    {
        DwellDetector.UpdateTargetCenter(id, centre);
        _latch.Positioned(id);
    }

    /// <summary>Clears every chrome target and the latch (the window calls this before
    /// re-registering a new card's set).</summary>
    public void ClearChoices()
    {
        DwellDetector.ClearTargets();
        _latch.Clear();
    }

    private void ReTimePracticeTarget()
    {
        _practiceDetector.RemoveTarget("guide_practice");
        if (PracticeVisible)
            _practiceDetector.AddTarget(new DwellTarget("guide_practice", _practiceCentre,
                radius: DpiHelper.LogicalToPhysicalX(60), dwellTimeMs: _settings.Dwell.ActivationButtonDwellMs));
    }

    /// <summary>The view pushes the practice target's real centre at layout and on DPI change
    /// (r1 F8 — an absolute top-left coordinate would park the target off the card).</summary>
    public void SetPracticeCentre(PhysicalPoint centre)
    {
        _practiceCentre = centre;
        ReTimePracticeTarget();
    }

    public void SetPracticeVisible(bool visible)
    {
        PracticeVisible = visible;
        ReTimePracticeTarget();
    }

    public void SetSliderVisible(bool visible) => SliderVisible = visible;
    public void SetThemeChipsVisible(bool visible) => ThemeChipsVisible = visible;

    internal void CloseRequestedFire() => CloseRequested?.Invoke();
    internal void ThemeChosenFire(string name) => ThemeChosen?.Invoke(name);

    /// <summary>The window's uniform chrome-activation route (r3 F4): every card button the
    /// window builds fires this with its id.</summary>
    internal void OnChromeChoice(string id) => ChoiceActivated?.Invoke(id);

    /// <summary>Per-sample feed: the base detector (chrome, latch-filtered) AND the practice
    /// detector, both through the shared gate — a gap-held sample accumulates nowhere.</summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);
        if (IsVisible && SampleMayDwell(message))
        {
            _latch.Update(message.Position);
            DwellDetector.Update(message.Position, message.TimestampMicroseconds, _latch.Filter);
            if (PracticeVisible)
                _practiceDetector.Update(message.Position, message.TimestampMicroseconds);
        }
    }

    protected override void OnTargetActivated(string targetId)
    {
        DwellDetector.ClearTargets();       // Settings precedent: clear before dispatching (:317-318)
        _latch.Clear();
        if (targetId == "guide_close") { CloseRequested?.Invoke(); return; }
        ChoiceActivated?.Invoke(targetId);
    }

    /// <summary>Chrome dwell progress per id, 0..1 (the window's progress elements bind to this).</summary>
    protected override void OnTargetProgress(string targetId, double progress)
        => ChoiceProgress?.Invoke(targetId, progress);
}
```

- [ ] **Step 5: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests|FullyQualifiedName~DwellSurfaceCensus"` → PASS; `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring"` → PASS.

- [ ] **Step 6: Commit** — files: the VM, the two test files at their REAL paths (`tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs` at the root, r3 F11), the new VM tests:

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideViewModel — unified chrome targets, latch protocol, progress events"
```

---

### Task 7: `FirstRunGuideWindow` — code-built visuals with real centres, progress bindings

**Files:**
- Create: `src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml` + `.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row, `exempt:`)
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`RaiseToTop()` default method on `IToolbarOverlay`, `ICursorCircleOverlay`, `IPauseIndicatorOverlay`)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` (new, STA, `[Trait("Category", "UI")]`)

**Interfaces:** Produces the window with: fullscreen geometry (read live); `ShowCardContent`; **`SetChoices(IReadOnlyList<(string Label, string Id)>)` building Border cards with the modal button brushes AND a per-card progress element, registering each through `vm.RegisterChoice(id)`, pushing real `PointToScreen` centres**; `SetPractice(bool)` building a stroked ellipse with a fill-progress element and pushing its centre; `SetDwellKnob(bool)` building the value read-out + −/+ chrome cards (r3 F4: `DwellSlider` is an `IDwellComposite` needing an `IDwellRegistrar` — not usable as a control); `SetThemeChipsVisible(bool)`; `HideGuide()/ShowGuide()`; `CardResources` (the theme preview's target); NO `Close()` override.

- [ ] **Step 1: XAML** — the choice row is a `StackPanel` (r2 F5):

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.FirstRunGuideWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
                        IsClickThrough="True"
                        WindowStyle="None" AllowsTransparency="True"
                        ShowInTaskbar="False" ShowActivated="False"
                        Topmost="True" ResizeMode="NoResize">
    <Grid>
        <Border x:Name="Card" Width="560" Height="440"
                Background="{DynamicResource BrushSurfaceDark}"
                CornerRadius="{DynamicResource LargeCornerRadius}"
                HorizontalAlignment="Center" VerticalAlignment="Center">
            <Grid Margin="32">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <TextBlock x:Name="TitleText" Grid.Row="0" FontSize="24" FontWeight="Medium"
                           Text="{Binding Title}" Foreground="{DynamicResource BrushTextPrimary}"/>
                <TextBlock x:Name="BodyText" Grid.Row="1" Margin="0,10,0,0" TextWrapping="Wrap"
                           Text="{Binding Body}" Foreground="{DynamicResource BrushTextPrimary}"/>
                <ContentControl x:Name="StepContent" Grid.Row="2" Margin="0,16,0,0"/>
                <StackPanel x:Name="ChoiceRow" Grid.Row="3" Orientation="Horizontal"
                            HorizontalAlignment="Right"/>
            </Grid>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Code-behind** — visuals with brushes, progress elements, and the centre push:

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window (spec §5): fullscreen click-through backdrop with a centered
/// interactive card. RENDER-ONLY for text; the window BUILDS the step visuals (choice cards,
/// practice ellipse, dwell knob, theme chips) and pushes every dwell target's real centre into
/// the VM after layout — PointToScreen yields device (physical) pixels (r2 F9). Cards carry the
/// modal button brushes and a per-card progress bar bound to the VM's ChoiceProgress (r3 F7).
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;
    private readonly Dictionary<string, FrameworkElement> _choiceCards = new();
    private readonly Dictionary<string, Rectangle> _progressBars = new();
    private Ellipse? _practiceFill;
    private TextBlock? _dwellValue;

    public FirstRunGuideWindow(FirstRunGuideViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // Progress bindings (r3 F7): the cards' bars and the practice fill follow the VM.
        vm.ChoiceProgress += (id, p) => SetProgressBar(id, p);
        vm.PracticeProgress += p => SetPracticeFill(p);
    }

    /// <summary>The theme preview's card-local target (Task 12 applies the resolved theme here;
    /// DynamicResource on the card's brushes picks it up, Tokens.Current stays untouched).</summary>
    public ResourceDictionary CardResources => Card.Resources;

    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowCard(title, body, chromeClose);

    /// <summary>Builds one visual card per (label, id) pair — background, border, label,
    /// progress bar — and registers each as a chrome dwell target through the VM (Task 6); then
    /// pushes the laid-out centres (r3 F5: EVERY card gets a visual and a centre).</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id)> choices)
    {
        _vm.ClearChoices();
        _choiceCards.Clear();
        _progressBars.Clear();
        ChoiceRow.Children.Clear();
        foreach (var (label, id) in choices)
        {
            var bar = new Rectangle
            {
                Height = 3,
                Width = 0,
                Fill = (Brush)FindResource("BrushAccent"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var text = new TextBlock
            {
                Text = label,
                Margin = new Thickness(12, 8, 12, 4),
                Foreground = (Brush)FindResource("BrushTextPrimary"),
            };
            var card = new Border
            {
                Child = new StackPanel { Children = { text, bar } },
                Margin = new Thickness(8, 0, 0, 0),
                Background = (Brush)FindResource("BrushSurfaceMedium"),
                BorderBrush = (Brush)FindResource("BrushAccent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            _choiceCards[id] = card;
            _progressBars[id] = bar;
            ChoiceRow.Children.Add(card);
            _vm.RegisterChoice(id);
        }
        PushCentres();
    }

    /// <summary>The practice target: a stroked circle with a fill element whose size follows the
    /// VM's PracticeProgress (r3 F7 — MeetDwell's lesson is "the ring fills").</summary>
    public void SetPractice(bool visible)
    {
        _vm.SetPracticeVisible(visible);
        if (visible)
        {
            _practiceFill = new Ellipse
            {
                Width = 0,
                Height = 0,
                Fill = (Brush)FindResource("BrushAccent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ring = new Ellipse
            {
                Width = 120,
                Height = 120,
                Stroke = (Brush)FindResource("BrushAccent"),
                StrokeThickness = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid();
            grid.Children.Add(ring);
            grid.Children.Add(_practiceFill);
            StepContent.Content = grid;
        }
        else
        {
            StepContent.Content = null;
            _practiceFill = null;
        }
        PushCentres();
    }

    /// <summary>The dwell knob (r3 F4): DwellSlider is an IDwellComposite needing an
    /// IDwellRegistrar — not usable as a control. A value read-out plus −/+ chrome cards, whose
    /// ids the wiring maps to SetDwellTime(∓100).</summary>
    public void SetDwellKnob(bool visible)
    {
        _vm.SetSliderVisible(visible);
        if (visible)
        {
            _dwellValue = new TextBlock
            {
                Text = $"{_vm.DwellTimeMs} ms",
                FontSize = 20,
                Foreground = (Brush)FindResource("BrushTextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(_dwellValue);
            StepContent.Content = panel;
            _vm.RegisterChoice("guide_dwell_minus");
            _vm.RegisterChoice("guide_dwell_plus");
            _choiceCards["guide_dwell_minus"] = BuildSmallCard("−", "guide_dwell_minus");
            _choiceCards["guide_dwell_plus"] = BuildSmallCard("+", "guide_dwell_plus");
            ChoiceRow.Children.Add(_choiceCards["guide_dwell_minus"]);
            ChoiceRow.Children.Add(_choiceCards["guide_dwell_plus"]);
            vm_DwellTimeChangedSubscription();
        }
        else
        {
            StepContent.Content = null;
        }
        PushCentres();
    }

    private void vm_DwellTimeChangedSubscription()
    {
        // Keep the read-out live (unsubscribe happens when the content is replaced).
        void OnChanged(int ms)
        {
            if (_dwellValue is { } value) value.Text = $"{ms} ms";
            else _vm.DwellTimeChanged -= OnChanged;
        }
        _vm.DwellTimeChanged += OnChanged;
    }

    private Border BuildSmallCard(string glyph, string id)
    {
        var bar = new Rectangle
        {
            Height = 3,
            Width = 0,
            Fill = (Brush)FindResource("BrushAccent"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _progressBars[id] = bar;
        return new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = glyph, FontSize = 18, Foreground = (Brush)FindResource("BrushTextPrimary"), HorizontalAlignment = HorizontalAlignment.Center },
                    bar,
                },
            },
            Margin = new Thickness(8, 0, 0, 0),
            Background = (Brush)FindResource("BrushSurfaceMedium"),
            BorderBrush = (Brush)FindResource("BrushAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
        };
    }

    private void SetProgressBar(string id, double progress)
    {
        if (_progressBars.TryGetValue(id, out var bar) && _choiceCards.TryGetValue(id, out var card))
            bar.Width = (card.ActualWidth - 2) * Math.Clamp(progress, 0, 1);
    }

    private void SetPracticeFill(double progress)
    {
        if (_practiceFill is { } fill)
        {
            var size = 116 * Math.Clamp(progress, 0, 1);   // inside the 120 ring
            fill.Width = size;
            fill.Height = size;
        }
    }

    public void SetThemeChipsVisible(bool visible) => _vm.SetThemeChipsVisible(visible);

    /// <summary>Step 4 hides the WHOLE window (the calibration window takes the screen) and the
    /// advance re-shows it — through the window's own Show/Hide path, never raw Visibility
    /// writes (r1 F26).</summary>
    public void HideGuide() => Hide();
    public void ShowGuide() => Show();

    /// <summary>Pushes every registered target's REAL centre (device px via PointToScreen) into
    /// the VM. Runs after a layout pass; the dispatcher round-trip lets a fresh card's layout
    /// complete first (r2 F9). Card is fixed-size, so SizeChanged never fires — an explicit push
    /// per build is the mechanism.</summary>
    private void PushCentres()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            foreach (var (id, card) in _choiceCards)
            {
                if (!card.IsVisible) continue;
                var centre = card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
            if (StepContent.Content is Grid { Children.Count: > 0 } grid
                && grid.Children[0] is Ellipse ring && ring.IsVisible)
            {
                var centre = ring.PointToScreen(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2));
                _vm.SetPracticeCentre(new PhysicalPoint(centre.X, centre.Y));
            }
        });
    }

    public void HideCard() => HideGuide();
    public void ShowCard() => ShowGuide();
    public void Close() { }   // never overridden behaviour: the surface's Close maps to window Close via HideGuide/Close() — see GuideSurface
}
```

(The `DwellSlider` control is NOT used — r3 F4. The `−`/`+` ids map in `OnGuideChoice` to `vm.SetDwellTime(vm.DwellTimeMs - 100 / + 100)`. The chip visuals ride the unified `SetChoices` list on the Theme step. `FindResource` resolves the card-local dictionary after the theme preview applies — Task 12 Step 4.)

- [ ] **Step 3: Overlay inventory row** (`exempt:`) + **Step 4: `IOverlayWindows.cs`** — the default method on THREE interfaces:

```csharp
    /// <summary>Raises this window above any later-created fullscreen topmost overlay (the
    /// guide). Default no-op so the test fakes need no member; the real OverlayWindow's
    /// implementation (:377) overrides it.</summary>
    void RaiseToTop() { }
```

- [ ] **Step 5: STA test** — the rev 3 test with `[Trait("Category", "UI")]` and complete usings.

- [ ] **Step 6: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideWindowTests|FullyQualifiedName~OverlayInventoryDrift"` → PASS; `--filter "FullyQualifiedName~Toolbar"` → PASS.

- [ ] **Step 7: Commit** `feat(guide): window builds brushed visuals with progress + real centres; RaiseToTop on three interfaces`.

---

### Task 8: `ToolbarAutoHideController` — guide-hidden hold + `ResetToHidden`

**Files:** Modify `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`; Test extend `ToolbarAutoHideControllerTests.cs` (harness: `NewController` at :755, `Attach`, `FakeVisibility` with `ShowCalls/HideCalls/ForceShowCalls/SetActiveCalls`).

**Interfaces:** `SetGuideHiddenHold(bool)` (suppress `ForceShow` + `ReassertVisibility`'s `SetActive(true)` re-arm, force Hidden) + `ResetToHidden()` (mirror `ResetToVisible()` at :551). **Order rule (r1 F11): the hold arms BEFORE any `SetSuspended(true)`.**

- [ ] **Step 1: Write the failing tests** — rev 3's three tests (`GuideHiddenHold_SuppressesTheAsyncForceShow_FromSuspend` reaching Hidden via the file's off-toolbar sample loop then hold→suspend→assert `ForceShowCalls == 0`; `GuideHiddenHold_NeutralizesReassertVisibility` asserting `SetActiveCalls` empty; `ResetToHidden_PutsTheControllerInHidden_ForAnImmediateSummon` asserting `StateName` + `HideCalls ≥ 1` + an immediate summon).

- [ ] **Step 2: Implement** — the rev 2/3 code verbatim:

```csharp
    // The guide-hidden hold (spec §5 claim 6): the first-run guide hides the toolbar on steps
    // 1–4/6–8. While held, the two shipped paths that would resurrect a hidden toolbar — the
    // async ForceShow inside SetSuspended, and ReassertVisibility's else-branch SetActive(true)
    // (calibration transitions) — are suppressed; release restores unconditionally (r4 F5).
    // ORDER: arm the hold BEFORE SetSuspended(true) — SetSuspended posts its ForceShow when
    // Hidden, and a hold armed after loses that race (r1 F11).
    private bool _guideHiddenHold;

    /// <summary>The first-run guide's hold: while true the controller stays hidden and deaf to
    /// ForceShow/ReassertVisibility re-arms, whatever SetSuspended says.</summary>
    public void SetGuideHiddenHold(bool held)
    {
        _guideHiddenHold = held;
        if (held)
        {
            _state = State.Hidden;
            _visibility?.HideToolbar(_paused);
            Volatile.Write(ref _hideAnchorUs, Unseeded);
            Volatile.Write(ref _summonRunUs, Unseeded);
            _lastValidHiddenUs = null;
        }
    }

    /// <summary>Mirrors <see cref="ResetToVisible"/>: state → Hidden, anchors cleared, hidden via
    /// the adapter. Step-5 entry uses this so summon detection is live immediately (r4 F2).</summary>
    public void ResetToHidden()
    {
        _state = State.Hidden;
        _visibility?.HideToolbar(_paused);
        Volatile.Write(ref _hideAnchorUs, Unseeded);
        Volatile.Write(ref _summonRunUs, Unseeded);
        _lastValidHiddenUs = null;
    }
```

and the two suppression points:

```csharp
    // in SetSuspended's suspended branch, replacing the ForceShow line:
            if (_state == State.Hidden && _enabled && !_guideHiddenHold)
                _visibility?.ForceShow();

    // in ReassertVisibility:
        if (_state == State.Hidden && _enabled && !_suspended && !_guideHiddenHold)
            _visibility.HideToolbar(_paused);   // re-asserts hide AND SetActive(false)
        else if (!_guideHiddenHold)
            _visibility.SetActive(true);
```

- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ToolbarAutoHideController guide-hidden hold + ResetToHidden`.

---

### Task 9: Toolbar wiring — ordered hold, feed terms, `OnTargetActivated` gate

**Files:** Modify `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`, `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`; Test extend `ToolbarLifecycleCoordinatorTests.cs` (incl. the :361-376 source pins).

**Interfaces:** `SetGuideHold(bool)` — **hidden hold FIRST, then `SetAutoHideSuspended("guide", …)`** (r1 F11); `ResetToolbarForGuideTask()`; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` + `guideOpen` (all call sites + source pins); `ToolbarViewModel.GuideOpenGate` (assigned in Task 12, checked AFTER the :537-541 progress/highlight reset lines); `ToolbarVisibilityChanged` REUSED (:52/:366). The gated press does not stop the dwell cue (the base publishes after `OnTargetActivated` returns) — accepted, r3 F19.

- [ ] **Step 1: Failing tests** — rev 3's two feed tests.

- [ ] **Step 2: Implement** — the rev 3 code blocks verbatim:

```csharp
    internal static bool ShouldFeedKeyboardSample(bool paused, bool modalKeyboardVisible, bool keyboardIsVisible,
        bool isInGap, bool probeOpen, bool exerciseActive, bool guideOpen) =>
        !paused && modalKeyboardVisible && keyboardIsVisible && !isInGap && !probeOpen && !exerciseActive && !guideOpen;

    internal static bool ShouldFeedSettingsSample(bool exerciseActive, bool guideOpen) =>
        !exerciseActive && !guideOpen;
```

```csharp
    /// <summary>The guide's toolbar hold. ORDER (r1 F11): the hidden hold arms FIRST —
    /// SetSuspended posts an async ForceShow when Hidden, and a hold armed after loses the race.
    /// Release (guide close) is the coordinator's ForceShow() restore path, not this.</summary>
    public void SetGuideHold(bool held)
    {
        _autoHide?.SetGuideHiddenHold(held);
        SetAutoHideSuspended("guide", held);
    }

    /// <summary>Step-5 entry: release the hold and reset the controller to Hidden so the summon
    /// detection is live immediately (r4 F2).</summary>
    public void ResetToolbarForGuideTask()
    {
        _autoHide?.SetGuideHiddenHold(false);
        SetAutoHideSuspended("guide", false);
        _autoHide?.ResetToHidden();
    }
```

and `ToolbarViewModel`'s `GuideOpenGate` property + the in-method gate after the :537-541 lines.

- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ordered toolbar hold + GuideOpen feed terms + OnTargetActivated gate`.

---

### Task 10: Ring lifecycle — the coordinators' gate + restore

**Files:** Modify `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`, `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs`; Test extend the coordinators' existing test files.

**Interfaces:** `OverlayVmBindingCoordinator.RingMayReshow => !_modal.GuideOpen` (the message coordinator has NO `_modal` — its ctor takes `(toolbar, vmBinding)`, `EventCoordinator.cs:212`); both calibration re-show sites consult it. The FULL ring lifecycle (hide on open, step-5 show, hide on exit, restore on close) lives in Task 12's wiring (r2 F15).

- [ ] **Step 1: Failing test** — the flag test (rev 3's `RingMayReshow_IsFalse_WhileGuideOpen`).
- [ ] **Step 2: Implement**

```csharp
    /// <summary>Whether the ring may re-show right now — false while the first-run guide owns
    /// the screen (the guide hides the ring and re-shows it itself: step 5 only, spec §5).</summary>
    internal bool RingMayReshow => !_modal.GuideOpen;
```

`OverlayVmBindingCoordinator`'s `IsVisible` case gains the gate:

```csharp
            case nameof(CursorCircleViewModel.IsVisible):
                if (Context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen && !_modal.GuideOpen)
                    cursorCircle.Show();
                else
                    cursorCircle.Hide();
                break;
```

`OverlayMessageCoordinator`'s two calibration `cursorCircle.Show()` sites (:308/:318) consult the binding coordinator (the field name is the ctor-injected binding coordinator — match the actual field):

```csharp
                        if (Context.CursorVM.IsVisible && _vmBinding.RingMayReshow) cursorCircle.Show();
```

- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ring re-show gated on GuideOpen via the binding coordinator's flag`.
- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ring re-show gated on GuideOpen via the binding coordinator's flag`.

---

### Task 11: Proactive surfaces — BannerSlot hold, hosts, SetupNoticeWatch

**Files:** Modify `BannerSlot.cs`, `RestartNoticeHost.cs`, `LagMarkHost.cs`, `SetupNoticeWatch.cs`; Test `BannerSlotHoldTests.cs` (new) + host extensions.

**Interfaces:** `BannerSlot.Hold()/Release()` (front re-queue; `Preempt` deliberately bypasses — caregiver alerts take the slot mid-guide, pinned by test); optional `Func<bool>? isGuideOpen = null` on both hosts + `OnGuideOpenChanged` (defer-newest replay); `SetupNoticeWatch`: `CheckStartupWhenGuideClosed()` at the three call sites + expiry skip while GuideOpen + close-time poke.

- [ ] **Step 1: Failing tests** — rev 3's three BannerSlot tests (incl. `Preempt_BypassesTheHold_CaregiverAlertsAreTheSafetyChannel`).

- [ ] **Step 2: Implement**

`BannerSlot.cs` — the hold:

```csharp
    // The guide's hold (spec §5 claim 4): while held, the live banner hides and re-queues at the
    // FRONT (it resumes first) and dequeue pauses — nothing is dropped, `_live` cannot wedge
    // because the hold clears it. Preempt deliberately bypasses the hold: caregiver alerts are
    // the safety channel and take the slot even mid-guide (pinned by test). UI thread only.
    private bool _held;

    /// <summary>Hold for a fullscreen surface (the first-run guide). Idempotent.</summary>
    public void Hold()
    {
        if (_held) return;
        _held = true;
        if (_live is { } live)
        {
            _live = null;
            live.Closed -= OnClosed;
            try
            {
                live.HideFromSlot();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A held banner failed to hide; it still re-queues.");
            }
            _queue.Insert(0, live);
        }
    }

    /// <summary>Release: dequeue resumes at once with the front of the queue (nothing dropped).
    /// A live pre-emptor is untouched.</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        ShowNextIfFree();
    }
```

and the dequeue guard in `ShowNextIfFree`:

```csharp
        while (!_held && _live is null && _queue.Count > 0)
```

`RestartNoticeHost.cs` — ctor gains an OPTIONAL `Func<bool>? isGuideOpen = null` (wired `() => _eventCoordinator.IsGuideOpen` at App.xaml.cs:519); the three show paths' guards gain `|| (_isGuideOpen?.Invoke() ?? false)`, with the blocked call stashed for replay:

```csharp
    private readonly Func<bool>? _isGuideOpen;
    private Action? _pendingOnGuideClosed;

    /// <summary>Guide opened or closed. Opening takes a notice down (it covers the card);
    /// closing replays at most ONE stashed show — the newest wins, an older notice is stale
    /// (defer, never drop — r3 F4).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (open) _presenter.HideNotice();
        else
        {
            var pending = _pendingOnGuideClosed;
            _pendingOnGuideClosed = null;
            pending?.Invoke();
        }
    }
```

(each show path: `if (_isSettingsOpen() || _isProbeOpen()) return; if (_isGuideOpen?.Invoke() == true) { _pendingOnGuideClosed = () => ShowInProgress(); return; } ShowInProgress();` — the stash captures the path's own continuation.)

`LagMarkHost.cs` — optional `Func<bool>? isGuideOpen = null` (App.xaml.cs:605), `CanShow()` gains `&& (_isGuideOpen?.Invoke() ?? false) == false`, and:

```csharp
    /// <summary>Guide opened or closed — the button hides and returns like Settings does (r3 F4).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (open || (_isGuideOpen?.Invoke() ?? false))
            _postToUi(HideButtonAndZone);
        else if (CanShow())
            _postToUi(ShowButtonAndZone);
    }
```

`SetupNoticeWatch.cs` — optional `Func<bool>? isGuideOpen = null` (App.xaml.cs:577); ONE gated helper replaces the three raw `CheckStartup()` call sites (Start :91, Poll :110, Poke :136 — each becomes `if (CheckStartupWhenGuideClosed())`, keeping its surrounding schedule/flow), and the expiry check also skips while the guide is open (the 90 s window must not run out behind the guide — r1 F24; `Finish()` would make the close-time poke a no-op):

```csharp
    /// <summary>r4 F3 + r1 F24: CheckStartup writes setup-notice-shown.json BEFORE _show (no burn
    /// behind the guide), and the expiry window must not run out while the guide holds the
    /// screen — both skip while GuideOpen; the close-time poke shows then.</summary>
    private bool GuideOpen => _isGuideOpen?.Invoke() ?? false;

    private bool CheckStartupWhenGuideClosed() => !GuideOpen && _service.CheckStartup();
```

plus:

```csharp
    /// <summary>Guide opened or closed: closing pokes once (r4 F3).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (!open) Poke();
    }
```

- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): BannerSlot hold (preempt exempted) + defer hosts + gated SetupNoticeWatch`.

---

### Task 12: Startup wiring — per-run raise, claims, gaze input, real visuals, retirement

**Files:**
- Modify: `InteractionMessages.cs` (rename), `SafetySetupFlow.cs` (message + retire `Launch`/`Host`), `SettingsUiCoordinator.cs`, `EventCoordinator.cs` (forwarders), `App.xaml.cs`, `SettingsWindow.xaml.cs`, `SettingsPageContext.cs`, `GeneralPageSource.cs`
- Modify tests: `SafetySetupFlowTests.cs` (:78-170 + `RequestProbe` :656), `TestSettingsPageContext.cs:46`, `SimpleModeCurationTests.cs:103/:108`, `localization/settings-row-english.golden.json:1177`, `SafetySetupWiringTests`, `GeneralPageSourceTests`, `StatusCardPresenterTests` (:735-736 pin)

**Interfaces:** Consumes Tasks 1–11. Produces the complete raise with the UNIFIED choice list (r3 F5) and the complete unwind.

- [ ] **Step 1: Rename the message** — `FirstRunGuideRequestedMessage` replaces `SafetySetupRequestedMessage` (delete the old record; `SafetySetupStartup.MaybeRequest` sends the new one).

- [ ] **Step 2: The per-run factory in `SettingsUiCoordinator`** — fields:

```csharp
    private FirstRunGuideHost? _guideHost;
    private SplashThreadHandle? _splashHandle;
    private FirstRunGuideWindow? _guideWindow;
    private FirstRunGuideViewModel? _guideVm;
    private FirstRunGuideFlow? _guideFlow;
    private GuideSurface? _guideSurface;
    private Action<bool>? _onToolbarVisibility;   // a FIELD, not a local function (r3 F6)
```

Registration + `SetSplashHandle` + `StartFirstRunGuide` (with the LOGGED unwind, r2 F14) + `BuildAndRaiseGuide`:

```csharp
    private void StartFirstRunGuide()
    {
        var context = Context!;
        if (_guideHost is not null) return;

        var host = new FirstRunGuideHost();
        if (!host.TryBegin(out var lease)) return;
        _guideHost = host;

        try
        {
            BuildAndRaiseGuide(context, lease);
        }
        catch (Exception ex)
        {
            // r2 F14: log instead of rethrowing — inside a BeginInvoke'd callback a rethrow
            // lands in DispatcherUnhandledException during a first-run startup.
            Log.Error(ex, "First-run guide: raise failed; unwinding");
            UnwindPartialRaise(context, lease);
        }
    }

    /// <summary>The raise-failure unwind: undo whatever BuildAndRaiseGuide/Show did, then hand
    /// the session back — toolbar restored, ring restored, lease released, cards started
    /// (r3 F9's additions). No re-entry: the flow is nulled BEFORE the window closes, and the
    /// toolbar handler is unsubscribed first.</summary>
    private void UnwindPartialRaise(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        if (_onToolbarVisibility is not null)
            _toolbar.ToolbarVisibilityChanged -= _onToolbarVisibility;
        context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
        context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);
        _modal.GuideOpen = false;
        _quickActions.SetSummonSuspended("guide", false);
        _toolbar.SetGuideHold(false);
        _toolbar.ForceShow();                          // r3 F9: floating/mouse users get the toolbar back
        if (context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen)
            context.OverlayManager.CursorCircle.Show();           // r3 F9: the ring comes back
        Log.Information("First-run guide: closed (step=raise-failed, completed=false)");
        _guideFlow = null;                             // BEFORE window.Close: no re-entry (r3 F9)
        (_guideVm as IDisposable)?.Dispose();
        _guideVm = null;
        if (_guideWindow is not null) { _guideWindow.Close(); _guideWindow = null; }
        _guideSurface = null;
        _guideHost = null;
        lease.Dispose();
        StatusCardsFallback?.Invoke();
        StatusCardsFallback = null;
    }

    /// <summary>App assigns this at startup; the unwind invokes it so the status-card presenter
    /// starts even when the raise failed (r3 F6 / r2 F14).</summary>
    internal Action? StatusCardsFallback { get; set; }

    private void BuildAndRaiseGuide(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        var settings = context.Settings;
        bool tracker = App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool toolbarTask = tracker && settings.Toolbar.IsDocked;

        _guideVm = new FirstRunGuideViewModel(context.Messenger,
            dwellCooldownMs: settings.Dwell.DwellCooldownMs,
            settings, sink: null, dwellRegistry: null);
        _guideWindow = new FirstRunGuideWindow(_guideVm);
        _guideSurface = new GuideSurface(_guideWindow, _guideVm, this);

        _guideFlow = new FirstRunGuideFlow(settings, trackerPresent: tracker,
            toolbarTaskAvailable: toolbarTask,
            surface: _guideSurface,
            createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, _supervisionToggle,
                showModal,
                applyAndSave: () =>
                {
                    OnSettingsChanged(settings);                     // the live-apply path (r2 F6/Q2)
                    context.SettingsService.SaveDebounced(settings); // the DEBOUNCED save
                },
                setSimpleSettingsUi: v => settings.General.SimpleSettingsUi = v,
                onEnded));

        _guideVm.IsVisible = true;                                  // gaze input lives with IsVisible
        _guideVm.ChoiceActivated += id => context.Dispatcher.BeginInvoke(() => OnGuideChoice(id));
        _guideVm.CloseRequested += () => context.Dispatcher.BeginInvoke(_guideFlow.Close);
        _guideVm.PracticeTargetActivated += () => context.Dispatcher.BeginInvoke(_guideFlow.NotifyTaskSucceeded);
        _guideVm.ThemeChosen += name => context.Dispatcher.BeginInvoke(() =>
        {
            var theme = ThemeRegistry.Resolve(name);
            ThemeResourceApplier.Apply(_guideWindow!.CardResources, theme);   // card-local preview (r1 F2)
            settings.Theme.Name = name;
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
        });

        // Dwell time: live-apply + save (r2 F16); the −/+ chrome cards map here:
        _guideVm.DwellTimeChanged += _ =>
        {
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
        };
        context.ToolbarVM.GuideOpenGate = () => _modal.GuideOpen;   // claim 7 wired (r1 F12)

        void Show()
        {
            try
            {
                context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Guide);   // claim 1
                _modal.GuideOpen = true;                   // claim 2
                _quickActions.SetSummonSuspended("guide", true);   // claim 5
                _toolbar.SetGuideHold(true);               // claim 6 — hold arms BEFORE suspend (r1 F11)
                context.OverlayManager.CursorCircle.Hide();            // the ring hides (spec §5) — INSIDE Show
                Log.Information("First-run guide: shown");
                _guideWindow!.ShowGuide();
                _guideFlow!.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "First-run guide: show failed; unwinding");
                UnwindPartialRaise(context, lease);
            }
        }
        if (_splashHandle is { } splash) splash.WhenExited(Show);
        else Show();

        // Step toggles (claim 6(d)) + the ring's step-5 lifecycle (r3 F8: SHOW then raise).
        _guideFlow.StepEntered += step => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen) return;
            if (step == FirstRunGuideStep.ToolbarTask)
            {
                _toolbar.ResetToolbarForGuideTask();       // hold released + controller Hidden
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.Show();       // SHOW (RaiseToTop alone can't)
                context.OverlayManager.Toolbar.RaiseToTop();          // Guide < Toolbar
                context.OverlayManager.PauseIndicator.RaiseToTop();   // < PauseIndicator
                context.OverlayManager.CursorCircle.RaiseToTop();     // < ring
            }
            else
            {
                _toolbar.SetGuideHold(true);               // non-task steps hold again
                context.OverlayManager.CursorCircle.Hide();           // step 5 ended: ring hidden again
            }
        });

        // The summon observation — a PRIVATE METHOD FIELD-SUBSCRIBED (r3 F6), gated on the task,
        // switching the card to the success text (spec §7.5), unsubscribed in Restore (r1 F21).
        _onToolbarVisibility = visible =>
        {
            if (_guideFlow is null || !_modal.GuideOpen || !visible) return;
            context.Dispatcher.BeginInvoke(() =>
            {
                if (_guideFlow is null || !_guideFlow.IsOnStep(FirstRunGuideStep.ToolbarTask)) return;
                context.OverlayManager.Toolbar.RaiseToTop();
                context.OverlayManager.PauseIndicator.RaiseToTop();
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.Show();       // show + raise (r3 F8)
                context.OverlayManager.CursorCircle.RaiseToTop();
                _guideWindow?.ShowCardContent(
                    StringTable.Current.T("guide.toolbar.success"),
                    _guideVm?.Body ?? string.Empty, chromeClose: true);
                _guideSurface?.RenderChoicesForCurrentStep();         // Continue (+chrome close) re-armed
            });
        };
        _toolbar.ToolbarVisibilityChanged += _onToolbarVisibility;

        void Restore(GuideExitReason reason)
        {
            _toolbar.ToolbarVisibilityChanged -= _onToolbarVisibility;   // no handler pile-up
            context.Messenger.Unregister<CalibrationStateChangedMessage>(this);  // always (r1 F17)
            context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);  // claim 1
            _modal.GuideOpen = false;
            _quickActions.SetSummonSuspended("guide", false);
            _toolbar.SetGuideHold(false);
            _toolbar.ForceShow();                          // ForceShow + ResetToVisible — every mode (r4 F5)
            if (context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen)
                context.OverlayManager.CursorCircle.Show();           // ring restore (spec §5)
            Log.Information("First-run guide: closed (step={Step}, completed={Completed})",
                _guideFlow?.LastStepId ?? "done", reason == GuideExitReason.Finished);
            (_guideVm as IDisposable)?.Dispose();
            _guideFlow = null;                             // BEFORE window.Close: no re-entry
            _guideVm = null;
            _guideWindow?.Close();
            _guideWindow = null;
            _guideSurface = null;
            _guideHost = null;
            StatusCardsFallback = null;                    // no double-start on a later failed re-run (r2 F19)
            lease.Dispose();
        }
        _guideFlow.Closed += reason => context.Dispatcher.BeginInvoke(() => Restore(reason));
        _guideWindow.Closed += (_, _) => _guideFlow?.WindowClosed();   // external close = abandon
    }
```

(The `GuideSurface.RenderChoicesForCurrentStep()` helper re-runs `ShowStep(_step)`'s choice rendering — the success text replaced the card's choices, and Continue must come back. Its body: store `_lastStep` in `ShowStep` and call `ShowStep(_lastStep)` minus the full re-render… concretely: `public void RenderChoicesForCurrentStep() { if (_lastStep is { } step) ShowStep(step); }` with `private FirstRunGuideStep? _lastStep;` set at the end of `ShowStep`.)

- [ ] **Step 3: `GuideSurface` — the UNIFIED choice list (r3 F5): every button on every card comes from `ChoicesFor`, registered AND visualized together**

```csharp
    /// <summary>The flow's window surface. ONE unified choice list per card: the step's buttons,
    /// then the chrome Close where it applies, then the theme chips + Continue on Theme —
    /// registered (dwell targets) AND visualized (the window's cards) together, so every target
    /// gets a pushed centre (r3 F5). Keys are LITERALS (r2 F12).</summary>
    private sealed class GuideSurface(
        FirstRunGuideWindow window, FirstRunGuideViewModel vm,
        SettingsUiCoordinator owner) : IGuideSurface
    {
        private FirstRunGuideStep? _lastStep;

        public void ShowStep(FirstRunGuideStep step)
        {
            var (title, body) = TextsFor(step);
            var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
            window.ShowCardContent(title, body, chromeClose);
            window.SetChoices(ChoicesFor(step, chromeClose));   // ONE list: buttons + close + chips
            window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
            window.SetDwellKnob(step == FirstRunGuideStep.DwellTime);
            window.SetThemeChipsVisible(step == FirstRunGuideStep.Theme);
            _lastStep = step;
        }

        private (string Label, string Id)[] ChoicesFor(FirstRunGuideStep step, bool chromeClose)
        {
            var choices = new List<(string Label, string Id)>();
            choices.AddRange(step switch
            {
                FirstRunGuideStep.Welcome => [
                    (T("guide.welcome.start"), "guide_start"),
                    (T("guide.later"), "guide_later")],
                FirstRunGuideStep.MeetDwell => [(T("guide.dwell.skip"), "guide_dwell_skip")],
                FirstRunGuideStep.DwellTime => [(T("guide.dwelltime.continue"), "guide_dwelltime_continue")],
                FirstRunGuideStep.Calibrate => [
                    (T("guide.calibrate.now"), "guide_calibrate_now"),
                    (T("guide.calibrate.skip"), "guide_calibrate_skip")],
                FirstRunGuideStep.ToolbarTask => [
                    (T("guide.toolbar.continue"), "guide_toolbar_continue"),
                    (T("guide.toolbar.skip"), "guide_toolbar_skip")],
                // The theme CHIPS + the Continue — all in the unified list, all with visuals (r3 F5):
                FirstRunGuideStep.Theme => [
                    (T("guide.theme.midnight"), "guide_theme_midnight"),
                    (T("guide.theme.ember"), "guide_theme_ember"),
                    (T("guide.theme.daylight"), "guide_theme_daylight"),
                    (T("guide.theme.contrast"), "guide_theme_contrast"),
                    (T("guide.theme.continue"), "guide_theme_continue")],
                FirstRunGuideStep.Safety => [(T("guide.safety.begin"), "guide_safety_begin")],
                FirstRunGuideStep.Done => [(T("guide.done.finish"), "guide_done_finish")],
                _ => [],
            });
            if (chromeClose)
                choices.Add((T("guide.chrome.close"), "guide_close"));
            return choices.ToArray();
        }

        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            window.ShowCardContent(T("guide.safety.title"), message, chromeClose: true);
            var list = new List<(string Label, string Id)>();
            foreach (var choice in choices)
                list.Add((choice.Label, choice.Id));
            list.Add((T("guide.chrome.close"), "guide_close"));   // chrome Close through step 7 (r3 F10)
            window.SetChoices(list.ToArray());
            owner.RememberSafetyActions(choices);                 // id → OnChosen, for the dispatch
        }

        public void HideCard() => window.HideGuide();
        public void ShowCard() => window.ShowGuide();
        public void Close() => window.Close();

        private static string T(string key) => StringTable.Current.T(key);
    }
```

with the coordinator-side support:

```csharp
    private readonly Dictionary<string, Action?> _safetyActions = new();

    /// <summary>The safety chain's id → OnChosen map, populated by the surface when a question
    /// card renders; the dispatch consults it before the guide's own ids (r3 F5's one route).</summary>
    internal void RememberSafetyActions(IReadOnlyList<ModalChoice> choices)
    {
        _safetyActions.Clear();
        foreach (var choice in choices)
            _safetyActions[choice.Id] = choice.OnChosen;
    }

    /// <summary>Theme chip display names — four new keys (surface "guide", fit modal170):
    /// guide.theme.midnight/ember/daylight/contrast. ADD to Task 4's sorted block and both DE
    /// files ("Mitternacht", "Glut", "Tageslicht", "Kontrast").</summary>
```

and `OnGuideChoice` — the ONE dispatch, safety choices first:

```csharp
    private void OnGuideChoice(string id)
    {
        if (_guideFlow is null) return;
        if (_safetyActions.TryGetValue(id, out var onChosen))
        {
            _safetyActions.Clear();                        // consumed: the chain advances to the next card
            _guideFlow.ForwardSafetyChoice(onChosen ?? (() => { }));
            return;
        }
        switch (id)
        {
            case "guide_start": _guideFlow.Advance(); break;
            case "guide_later": _guideFlow.Close(); break;
            case "guide_dwell_skip": _guideFlow.Skip(); break;
            case "guide_dwell_minus": _guideVm?.SetDwellTime(_guideVm.DwellTimeMs - 100); break;
            case "guide_dwell_plus": _guideVm?.SetDwellTime(_guideVm.DwellTimeMs + 100); break;
            case "guide_dwelltime_continue": _guideFlow.Advance(); break;
            case "guide_calibrate_now": StartCalibration(); break;
            case "guide_calibrate_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_continue": _guideFlow.Advance(); break;
            case "guide_theme_continue": _guideFlow.Advance(); break;
            case "guide_done_finish": _guideFlow.Finish(); break;
            case "guide_safety_begin": _guideFlow.BeginSafetyQuestions(); break;   // transition card
            case "guide_close": _guideFlow.Close(); break;
            default:
                if (id.StartsWith("guide_theme_", StringComparison.Ordinal))
                {
                    var theme = id["guide_theme_".Length..];
                    _guideVm?.ThemeChosenFire(
                        char.ToUpperInvariant(theme[0]) + theme[1..]);
                }
                break;
        }
    }
```

(The theme chip labels come from four NEW keys — `guide.theme.midnight/ember/daylight/contrast` — added to Task 4's sorted block with German values; the ids carry the canonical ThemeRegistry names lower-cased, upper-cased on dispatch. `window.SetDwellKnob` replaces `SetSlider` — Task 7's builder.)

- [ ] **Step 4: Calibration — per-"Calibrate-now" subscription + the refusal guard (r2 F10)** — rev 3's `StartCalibration` with r3 F12's fix: the Idle path does NOT re-render (no flicker — `NotifyCalibrationEnded` advances and renders step 5); only the refusal guard re-renders via `_guideSurface.ShowStep(FirstRunGuideStep.Calibrate)`:

```csharp
    private IDisposable? _calibrationCeiling;

    private void StartCalibration()
    {
        var context = Context!;
        if (_guideVm is null || _guideFlow is null || _guideSurface is null) return;

        var collected = false;
        void OnCalibrationStateChanged(CalibrationStateChangedMessage m)
        {
            if (m.State is CalibrationState.Collecting or CalibrationState.Training)
            {
                collected = true;
                _calibrationCeiling?.Dispose();
                _guideWindow?.HideGuide();
            }
            else if (m.State is CalibrationState.Idle or CalibrationState.Cancelled)
            {
                context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
                _calibrationCeiling?.Dispose();
                context.Dispatcher.BeginInvoke(() =>
                {
                    _guideWindow?.ShowGuide();
                    _guideFlow?.NotifyCalibrationEnded();   // renders step 5 — no Calibrate flicker (r3 F12)
                });
            }
        }
        context.Messenger.Register<SettingsUiCoordinator, CalibrationStateChangedMessage>(this,
            (_, m) => OnCalibrationStateChanged(m));
        _calibrationCeiling = SafetySetupFlow.DispatcherDelay(TimeSpan.FromSeconds(2), RefusalGuard);

        void RefusalGuard()
        {
            if (collected) return;
            context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
            context.Dispatcher.BeginInvoke(() => _guideSurface.ShowStep(FirstRunGuideStep.Calibrate));
        }

        context.Messenger.Send(new CalibrationRequestedMessage());
    }
```

(`Restore`/`UnwindPartialRaise` unregister unconditionally with the same one-type-arg overload.)

- [ ] **Step 5: App.xaml.cs wiring** — rev 3's Step 5 verbatim (the forwarder; `:704` canInterrupt; the banner hold AFTER :518 inside the notice-hosts region with the status-card block inside `!IsTestMode` + `StatusCardsFallback` through EventCoordinator; the notice hosts' optional args + subscriptions; the renamed raise).

- [ ] **Step 6: Retire the machinery + swap the row** — rev 3's Step 6 verbatim (the complete consumer list, `g_guide`, the new `guide.theme.*` label keys added to Task 4's block + DE).

- [ ] **Step 7: Wiring tests** — rev 3's Step 7 + the `StatusCardPresenterTests` pin (:735-736).

- [ ] **Step 8: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → PASS; `node .gitnexus/run.cjs detect-changes --scope all` → not partial.

```bash
git -C .worktrees/first-run-guide add -A
git -C .worktrees/first-run-guide commit -m "feat(guide): per-run raise, unified choice targets, gaze input, calibration guards, retire the safety-setup machinery"
```

---

### Task 13: Final gates

- [ ] **Step 1:** `dotnet build EyeCtrl.slnx` → 0 errors.
- [ ] **Step 2:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → 0 failed (under `wait-idle-tests.ps1`).
- [ ] **Step 3:** `dotnet test tests/EyeCtrl.ViewModels.Tests` → 0 failed; `dotnet test tests/EyeCtrl.Core.Tests` → 0 failed.
- [ ] **Step 4:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"` → 0 failed.
- [ ] **Step 5:** `python -I tools/strings_validate.py` → clean; `python -I tools/kb_log_metrics_test.py` → clean.
- [ ] **Step 6:** `node .gitnexus/run.cjs detect-changes --scope all` → not partial; report the risk line.

---

## Self-review notes (rev 4 — the plan-review r3 fixes folded in)

1. **Self-contained again (r3 F1)** — every task's code is inline; no "rev 2 verbatim" references; `a21b7caf` is not needed.
2. **One unified choice list per card (r3 F5)** — `ChoicesFor(step, chromeClose)` returns the step's buttons + chrome Close (where it applies) + the theme chips AND Theme's Continue; `SetChoices` registers AND visualizes together; the extra `ClearChoices()` after `SetChoices` is deleted; `ShowSafetyCard` appends `guide_close` (r3 F10) — chrome Close exists through step 7 per spec §6.
3. **Compile-level** — `ShutdownStarted += (_, _) => OnWindowExitFinished();` (EventHandler, r3 F2); `OnChromeChoice` defined on the VM (r3 F4); no `DwellSlider` control (−/+ chrome cards + value read-out, r3 F4); `using System.Windows.Shapes;`; `OnToolbarVisibility` field-subscribed (r3 F6); `_guideSurface` field declared (r3 F6); `EdgeWord` → instance accessor + literal-key switch (r3 F6); `Unregister` one-type-arg (r2 F10); the App block below `bannerSlot` inside `!IsTestMode` with the `StatusCardsFallback` forwarder (r3 F13).
4. **Visuals (r3 F7)** — cards get `BrushSurfaceMedium` background, `BrushAccent` border, `BrushTextPrimary` label, and a per-card progress rectangle bound to `ChoiceProgress`; the practice ring is stroked with a fill element bound to `PracticeProgress`; `OnTargetProgress` now dispatches.
5. **Ring lifecycle (r3 F8)** — `Show()` hides the ring (inside `Show`, not at body level); `ToolbarTaskEntered` SHOWS the ring before the raise chain; task exit hides it; close restores it (`_modal` guards).
6. **Unwind (r3 F9)** — `ForceShow()` + ring restore added; `_guideFlow` nulled and the handler unsubscribed BEFORE `window.Close()` (no re-entry); `StatusCardsFallback` cleared in Restore.
7. **Calibration** — the refusal guard re-renders via `ShowStep(Calibrate)` (live targets, r2 F10); the Idle path does NOT re-render (no flicker, r3 F12); unregister is the one-type-arg overload, unconditional in Restore/unwind.
8. **Keys** — four new `guide.theme.*` label keys (sorted into Task 4's block + DE); every key literal-referenced; edge words via a literal switch over `DockedEdge`.
9. **Theme preview persistence** — `Card.Resources` keeps the previewed theme for later cards too; accepted and documented here (r3 F12).
10. **Census** — `DwellSurfaceCensusTests` at the App.Tests ROOT (r3 F11); the commit adds that exact path; classification text specified as the chrome-detector row.
11. **Spec coverage** — §4 (Tasks 4, 12), §5 (Tasks 1, 8, 9, 10, 11, 12), §6 (Tasks 5, 6, 12), §7 steps 1–8 incl. the transition card, theme preview, dwell knob (Tasks 4–7, 12), §8 (Tasks 6, 12), §9 (Tasks 8–12), §12 (per-task tests + Task 13). No known gaps.
