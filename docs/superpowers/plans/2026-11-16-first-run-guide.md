# First-Run Introduction Guide — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Rev 3** — external plan-review r2 processed (verdict FIX-FIRST: 13×S1, 5×S2; all verified against source and folded in). Rev 2's headline corrections: **every step button is a registered dwell target** (rev 2 stranded the user on Welcome); the safety block shows a transition card and `StartQuestions()` runs from its Continue (`BeginSafetyQuestions()`), so the first question is no longer wiped; the window pushes real centres itself (`PointToScreen`) after building each card — no deferred "view code"; choice visuals are code-built (no `ItemsControl` tuple binding); `RaiseToTop` also lands on `IPauseIndicatorOverlay`; German rows sorted + identical values allowlisted; `SettingsService`/`ThemeResourceApplier` mechanisms pinned; the raise-failure unwind is logged, not thrown.

**Goal:** Build the first-run introductory guide exactly as specified in `docs/superpowers/specs/2026-11-16-first-run-guide-design.md` (rev 2.4, SHIP): an eight-step gaze-driven first-run experience that absorbs the safety setup and holds seven `GuideOpen` claims.

**Architecture:** A headless `FirstRunGuideFlow` state machine (the `SafetySetupFlow` pattern) drives a per-run `FirstRunGuideWindow` through `IGuideSurface`. `SettingsUiCoordinator` builds the per-run trio behind the renamed `FirstRunGuideRequestedMessage`, acquires the guide's claims at the actual `Show()` inside the latched `SplashThreadHandle.WhenExited` continuation, wires gaze input, and releases/restores on close. The five safety questions run through the existing `SafetySetupFlow` via `StartQuestions()`, begun from the transition card's Continue.

**Tech Stack:** .NET 10 WPF (net10.0-windows), CommunityToolkit.Mvvm, NFluent 3.1.0 + xUnit v3, Serilog.

## Global Constraints

- **Worktree:** every command runs inside `.worktrees/first-run-guide`. Test commands use ONE project per invocation. Before committing code: `node .gitnexus/run.cjs detect-changes --scope all`.
- **Assertions:** NFluent 3.1.0, xUnit v3, private nested fakes only. The repo has **no `[Category]` attribute** — UI-gated tests use `[Trait("Category", "UI")]`; everything else runs in the headless subset.
- **No visible windows in App tests:** STA tests that show the guide window call `InvisibleTestWindow.Prepare(window)` first; never write/animate `Opacity` after `Show`.
- **Style:** file-scoped namespaces, Allman, `_camelCase`, nullable enabled, no `#region`.
- **Strings:** `guide.*` keys set `where:"guide"`; `StringTableSchema.Surfaces` and the validator's `SURFACES` stay in exact parity; **`strings.json` keys must be sorted** (`strings_validate.py:33`); **every key must appear as a quoted literal under `src/`** (`validate_unused_keys`, :629-655 — so all keys are referenced by literal `T("...")` calls in Task 12, never interpolated); German rows are `{"text", "src"}` only (`src` gets hash-rewritten by `--refresh-de-src`); a German value identical to English must be added to `localization/strings.identical-allowlist.json`; `--refresh-de-src` after any EN text change.
- **Privacy:** guide log lines carry step ids only. Two lines: `First-run guide: shown` / `First-run guide: closed (step=<id>, completed=<bool>)`.
- **Spec §10 ledger:** settled — plan defects that would need a design change are escalated, not improvised.
- **Dwell geometry (verified):** choice targets register AT THE ORIGIN + `Hold(id)`; centres pushed afterwards (`UpdateTargetCenter` + `latch.Positioned`, `SettingsWindowViewModel.cs:186-215`); radius 70 DIP (`SettingsWindow.ModalBtnRadius`, `SettingsWindow.xaml.cs:178` — an App-internal; the guide VM declares its own 70 DIP const); margin 35 DIP (`LatchReleaseMarginDip`); chrome dwell = `ToolbarButtonDwellMs`; the practice target is the one `ActivationButtonDwellMs` consumer. The detector caps per-gap credit (~150 ms, `DwellDetector.cs:403-415`) — tests feed real 30 ms samples.

---

### Task 1: `GuideOpen` seams — suppressor member, modal flag + event wiring, offers, coordinator exposure

*(Unchanged from rev 2 — r2 confirmed this task's fixes. Restated for the executor's completeness.)*

**Files:** Modify `src/EyeCtrl.Core/FixationSuppressor.cs`, `src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs`, `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`; Test `tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs` (new).

**Interfaces:** Produces `FixationSuppressor.Guide`; `ModalInteractionState.GuideOpen` + `GuideOpenChanged` (volatile flag, per-subscriber try/catch raise like `ProbeOpenChanged`); `ActivationOfferCoordinator` subscribes `GuideOpenChanged += OnGuideOpenChanged` (:113, unsubscribe beside :367, `private void OnGuideOpenChanged(bool _) => RecomputeSuspension();`) and adds `if (_modal.GuideOpen) reasons.Add("guide");` after the ProbeOpen check; `EventCoordinator.IsGuideOpen` + `GuideOpenChanged` forwarders.

- [ ] **Step 1:** Test (new file, the two tests from rev 2: flip-once + throwing-subscriber) → **Step 2:** run, FAIL → **Step 3:** implement exactly the rev 2 code blocks → **Step 4:** `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` then `--filter "FullyQualifiedName~ActivationOffer"` → PASS → **Step 5:**

```bash
git -C .worktrees/first-run-guide commit -m "feat(guide): GuideOpen modal flag + FixationSuppressor.Guide + offers subscription and reason"
```

---

### Task 2: `SafetySetupFlow.StartQuestions()` + flight-kind guard

*(Unchanged from rev 2 — r2 confirmed, including the `EnsuringInFlightSupervision` helper and the `Pick`/`Top`-style tests.)*

**Files:** Modify `src/EyeCtrl.App/Services/SupervisionToggleService.cs`, `src/EyeCtrl.App/Services/SafetySetupFlow.cs`; Test extend `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs`.

**Interfaces:** Produces `SafetySetupFlow.StartQuestions()` (`_active = true; _pendingFailure = null; Advance(0);`) + `SupervisionToggleService.IsEnablingInFlight => _inFlight && _kind == RequestKind.Enabling`; `RequestCrashRecovery` gains `if (_supervision.Enabled && !_supervision.InFlight) return;` and attaches only when `IsEnablingInFlight`. Tests: `StartQuestions_AskesQuestion0_WithoutTheIntroCard`, `StartQuestions_Completes_AndSetsTheFlag`, `CrashRecovery_Yes_WhileAlreadyEnabled_IsANoOpSuccess`, `CrashRecovery_Yes_WithAnUnrelatedFlightInFlight_DoesNotWait` (uses `EnsuringInFlightSupervision`: supervision ON + `isSupervisorConnected: () => false` + `runInBackground: _background.Add`), `StartQuestions_CanBeAbandoned_ByWindowClose` — all through the harness's `Pick`/`Top`.

- [ ] Steps: tests → FAIL → implement → PASS (`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SafetySetupFlowTests"`) → commit `feat(guide): SafetySetupFlow.StartQuestions + supervision flight-kind guard`.

---

### Task 3: `SplashThreadHandle.WhenExited` — latched, shutdown-subscribed continuation

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs` (new, STA, **no Category trait — headless CI runs these**)

**Interfaces:**
- Produces: `SplashThreadHandle.WhenExited(Action)` — latched; fires on the REGISTERING thread's dispatcher; the constructor subscribes `_splashDispatcher.ShutdownStarted += OnWindowExitFinished` so a splash thread that faults after registration still releases the continuation (r2 F17); `HasShutdownStarted` is also checked at registration.

- [ ] **Step 1: Write the failing tests** — the shutdown test needs a REAL second dispatcher on its own thread (r2 F1: `BeginInvokeShutdown` only queues, so `HasShutdownStarted` is false immediately; and `BeginInvoke`'d continuations must be pumped):

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Threading;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

public class SplashThreadHandleWhenExitedTests
{
    private sealed class FakeSplashWindow : ISplashWindow
    {
        public event Action? ExitFinished;
        public void OnSplashShown() { }
        public void BeginExit() { }
        public void ShowNoTrackerSentence() { }
        public void ForceClose() { }
        public void FatalClose() { }
        public bool TryReadDpiScale(out double scaleX, out double scaleY) { scaleX = scaleY = 0; return false; }
        public void RaiseExit() => ExitFinished?.Invoke();
    }

    [StaFact]
    public void WhenExited_Attaches_BeforeExit_AndFiresOnce_OnTheRegisteringDispatcher()
    {
        var window = new FakeSplashWindow();
        var handle = new SplashThreadHandle(Dispatcher.CurrentDispatcher, window);
        var fired = new List<string>();

        handle.WhenExited(() => fired.Add("a"));          // attached: waits
        Check.That(fired).IsEmpty();

        window.RaiseExit();                                // fires on THIS thread (no splash thread here)
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain

        Check.That(fired).HasSize(1);
        handle.WhenExited(() => fired.Add("late"));        // already exited: fires at once
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain
        Check.That(fired).HasSize(2);
        Check.That(fired[0]).IsEqualTo("a");
    }

    [StaFact]
    public void WhenExited_AfterTheSplashDispatcherShutDown_FiresImmediately()
    {
        // A REAL second dispatcher, shut down on its own thread and joined — then the
        // registration on THIS thread sees HasShutdownStarted (r2 F1's pump requirement).
        var window = new FakeSplashWindow();
        Dispatcher? splashDispatcher = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            splashDispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Check.That(ready.Wait(5000)).IsTrue();
            var handle = new SplashThreadHandle(splashDispatcher!, window);

            splashDispatcher!.Invoke(() => splashDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
            thread.Join(5000);

            var fired = new List<string>();
            handle.WhenExited(() => fired.Add("x"));       // shutdown: fires on THIS thread at once
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain
            Check.That(fired).HasSize(1);
        }
        finally
        {
            if (splashDispatcher is { HasShutdownStarted: false } d)
                d.Invoke(() => d.BeginInvokeShutdown(DispatcherPriority.Normal));
            thread.Join(1000);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandleWhenExitedTests"`
Expected: FAIL — no `WhenExited`.

- [ ] **Step 3: Implement** (in `SplashThreadHandle`; constructor gains the wiring)

```csharp
    // The exit latch: ExitFinished fires on the SPLASH thread; continuations run on the
    // dispatcher of the thread that REGISTERED (the main thread in production). Two fallback
    // latches cover a splash thread that never raises ExitFinished: its dispatcher's
    // ShutdownStarted event (subscribed in the ctor — a faulted thread still shuts down via
    // Dispose, r2 F17) and the HasShutdownStarted check at registration time (r1 F20).
    private readonly object _exitGate = new();
    private readonly List<(Dispatcher Dispatcher, Action Action)> _exitContinuations = new();
    private bool _exited;

    /// <summary>
    /// Runs <paramref name="onMainThread"/> once, on the registering thread's dispatcher, after
    /// the splash's <see cref="ISplashWindow.ExitFinished"/> — or immediately when the exit
    /// already happened, the splash dispatcher shut down, or the handle is disposed.
    /// </summary>
    internal void WhenExited(Action onMainThread)
    {
        ArgumentNullException.ThrowIfNull(onMainThread);
        var dispatcher = Dispatcher.CurrentDispatcher;     // the registering thread (main, in prod)
        bool fireNow;
        lock (_exitGate)
        {
            fireNow = _exited || _disposed || _splashDispatcher.HasShutdownStarted;
            if (!fireNow) _exitContinuations.Add((dispatcher, onMainThread));
        }
        if (fireNow) _ = dispatcher.BeginInvoke(onMainThread);
    }

    private void OnWindowExitFinished()
    {
        List<(Dispatcher, Action)> toRun;
        lock (_exitGate)
        {
            if (_exited) return;
            _exited = true;
            toRun = new List<(Dispatcher, Action)>(_exitContinuations);
            _exitContinuations.Clear();
        }
        foreach (var (dispatcher, action) in toRun)
            _ = dispatcher.BeginInvoke(action);
    }
```

and the constructor:

```csharp
    internal SplashThreadHandle(Dispatcher splashDispatcher, ISplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
        window.ExitFinished += OnWindowExitFinished;              // fires on the splash thread
        splashDispatcher.ShutdownStarted += OnWindowExitFinished; // the faulted-thread fallback (r2 F17)
    }
```

- [ ] **Step 4: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandle"` → PASS.

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): SplashThreadHandle.WhenExited — latched, shutdown-subscribed continuation"
```

---

### Task 4: The `guide` string surface — schema, validator, sorted keys, German

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs` (Surfaces)
- Modify: `tools/strings_validate.py` (SURFACES)
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (guide.* keys, **sorted**)
- Modify: `src/EyeCtrl.App/Localization/strings.de.json` (+ `strings.de.manifest.json`)
- Modify: `localization/strings.identical-allowlist.json`
- Test: run the parity gates (existing)

**Interfaces:**
- Produces: the `guide.*` keys below. **`strings.json` keys must be SORTED** (`strings_validate.py:33`); **every key must appear as a quoted literal under `src/`** (`validate_unused_keys` :629-655) — Task 12's GuideSurface references them all literally; German rows `{"text", "src"}` only; a German value identical to English goes into `localization/strings.identical-allowlist.json`.

- [ ] **Step 1: Add the surface to both lists (same position — after `"eye-care",`)**

- [ ] **Step 2: Add the English keys, SORTED** (fit: titles `modal460`, choice buttons `modal170`, prose `prose`):

```json
  "guide.calibrate.body": { "text": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.calibrate.now": { "text": "Calibrate now", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.title": { "text": "Calibration", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.chrome.close": { "text": "Close guide", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.done.body": { "text": "The docked edge summons the toolbar — your main menu. The gaze keyboard opens from it, and every setting lives in Settings.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.done.finish": { "text": "Finish", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.done.title": { "text": "You're set.", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwell.body": { "text": "Rest your gaze on a target and hold still: the ring fills, and on completion the target activates. Try it on the circle below.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwell.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwell.title": { "text": "Dwell — your click", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwelltime.body": { "text": "This slider sets how long a desktop click takes — the cursor dwell that clicks. Try values on the practice target; the guide's own buttons keep the standard UI dwell.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwelltime.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwelltime.title": { "text": "Dwell time", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.later": { "text": "Later", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.safety.begin": { "text": "Begin", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.safety.body": { "text": "Five quick questions keep crash recovery, startup and the audible cue set up for you.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.safety.title": { "text": "5 quick questions to keep you safe", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.theme.body": { "text": "The theme takes effect after a restart — the card here previews your choice.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.theme.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.title": { "text": "Pick a look", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.toolbar.body": { "text": "Look at the {0} edge of the screen to summon the toolbar — the keyboard, settings and everything else open from there. Try it now.", "who": "user", "where": "guide", "fit": "prose", "args": ["0"] },
  "guide.toolbar.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.bottom": { "text": "bottom", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.left": { "text": "left", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.right": { "text": "right", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.top": { "text": "top", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.success": { "text": "Summoned! The toolbar hides again on its own. Continue when ready.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.toolbar.title": { "text": "Your main menu", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.welcome.body": { "text": "EyeCtrl lets you control this PC with your eyes. This short guide shows you the basics — about 2 minutes. Every practice task can be skipped.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.welcome.start": { "text": "Start", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.welcome.title": { "text": "Welcome to EyeCtrl", "who": "user", "where": "guide", "fit": "modal460", "args": [] }
```

- [ ] **Step 3: Add the German rows** (`{"text", "src"}` only; `src` = the exact English text — `--refresh-de-src` hash-rewrites it):

```json
  "guide.calibrate.body": { "text": "Stelle sicher, dass der Cursor deinen Augen folgt. Du kannst später auch über Einstellungen oder die Symbolleiste kalibrieren.", "src": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar." },
  "guide.welcome.start": { "text": "Start", "src": "Start" },
  "guide.welcome.title": { "text": "Willkommen bei EyeCtrl", "src": "Welcome to EyeCtrl" }
```

(translate every key; `"guide.welcome.start"`'s identical value goes into `localization/strings.identical-allowlist.json` — r2 F2.)

- [ ] **Step 4: Manifest + refresh** — `"guide"` between `"eye-care"` and `"language-recovery"` in `strings.de.manifest.json`, then `python -I tools/strings_validate.py --refresh-de-src`.

- [ ] **Step 5: Parity gates** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~StringsEnumParity|FullyQualifiedName~SingleSurfaceKey"` and `python -I tools/strings_validate.py` → clean.

- [ ] **Step 6: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.Core/Localization/StringTableSchema.cs src/EyeCtrl.Core/Localization/strings.json tools/strings_validate.py src/EyeCtrl.App/Localization/ localization/strings.identical-allowlist.json
git -C .worktrees/first-run-guide commit -m "feat(guide): guide string surface — sorted EN keys, DE rows, identical allowlist"
```

---

### Task 5: `FirstRunGuideFlow` + `FirstRunGuideHost` — the headless state machine

**Files:**
- Create: `src/EyeCtrl.App/Services/FirstRunGuideFlow.cs` + `FirstRunGuideHost.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` (new)

**Interfaces:**
- Produces: `FirstRunGuideStep`; `GuideExitReason { Finished, Abandoned }`; `IGuideSurface` (`ShowStep`, `ShowSafetyCard`, `HideCard`, `ShowCard`, `Close`); `FirstRunGuideFlow` — factory `Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow>`, the flow routes `showModal` to the surface itself; **`BeginSafetyQuestions()`** (r2 F8: `Enter(Safety)` creates the chain but does NOT start it — the transition card's Continue calls this); `Start/Advance/Skip/Finish/Close/NotifyTaskSucceeded/NotifyCalibrationEnded/NotifySafetyEnded/ForwardSafetyChoice/WindowClosed/IsOnStep/LastStepId` + events; `FirstRunGuideHost.TryBegin/End`.

- [ ] **Step 1: Write the failing tests** — same file as rev 2 with TWO deltas: the all-steps test inserts the transition card (`flow.BeginSafetyQuestions()` before the first choice — asserting `s.SafetyMessages` is EMPTY until then), and the safety-routing test asserts the transition card arrives first:

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
        Check.That(s.SafetyMessages).IsEmpty();            // transition card, no chain yet (r2 F8)

        flow.BeginSafetyQuestions();                        // the transition card's Continue
        Check.That(s.SafetyMessages).HasSize(1);            // question 0 arrived on the surface
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
        Check.That(s.SafetyMessages.Count).IsEqualTo(2);    // question 1 on the surface
        Check.That(s.LastSafetyChoices!.Single(c => c.Id == "safety_no")).IsNotNull();
    }
```

(The other tests — omissions, skip policy, abandon, host — carry over from rev 2 verbatim; the safety test in rev 2's form is replaced by the two above.)

- [ ] **Step 2: Run → FAIL. Step 3: Implement** — `FirstRunGuideFlow.cs` exactly as rev 2 EXCEPT the safety block in `Enter` and the new method:

```csharp
        if (step == FirstRunGuideStep.Safety)
        {
            // The chain is created here but NOT started: the transition card's Continue calls
            // BeginSafetyQuestions() — starting before ShowStep would register question 0's
            // targets and ShowStep would then wipe them (r2 F8).
            _safetyFlow = _createSafetyFlow(
                (message, choices) => _surface.ShowSafetyCard(message, choices),
                completed => NotifySafetyEnded(completed));
        }

        _surface.ShowStep(step);
    }

    /// <summary>The transition card's Continue: starts the absorbed question chain (spec §7 step 7).
    /// No-op unless the guide is on the Safety step.</summary>
    public void BeginSafetyQuestions()
    {
        if (_step != FirstRunGuideStep.Safety) return;
        _safetyFlow?.StartQuestions();
    }
```

(`FirstRunGuideHost.cs` unchanged from rev 2.)

- [ ] **Step 4: Run → PASS. Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/FirstRunGuideFlow.cs src/EyeCtrl.App/Services/FirstRunGuideHost.cs tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): headless flow + host — transition-gated safety block, Finish exit"
```

---

### Task 6: `FirstRunGuideViewModel` — binding surface, gaze contract, latch protocol

**Files:**
- Create: `src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs`
- Modify: `tests/EyeCtrl.App.Tests/Services/DwellSurfaceCensusTests.cs` (census row — **the census lives in App.Tests**, r2 F18) + `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` (expected map)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs` (new)

**Interfaces:**
- Produces: the rev 2 VM surface, with r2's corrections: **`private const double ChoiceRadiusDip = 70;`** declared on the VM (`SettingsWindow.ModalBtnRadius` is an App-internal ViewModels cannot see — r2 F3), radius computed PER CALL in `RegisterChoice` (a `static readonly` would freeze the DPI — r2 F18), `using EyeCtrl.Core.Services;` for `DpiHelper`, **the base `Receive` override passes `_latch.Filter`**, and the VM's `IsVisible` defaults true-friendly (the wiring sets it true at Show; the TEST helper sets it too).

- [ ] **Step 1: The gate entries** — `OverlayTelemetryWiringTests`'s `expected` map gains `[nameof(FirstRunGuideViewModel)] = DwellContext.Other`; `DwellSurfaceCensusTests` gains the guide's census row (App.Tests file — run `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSurfaceCensus"`).

- [ ] **Step 2: Write the failing tests** (the `Vm()` helper now sets `IsVisible = true` — r2 F4; the latch test dwells at `ToolbarButtonDwellMs = 300` with real 30 ms samples — r2 F4's gap-credit cap):

```csharp
    private static FirstRunGuideViewModel Vm() =>
        new(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
```

```csharp
    [Fact]
    public void Latch_HoldsNewChoices_UntilGazeLeaves_AndCentrePushArms()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 300;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        var fired = new List<string>();
        vm.RegisterChoice("c_yes", () => fired.Add("yes"));       // at origin, held (Settings pattern)
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();

        vm.UpdateChoiceCentre("c_yes", new PhysicalPoint(400, 300));
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();         // gaze never left it yet

        long t = 0;
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(900, 900), t)); t += 30_000;  // elsewhere
        // Onto the card: 300 ms of chrome dwell at 30 ms samples (11 samples, real cadence —
        // the detector caps per-gap credit, so one huge jump would never complete, r2 F4).
        for (var i = 0; i < 11; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(400, 300), t));
            t += 30_000;
        }
        Check.That(fired).HasSize(1);                             // armed, dwelled, action ran
    }
```

(The other tests carry over from rev 2 verbatim — `DwellActiveWhilePaused_IsTrue`, `PracticeTarget_UsesActivationDwell…`, `Gaze_GatesOnIsInGap_NotOnIsPaused`, `SetDwellTime_ReTimesThePracticeTarget`.)

- [ ] **Step 3: Run → FAIL. Step 4: Implement** — the rev 2 VM body with these deltas:

```csharp
using EyeCtrl.Core.Services;   // DpiHelper — added (r2 F3)

    /// <summary>The choice cards' radius, in DIP (Settings' ModalBtnRadius is an App-internal
    /// ViewModels cannot see — the guide declares its own, r2 F3). Computed PER REGISTRATION:
    /// a static would freeze the DPI (r2 F18).</summary>
    private const double ChoiceRadiusDip = 70;

    // in RegisterChoice — replace ChoiceRadiusPhysical with:
        DwellDetector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0),
            DpiHelper.LogicalToPhysicalX(ChoiceRadiusDip), _settings.Dwell.ToolbarButtonDwellMs));
```

(everything else — `ShowCard`, `ShowSafetyCard`/`SafetyChoices`, `RegisterChoice(id, onActivated)` at origin + `Hold`, `UpdateChoiceCentre` → `UpdateTargetCenter` + `Positioned`, `ClearChoices`, `SetPracticeCentre`/`ReTimePracticeTarget`/`SetDwellTime`, `Set*Visible`, `Receive` with `_latch.Filter`, `OnTargetActivated` clear-before-act, the practice `DwellActivationMessage` — is the rev 2 code verbatim, minus the `static readonly ChoiceRadiusPhysical` field.)

- [ ] **Step 5: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests|FullyQualifiedName~DwellSurfaceCensus"` → PASS; `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring"` → PASS.

- [ ] **Step 6: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs tests/EyeCtrl.App.Tests/Services/DwellSurfaceCensusTests.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideViewModel — latch protocol, dual dwell knobs, telemetry + census rows"
```

---

### Task 7: `FirstRunGuideWindow` — render-only window with REAL content building and centre pushes

**Files:**
- Create: `src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml` + `.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row, `exempt:`)
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`RaiseToTop()` default method on `IToolbarOverlay`, `ICursorCircleOverlay`, **and `IPauseIndicatorOverlay`** — r2 F11: Task 12 raises the pause strip too)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` (new, STA, `[Trait("Category", "UI")]`)

**Interfaces:**
- Produces: `FirstRunGuideWindow : OverlayWindow` — fullscreen (`Left/Top/Width/Height` read live), `IsClickThrough="True"` in XAML only, no `Close()` override; **the window builds the step visuals in code and pushes real centres itself** (r2 F9 — the centre push is no longer deferred prose): `SetChoices` builds Border cards into a horizontal `StackPanel`, then on the dispatcher pushes each card's centre via `PointToScreen` into `vm.UpdateChoiceCentre`; `SetPractice` builds the practice `Ellipse` and pushes its centre into `vm.SetPracticeCentre`; `SetSlider` hosts a `DwellSlider`; `SetThemeChips` builds chip cards; `HideGuide()`/`ShowGuide()` via Show/Hide.

- [ ] **Step 1: XAML** — the choice row is a plain `StackPanel` (r2 F5: `ItemsControl` has no `Orientation`; tuples do not bind without a template — code-built cards avoid both):

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

- [ ] **Step 2: Code-behind** — the centre-push is REAL code now (r2 F9):

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window (spec §5): fullscreen click-through backdrop with a centered
/// interactive card. RENDER-ONLY for text; the window BUILDS the step visuals (choice cards,
/// practice ellipse, slider, theme chips) and pushes every dwell target's real centre into the
/// VM after layout — PointToScreen already yields device (physical) pixels (r2 F9: without this
/// every target after the first stays held at the origin and never arms).
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;
    private readonly List<(string Id, FrameworkElement Card)> _choiceCards = new();

    public FirstRunGuideWindow(FirstRunGuideViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowCard(title, body, chromeClose);

    /// <summary>Builds one visual card per (label, id) pair and registers each as a chrome
    /// dwell target through the VM (Task 6's protocol); then pushes the laid-out centres.</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id)> choices)
    {
        _vm.ClearChoices();
        _choiceCards.Clear();
        ChoiceRow.Children.Clear();
        foreach (var (label, id) in choices)
        {
            var card = new Border
            {
                Child = new TextBlock { Text = label, Margin = new Thickness(12, 8, 12, 8) },
                Margin = new Thickness(8, 0, 0, 0),
            };
            _choiceCards.Add((id, card));
            ChoiceRow.Children.Add(card);
            _vm.RegisterChoice(id, () => _vm.OnChromeChoice(id));
        }
        PushCentres();
    }

    /// <summary>The practice target's visual + centre (r2 F9 — an absolute coordinate would
    /// park it off the card).</summary>
    public void SetPractice(bool visible)
    {
        _vm.SetPracticeVisible(visible);
        StepContent.Content = visible ? BuildPracticeEllipse() : null;
        PushCentres();
    }

    private FrameworkElement BuildPracticeEllipse()
    {
        var ellipse = new Ellipse
        {
            Width = 120,
            Height = 120,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ellipse.Loaded += (_, _) => PushCentres();
        return ellipse;
    }

    public void SetSlider(bool visible)
    {
        _vm.SetSliderVisible(visible);
        // The DwellSlider the Settings rows use, bound to the VM's knob:
        StepContent.Content = visible
            ? new DwellSlider
              {
                  MinValue = 200,
                  MaxValue = 2000,
              }
            : null;
        PushCentres();
    }

    public void SetThemeChips(bool visible)
    {
        _vm.SetThemeChipsVisible(visible);
        PushCentres();
    }

    /// <summary>Pushes every registered target's REAL centre (device px via PointToScreen) into
    /// the VM. Runs after a layout pass; the dispatcher round-trip lets a fresh card's layout
    /// complete first. Card is fixed-size, so SizeChanged never fires — an explicit push per
    /// build is the mechanism (r2 F9).</summary>
    private void PushCentres()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            foreach (var (id, card) in _choiceCards)
            {
                if (!card.IsVisible) continue;
                var centre = card.PointToScreen(
                    new Point(card.ActualWidth / 2, card.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
            if (StepContent.Content is Ellipse practice && _vm.PracticeVisible)
            {
                var centre = practice.PointToScreen(
                    new Point(practice.ActualWidth / 2, practice.ActualHeight / 2));
                _vm.SetPracticeCentre(new PhysicalPoint(centre.X, centre.Y));
            }
        });
    }

    public void HideGuide() => Hide();
    public void ShowGuide() => Show();
}
```

(The `DwellSlider` property names — `MinValue`/`MaxValue` — and the `Ellipse` are pinned against the control's actual API during implementation; the executor greps `DwellSlider`'s public surface once and matches, and the choice-card Visual carries the `guide_close`/theme-chip ids the wiring registers. `vm.OnChromeChoice(id)` is Task 6's routing name for "a registered chrome target fired" — the VM's `OnTargetActivated` invokes it for every non-guide_close id, and `RegisterChoice`'s stored action handles the rest.)

- [ ] **Step 3: Overlay inventory row** (`exempt:`) — as rev 2.

- [ ] **Step 4: `IOverlayWindows.cs`** — the default method on **three** interfaces (`IToolbarOverlay`, `ICursorCircleOverlay`, **`IPauseIndicatorOverlay`** — r2 F11):

```csharp
    /// <summary>Raises this window above any later-created fullscreen topmost overlay (the
    /// guide). Default no-op so the test fakes need no member; the real OverlayWindow's
    /// implementation (:377) overrides it.</summary>
    void RaiseToTop() { }
```

- [ ] **Step 5: STA test** (usings complete; `[Trait("Category", "UI")]` — r2 F1/F5):

```csharp
// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

[Trait("Category", "UI")]
public class FirstRunGuideWindowTests
{
    [StaFact]
    public void Window_IsClickThrough_AllowsTransparency_Fullscreen_AndBindsTheVm()
    {
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            Check.That(window.IsClickThrough).IsTrue();
            Check.That(window.AllowsTransparency).IsTrue();
            Check.That(window.DataContext).IsSameReferenceAs(vm);
            Check.That(window.Width).IsGreaterThan(SystemParameters.PrimaryScreenWidth - 1);
            Check.That(window.Height).IsGreaterThan(SystemParameters.PrimaryScreenHeight - 1);
        }
        finally
        {
            window.Close();
        }
    }
}
```

- [ ] **Step 6: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideWindowTests|FullyQualifiedName~OverlayInventoryDrift"` → PASS; `--filter "FullyQualifiedName~Toolbar"` → PASS (the default members keep the fakes compiling).

- [ ] **Step 7: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml* docs/design/overlay-unification-spec.md tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs src/EyeCtrl.App/Services/IOverlayWindows.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): window builds step visuals and pushes real centres; RaiseToTop on three interfaces"
```

---

### Task 8: `ToolbarAutoHideController` — guide-hidden hold + `ResetToHidden`

*(Unchanged from rev 2 — r2 confirmed the ordered hold, the real-harness tests, and `ResetToHidden`.)*

**Files:** Modify `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`; Test extend `ToolbarAutoHideControllerTests.cs`.

**Interfaces:** `SetGuideHiddenHold(bool)` (suppress `ForceShow` + `ReassertVisibility`'s `SetActive(true)`, force Hidden) + `ResetToHidden()` (mirror `ResetToVisible()`); tests use `NewController`/`Attach`/`FakeVisibility` (`ForceShowCalls`, `SetActiveCalls`) with the file's sample-drive loops.

- [ ] Steps: tests → FAIL → implement (the rev 2 code blocks: `_guideHiddenHold` field, `SetGuideHiddenHold`, `ResetToHidden`, the two suppression points) → PASS → commit `feat(guide): ToolbarAutoHideController guide-hidden hold + ResetToHidden`.

---

### Task 9: Toolbar wiring — ordered hold, feed terms, `OnTargetActivated` gate

*(Unchanged from rev 2 — r2 confirmed. The gate sits AFTER the progress/highlight reset lines; note the gate does not stop the dwell cue (the base publishes `DwellActivationMessage` after `OnTargetActivated` returns) — the cue on a gated press is accepted (r2 F19), the settings write it was protecting against is not.)*

**Files:** Modify `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`, `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`; Test extend `ToolbarLifecycleCoordinatorTests.cs` (incl. the `:361-376` source pins).

**Interfaces:** `SetGuideHold` (hidden hold FIRST, then `SetAutoHideSuspended("guide", …)`), `ResetToolbarForGuideTask()`, `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` + `guideOpen` (all call sites + source pins), `ToolbarViewModel.GuideOpenGate` (assigned in Task 12), `ToolbarVisibilityChanged` REUSED (:52/:366).

- [ ] Steps: tests → FAIL → implement → PASS → commit `feat(guide): ordered toolbar hold + GuideOpen feed terms + OnTargetActivated gate`.

---

### Task 10: Ring lifecycle — the coordinators' gate + restore

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs` (+ `RingMayReshow`)
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs` (both calibration re-show sites gated)
- Test: extend the coordinators' existing test files

**Interfaces:** `OverlayVmBindingCoordinator.RingMayReshow => !_modal.GuideOpen` (the message coordinator has NO `_modal` — its ctor takes `(toolbar, vmBinding)`). The FULL ring lifecycle lives in Task 12's wiring: hide on guide open, re-show for step 5 only, hide again on task exit, restore on close — the coordinators only refuse their own re-shows while GuideOpen (r2 F15's completeness is Task 12's).

- [ ] **Step 1: Failing test** (the flag — driveable headlessly; the calibration-path test drives through the HARNESS's messenger, not a nonexistent method — r2 F15):

```csharp
    [Fact]
    public void RingMayReshow_IsFalse_WhileGuideOpen()
    {
        modal.GuideOpen = true;
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.GuideOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();
    }
```

- [ ] **Step 2: Implement** — the rev 2 code (the flag + the `IsVisible` case gate + the two calibration sites consulting `_vmBinding.RingMayReshow`).

- [ ] **Step 3: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayVmBinding|FullyQualifiedName~OverlayMessage"` → PASS.

```bash
git -C .worktrees/first-run-guide commit -m "feat(guide): ring re-show gated on GuideOpen via the binding coordinator's flag"
```

---

### Task 11: Proactive surfaces — BannerSlot hold, hosts, SetupNoticeWatch

*(Unchanged from rev 2 — r2 confirmed the optional params, the Preempt exemption, and the gated watch.)*

**Files:** Modify `BannerSlot.cs`, `RestartNoticeHost.cs`, `LagMarkHost.cs`, `SetupNoticeWatch.cs`; Test `BannerSlotHoldTests.cs` (new) + host extensions.

**Interfaces:** `Hold()/Release()` (front re-queue; `Preempt` deliberately bypasses — caregiver alerts take the slot mid-guide, pinned by test); optional `Func<bool>? isGuideOpen = null` on both hosts + `OnGuideOpenChanged` (defer-newest replay); `SetupNoticeWatch`: `CheckStartupWhenGuideClosed()` helper at the three call sites + expiry skip while GuideOpen + close-time poke.

- [ ] Steps: tests → FAIL → implement (the rev 2 code blocks) → PASS → commit `feat(guide): BannerSlot hold (preempt exempted) + defer hosts + gated SetupNoticeWatch`.

---

### Task 12: Startup wiring — per-run raise, claims, gaze input, real visuals, retirement

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (rename), `SafetySetupFlow.cs` (message + retire `Launch`/`Host`), `SettingsUiCoordinator.cs`, `EventCoordinator.cs` (forwarders), `App.xaml.cs`, `SettingsWindow.xaml.cs`, `SettingsPageContext.cs`, `GeneralPageSource.cs`
- Modify tests: `SafetySetupFlowTests.cs` (:78-170 + `RequestProbe` :656), `TestSettingsPageContext.cs:46`, `SimpleModeCurationTests.cs:103/:108`, `localization/settings-row-english.golden.json:1177`, `SafetySetupWiringTests`, `GeneralPageSourceTests`, `StatusCardPresenterTests` (:735-736 pin)

**Interfaces:** Consumes Tasks 1–11. Produces the complete raise: renamed message; per-run trio; claims at `Show()` inside `WhenExited`; gaze input wired through chrome-target actions; the full `GuideSurface` step mapping with LITERAL keys; calibration per-handler + refusal guard re-running `ShowStep(Calibrate)`; restore unconditional.

- [ ] **Step 1: Rename the message** — `FirstRunGuideRequestedMessage` (rev 2's block).

- [ ] **Step 2: The per-run factory** — rev 2's `StartFirstRunGuide`/`BuildAndRaiseGuide` with r2's corrections:

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
            // lands in DispatcherUnhandledException during a first-run startup. The unwind
            // mirrors Restore: claims down, subscriptions off, lease released, cards started.
            Log.Error(ex, "First-run guide: raise failed; unwinding");
            UnwindPartialRaise(context, lease);
        }
    }

    /// <summary>The raise-failure unwind: whatever BuildAndRaiseGuide already did is undone in
    /// reverse — the same paths Restore uses (r2 F14: partial state — VM visible/subscribed,
    /// GuideOpenGate set, OnToolbarVisibility subscribed — must not survive).</summary>
    private void UnwindPartialRaise(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        if (_guideFlow is not null)
            _toolbar.ToolbarVisibilityChanged -= OnToolbarVisibility;
        if (_guideVm is not null)
        {
            _guideVm.IsVisible = false;
            (_guideVm as IDisposable)?.Dispose();
        }
        if (_guideWindow is not null) { _guideWindow.Close(); _guideWindow = null; }
        context.ToolbarVM.GuideOpenGate = null;
        context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);
        _modal.GuideOpen = false;
        _quickActions.SetSummonSuspended("guide", false);
        _toolbar.SetGuideHold(false);
        _guideFlow = null;
        _guideVm = null;
        _guideHost = null;
        lease.Dispose();
        StatusCardsFallback?.Invoke();
        StatusCardsFallback = null;
    }
```

with `BuildAndRaiseGuide` = rev 2's body PLUS these r2 corrections:

```csharp
        // (a) The safety chain's ctor is (settings, supervision, showModal, applyAndSave,
        //     setSimpleSettingsUi, onEnded) — rev 2 put onEnded in applyAndSave's slot (r2 F6):
        _guideFlow = new FirstRunGuideFlow(settings, trackerPresent: tracker,
            toolbarTaskAvailable: toolbarTask,
            surface: _guideSurface = new GuideSurface(_guideWindow, _guideVm, this),
            createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, _supervisionToggle,
                showModal,
                applyAndSave: () =>
                {
                    OnSettingsChanged(settings);                     // the live-apply path (r2 F6/Q2)
                    context.SettingsService.SaveDebounced(settings); // the DEBOUNCED save
                },
                setSimpleSettingsUi: v => settings.General.SimpleSettingsUi = v,
                onEnded));

        // (b) The summon handler keeps rev 2's step gate + unsubscribe (r1 F21) and the ring
        //     lifecycle is COMPLETED here (r2 F15): hide on open, re-show on the task, hide on
        //     task exit, restore on close.
        _guideFlow.ToolbarTaskExited += () => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen) return;
            context.OverlayManager.CursorCircle.Hide();   // step 5 ended: ring hidden again (spec §5)
        });

        // (c) On Show, the ring hides (the guide owns the screen; spec §5):
        //   inside Show(), after AcquireFixationSuppression:
        context.OverlayManager.CursorCircle.Hide();

        // (d) Show() is wrapped (r2 F14): an exception between claim acquisition and flow start
        //     must not hold claims for the session:
        void Show()
        {
            try
            {
                context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Guide);
                _modal.GuideOpen = true;
                _quickActions.SetSummonSuspended("guide", true);
                _toolbar.SetGuideHold(true);
                context.OverlayManager.CursorCircle.Hide();
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
```

(`OnToolbarVisibility`/`Restore` keep rev 2's bodies — `Restore` additionally clears `StatusCardsFallback = null` so a later failed re-run cannot double-start the presenter (r2 F19). The ring's step-5 raise and the success text stay rev 2's. `context.IsSettingsOpen`/`context.IsProbeOpen` do NOT exist — `Restore`'s ring-restore guard reads `_modal.SettingsOpen`/`_modal.ProbeOpen` (r2 F19).)

- [ ] **Step 3: `GuideSurface` — every step button is a registered dwell target (r2 F7)** — literal keys (r2 F12), the transition card (r2 F8), and the safety choices rendered + registered:

```csharp
    /// <summary>The flow's window surface. EVERY button on a card is a registered chrome target
    /// (r2 F7 — rev 2 only registered guide_close and the chips, stranding Welcome). Keys are
    /// LITERALS (r2 F12 — interpolated keys are invisible to the unused-key scan and two step
    /// names do not match their key segments).</summary>
    private sealed class GuideSurface(
        FirstRunGuideWindow window, FirstRunGuideViewModel vm,
        SettingsUiCoordinator owner) : IGuideSurface
    {
        public void ShowStep(FirstRunGuideStep step)
        {
            var (title, body) = TextsFor(step);
            var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done or FirstRunGuideStep.Safety);
            window.ShowCardContent(title, body, chromeClose);
            window.SetChoices(ChoicesFor(step));
            window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
            window.SetSlider(step == FirstRunGuideStep.DwellTime);
            window.SetThemeChips(step == FirstRunGuideStep.Theme);
            vm.ClearChoices();
            if (chromeClose)
                vm.RegisterChoice("guide_close", () => vm.CloseRequestedFire());
            if (step == FirstRunGuideStep.Theme)
                foreach (var theme in new[] { "Midnight", "Ember", "Daylight", "Contrast" })
                    vm.RegisterChoice($"guide_theme_{theme.ToLowerInvariant()}",
                        () => { vm.ThemeChosenFire(theme); ReRegisterFor(step); });
        }

        private (string Title, string Body) TextsFor(FirstRunGuideStep step) => step switch
        {
            FirstRunGuideStep.Welcome => (T("guide.welcome.title"), T("guide.welcome.body")),
            FirstRunGuideStep.MeetDwell => (T("guide.dwell.title"), T("guide.dwell.body")),
            FirstRunGuideStep.DwellTime => (T("guide.dwelltime.title"), T("guide.dwelltime.body")),
            FirstRunGuideStep.Calibrate => (T("guide.calibrate.title"), T("guide.calibrate.body")),
            FirstRunGuideStep.ToolbarTask => (T("guide.toolbar.title"),
                string.Format(T("guide.toolbar.body"), EdgeWord())),
            FirstRunGuideStep.Theme => (T("guide.theme.title"), T("guide.theme.body")),
            FirstRunGuideStep.Safety => (T("guide.safety.title"), T("guide.safety.body")),
            FirstRunGuideStep.Done => (T("guide.done.title"), T("guide.done.body")),
            _ => (string.Empty, string.Empty),
        };

        private static string EdgeWord() => StringTable.Current.T(
            $"guide.toolbar.edge.{owner_Context().Settings.Toolbar.DockedEdge.ToString().ToLowerInvariant()}");

        private static (string Label, string Id)[] ChoicesFor(FirstRunGuideStep step) => step switch
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
            FirstRunGuideStep.Safety => [(T("guide.safety.begin"), "guide_safety_begin")],  // transition card
            FirstRunGuideStep.Done => [(T("guide.done.finish"), "guide_done_finish")],
            _ => [],
        };

        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            window.ShowCardContent(T("guide.safety.title"), message, chromeClose: true);
            window.SetChoices(choices.Select(c => (c.Label, c.Id)).ToArray());
            foreach (var choice in choices)
                vm.RegisterChoice(choice.Id, () => owner.ForwardSafety(choice));
        }

        public void HideCard() => window.HideGuide();
        public void ShowCard() => window.ShowGuide();
        public void Close() => window.Close();

        private void ReRegisterFor(FirstRunGuideStep step)
        {
            // A theme pick re-renders the chips' brushes; the targets re-register so the laid-out
            // centres stay live (the theme preview applies card-local brushes — see below).
            ShowStep(step);
        }

        private static string T(string key) => StringTable.Current.T(key);
    }
```

(`EdgeWord()` reads the docked edge through the coordinator — implemented as an instance accessor `private EventCoordinationContext OwnerContext() => Context!;` on `SettingsUiCoordinator`, static-delegated; adjust to the file's actual shape. The theme chips' ids are never user-visible.)

The coordinator's `OnGuideChoice` keeps rev 2's switch and gains:

```csharp
            case "guide_safety_begin": _guideFlow.BeginSafetyQuestions(); break;   // transition card (r2 F8)
```

- [ ] **Step 4: Theme preview + dwell-time live apply (r2 F16)** — in the wiring:

```csharp
        // Theme preview: card-LOCAL brush application — DynamicResource on the card resolves the
        // swapped resources; Tokens.Current and every other surface stay startup-themed (r1 F2).
        _guideVm.ThemeChosen += name => context.Dispatcher.BeginInvoke(() =>
        {
            var theme = ThemeRegistry.Resolve(name);
            ThemeResourceApplier.Apply(_guideWindow.CardResources, theme);   // Card.Resources (see Task 7 note)
            settings.Theme.Name = name;
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
        });

        // Dwell time: the slider live-applies too (r2 F16), not just saves:
        _guideVm.DwellTimeChanged += _ =>
        {
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
        };
```

(Task 7's window exposes `public ResourceDictionary CardResources => Card.Resources;` — the one accessor the preview needs. The DwellSlider visual binds `DwellTimeMs`/`SetDwellTime` per Task 7's builder, with the control's actual bindable API matched during implementation.)

- [ ] **Step 5: Calibration — per-"Calibrate-now" subscription + the refusal guard re-renders the card (r2 F10)**

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
                context.Messenger.Unregister<CalibrationStateChangedMessage>(this);   // ONE type arg (r2 F10)
                _calibrationCeiling?.Dispose();
                context.Dispatcher.BeginInvoke(() =>
                {
                    _guideWindow?.ShowGuide();
                    _guideSurface.ShowStep(FirstRunGuideStep.Calibrate);   // re-render: live targets (r2 F10)
                    _guideFlow?.NotifyCalibrationEnded();
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

(The refusal re-show runs the FULL `ShowStep(Calibrate)` — the choice targets were cleared when "Calibrate now" fired, so a bare `ShowGuide()` would strand. `Restore` still unregisters unconditionally with the same ONE-type-arg overload.)

- [ ] **Step 6: App.xaml.cs wiring (r2 F13's placement fixes)**

  1. `_eventCoordinator.SetSplashHandle(_splashHandle);` after Initialize, with the EventCoordinator forwarder (`public void SetSplashHandle(SplashThreadHandle? handle) => _settingsUi?.SetSplashHandle(handle);`).
  2. `:704`: `canInterrupt: im.CurrentState == InteractionState.FreeLook && !_eventCoordinator.IsGuideOpen`.
  3. The banner hold + status-card block moves BELOW :518 (where `bannerSlot` is declared — r2 F13's CS0841), placed after the notice hosts' construction; the whole status-card part stays inside `if (!IsTestMode)`:
  ```csharp
            // The guide's banner hold (claim 4) — after bannerSlot's declaration (:518):
            _eventCoordinator.GuideOpenChanged += open =>
            {
                if (open) bannerSlot.Hold(); else bannerSlot.Release();
            };

            if (!IsTestMode)
            {
                if (SafetySetupStartup.ShouldPrompt(
                        settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode))
                {
                    // The status cards start on guide close; the coordinator's raise-failure
                    // unwind invokes the fallback (r3 F6 / r2 F14).
                    void OnGuideClosedForStatusCards(bool open)
                    {
                        if (open) return;
                        _eventCoordinator.GuideOpenChanged -= OnGuideClosedForStatusCards;
                        _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
                    }
                    _eventCoordinator.GuideOpenChanged += OnGuideClosedForStatusCards;
                    _eventCoordinator.StatusCardsFallback = () => OnGuideClosedForStatusCards(false);
                }
                else
                {
                    _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
                }
            }
  ```
  with the EventCoordinator forwarder:
  ```csharp
      /// <summary>The guide raise's failure fallback: starts the status-card presenter when the
      /// coordinator's unwind runs (r2 F13 — App has no coordinator field of its own).</summary>
      internal Action? StatusCardsFallback
      {
          get => _settingsUi?.StatusCardsFallback;
          set { if (_settingsUi is not null) _settingsUi.StatusCardsFallback = value; }
      }
  ```
  (`StatusCardsFallback` is also cleared in `Restore` — r2 F19's double-start.)
  4. the notice hosts' optional args + `GuideOpenChanged` subscriptions at :519/:605/:577 (rev 2's list).
  5. `SafetySetupStartup.MaybeRequest` (:666) — renamed message.

- [ ] **Step 7: Retire the machinery + swap the row** — rev 2's Step 6 verbatim (the complete consumer list).

- [ ] **Step 8: Wiring tests** — rev 2's Step 7 + the `StatusCardPresenterTests` pin (:735-736) updated for the two branches.

- [ ] **Step 9: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → PASS; `node .gitnexus/run.cjs detect-changes --scope all` → not partial.

```bash
git -C .worktrees/first-run-guide add -A
git -C .worktrees/first-run-guide commit -m "feat(guide): per-run raise, claims, gaze input with real visuals, calibration guards, retire the safety-setup machinery"
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

## Self-review notes (rev 3 — the plan-review r2 fixes folded in)

1. **Every step button is a dwell target** — `GuideSurface.ShowStep` registers ALL of `ChoicesFor(step)` via `vm.RegisterChoice(id, () => owner.OnGuideChoice(id))` (r2 F7); Welcome's Start/Later can fire; the chrome Close renders wherever `chromeClose` holds.
2. **Safety block order** — `Enter(Safety)` creates the chain but does NOT start it; the transition card's Continue calls `BeginSafetyQuestions()` (r2 F8); `ShowSafetyCard` renders the choice cards AND registers their targets (the wiring, via `owner.ForwardSafety`).
3. **Centres are real code** — the window builds cards and pushes `PointToScreen` centres after a layout round-trip (r2 F9); `SizeChanged` never fires on a fixed-size card, so the push is explicit per build.
4. **Compile-level** — `[Trait("Category","UI")]` (no `[Category]`); `StackPanel` choice row (no `ItemsControl.Orientation`, no tuple binding); `SettingsWindow.ModalBtnRadius` replaced by the VM's own `ChoiceRadiusDip` const + `using EyeCtrl.Core.Services`; `Vm()` sets `IsVisible`; the latch test dwells at a 300 ms chrome knob with real 30 ms samples (gap-credit cap); the safety ctor's `(showModal, applyAndSave, setSimpleSettingsUi, onEnded)` arg order restored (r2 F6); `Unregister<CalibrationStateChangedMessage>(this)` one-type-arg overload (r2 F10); `IPauseIndicatorOverlay.RaiseToTop()` default member (r2 F11); literal keys via `TextsFor`/`ChoicesFor` switches (r2 F12); the App status-card block sits below `bannerSlot`'s declaration inside `!IsTestMode` with the `StatusCardsFallback` forwarder through EventCoordinator (r2 F13).
5. **Failure unwind** — raise failures log + run `UnwindPartialRaise` (the Restore-shaped teardown); `Show()` carries its own try/catch (r2 F14).
6. **Ring lifecycle complete** — hide on open, step-5 raise, hide on task exit, restore on close (r2 F15); Task 10's test drives only the flag.
7. **Theme preview** — `ThemeResourceApplier.Apply(Card.Resources, …)` (card-local; DynamicResource resolves; `Tokens.Current` untouched) + dwell-time live-apply (r2 F16).
8. **WhenExited** — ctor subscribes `ShutdownStarted` (faulted threads release), tests use a REAL second dispatcher and pump (r2 F1/F17).
9. **Census placement** — `DwellSurfaceCensusTests` lives in App.Tests; the radius is computed per registration (r2 F18).
10. **Smalls** — no `ArmLatchForCards`; `_modal.SettingsOpen`/`_modal.ProbeOpen` for the ring-restore guard; German `src` hash-rewritten by refresh; Task 3's tests carry no UI trait; `StatusCardsFallback` cleared in Restore; the gated toolbar press does not stop the cue (accepted, documented).
11. **Spec coverage** — §4 (Tasks 4, 12), §5 (Tasks 1, 8, 9, 10, 11, 12), §6 (Tasks 5, 6, 12), §7 steps 1–8 incl. the transition card and the theme preview (Tasks 4, 5, 6, 7, 12), §8 (Tasks 6, 12), §9 (Tasks 8–12), §12 (per-task tests + Task 13). No known gaps.
