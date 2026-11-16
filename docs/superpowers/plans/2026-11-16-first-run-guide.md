# First-Run Introduction Guide — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Rev 7** — external plan-review r6 processed (verdict FIX-FIRST: 2×S1, 2×S2; all folded in). Rev 6's corrections: the census entry is a **tuple** (`DetectorSites` is `(string Path, string Classification, string Reason)[]`); **`SetSuspended`'s state write is gated too** (`if (!_guideHiddenHold) _state = State.Visible;` — the hold keeps the controller Hidden whatever SetSuspended says, r6 F2); **the card grows to 560 × 620** (the fixed rows left ~58 DIP for StepContent on DwellTime — the practice circle and the theme grid were clipped, r6 F3) and the title wraps with a 186 DIP right margin (it stops at the Close card); **chips and the chrome Close join `_choiceCards`/`_progressBars`** with their bars restored from their own fields on a re-render, and `SetThemeGrid` re-attaches `_themeGrid` when `SetPractice(false)` emptied `StepContent` (r6 F4). Rev 4's corrections: **every task's code is inline again** (the file is the single source — no rev-2/rev-3 references, r4 F1); **`SetChoices`/`SetDwellKnob`/`SetPractice` each own ONE slot** — the knob builds into its own `KnobRow` instead of overwriting `StepContent`, so the MeetDwell circle survives and the DwellTime practice target sits beside the knob (r4 F4); **in-place presses re-render** (`RenderChoicesForCurrentStep()` after the dwell ± and theme chips — `OnTargetActivated` clears every chrome target on any press, and ±/chips don't change the step, r4 F3); **the window has no `Close()` member** (r4 F2); the summon handler no longer re-renders (the toolbar event clears no targets, r4 F5); the theme labels moved into Task 4's sorted block; `IsStrictlyGreaterThan`; `Card.FindResource` so the preview reaches the code-built cards; census row in the census table's own shape; the dwell read-out's handler is subscribed once.

**Goal:** Build the first-run introductory guide exactly as specified in `docs/superpowers/specs/2026-11-16-first-run-guide-design.md` (rev 2.4, SHIP): an eight-step gaze-driven first-run experience that absorbs the safety setup and holds seven `GuideOpen` claims.

**Architecture:** A headless `FirstRunGuideFlow` state machine drives a per-run `FirstRunGuideWindow` through `IGuideSurface`. `SettingsUiCoordinator` builds the per-run trio behind the renamed `FirstRunGuideRequestedMessage`, acquires the seven claims at the actual `Show()` inside the latched `SplashThreadHandle.WhenExited` continuation, wires gaze input through ONE choice-id dispatch, and releases/restores on close. The safety questions run through the existing `SafetySetupFlow` via `StartQuestions()`, begun from the transition card's Continue.

**Tech Stack:** .NET 10 WPF (net10.0-windows), CommunityToolkit.Mvvm, NFluent 3.1.0 + xUnit v3, Serilog.

## Global Constraints

- **Worktree:** every command runs inside `.worktrees/first-run-guide`. Test commands use ONE project per invocation. Before committing code: `node .gitnexus/run.cjs detect-changes --scope all`.
- **Assertions:** NFluent 3.1.0, xUnit v3, private nested fakes only. No `[Category]` attribute — UI-gated tests use `[Trait("Category", "UI")]`. Strict comparisons: `IsStrictlyGreaterThan`.
- **No visible windows in App tests:** STA tests that show the guide window call `InvisibleTestWindow.Prepare(window)`; never write/animate `Opacity` after `Show`.
- **Style:** file-scoped namespaces, Allman, `_camelCase`, nullable enabled.
- **Strings:** `guide.*` keys set `where:"guide"`; schema/validator `SURFACES` exact parity; **`strings.json` keys sorted**; **every key referenced as a quoted literal under `src/`**; German rows `{"text", "src"}` only, **Sie form**; identical values into `localization/strings.identical-allowlist.json`; `--refresh-de-src` after EN changes.
- **Privacy:** guide log lines carry step ids only. Two lines: `First-run guide: shown` / `First-run guide: closed (step=<id>, completed=<bool>)`.
- **Spec §10 ledger:** settled — escalate, don't improvise.
- **Dwell geometry (verified):** chrome targets register AT THE ORIGIN + `Hold(id)`; centres pushed afterwards (`UpdateTargetCenter` + `latch.Positioned`, `SettingsWindowViewModel.cs:186-215`); radius 70 DIP (the VM's own const — `SettingsWindow.ModalBtnRadius` is an App-internal); margin 35 DIP (`LatchReleaseMarginDip`); chrome dwell = `ToolbarButtonDwellMs`; the practice target is the one `ActivationButtonDwellMs` consumer; the detector caps per-gap credit (~150 ms, `DwellDetector.cs:403-415`) — tests feed real 30 ms samples.

---

### Task 1: `GuideOpen` seams — suppressor member, modal flag + event wiring, offers, coordinator exposure

**Files:**
- Modify: `src/EyeCtrl.Core/FixationSuppressor.cs`, `src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs`, `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs` (new)

**Interfaces:** Produces `FixationSuppressor.Guide`; `ModalInteractionState.GuideOpen` + `GuideOpenChanged` (volatile flag, per-subscriber try/catch raise); `ActivationOfferCoordinator` recomputes on the flip; `EventCoordinator.IsGuideOpen` + `GuideOpenChanged` forwarders.

- [x] **Step 1: Write the failing tests**

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

- [x] **Step 2: Run to verify failure** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` → FAIL.

- [x] **Step 3: Implement** — `FixationSuppressor.cs` gains:

```csharp
    /// <summary>The first-run guide, while its fullscreen card is up.</summary>
    Guide,
```

`ModalInteractionState.cs` — after the `ProbeOpen` region (same volatile/write-on-UI/read-on-gaze contract, same per-subscriber try/catch raise as `ProbeOpenChanged`):

```csharp
    // volatile: read on the gaze thread (ring predicates, toolbar feed checks), written on the
    // UI thread by the guide's raise/close path.
    private volatile bool _guideOpen;

    /// <summary>
    /// True while the first-run guide owns the screen. The guide's card is interactive but the
    /// window is click-through outside it and every proactive surface defers behind it, so every
    /// consumer gates on it like <see cref="SettingsOpen"/>. Assigning raises
    /// <see cref="GuideOpenChanged"/>.
    /// </summary>
    public bool GuideOpen
    {
        get => _guideOpen;
        set
        {
            if (_guideOpen == value) return;
            _guideOpen = value;
            RaiseGuideOpenChanged(value);
        }
    }

    /// <summary>Raised after <see cref="GuideOpen"/> flips, on the UI thread. Per-subscriber
    /// try/catch like <see cref="ProbeOpenChanged"/>; subscribers read <see cref="GuideOpen"/>,
    /// not the argument (re-entrancy).</summary>
    public event Action<bool>? GuideOpenChanged;

    private void RaiseGuideOpenChanged(bool open)
    {
        var subscribers = GuideOpenChanged;
        if (subscribers is null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)subscriber)(open);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A GuideOpenChanged subscriber threw; the remaining subscribers still run");
            }
        }
    }
```

`ActivationOfferCoordinator.cs` — beside the :113 subscriptions add `_modal.GuideOpenChanged += OnGuideOpenChanged;`; beside the :367 unsubscriptions add the matching `-=`; add `private void OnGuideOpenChanged(bool _) => RecomputeSuspension();`; and `RecomputeSuspension` gains after the ProbeOpen check:

```csharp
        if (_modal.GuideOpen)
            reasons.Add("guide");
```

`EventCoordinator.cs` — beside the `IsSettingsOpen`/`SettingsOpenChanged` forwarders:

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

- [x] **Step 4: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` → PASS; `--filter "FullyQualifiedName~ActivationOffer"` → PASS.

- [x] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add src/EyeCtrl.Core/FixationSuppressor.cs src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): GuideOpen modal flag + FixationSuppressor.Guide + offers subscription and reason"
```

---

### Task 2: `SafetySetupFlow.StartQuestions()` + flight-kind guard

**Files:** Modify `src/EyeCtrl.App/Services/SupervisionToggleService.cs`, `src/EyeCtrl.App/Services/SafetySetupFlow.cs`; Test extend `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs`.

**Interfaces:** Produces `StartQuestions()` (`_active = true; _pendingFailure = null; Advance(0);`) + `IsEnablingInFlight => _inFlight && _kind == RequestKind.Enabling`; `RequestCrashRecovery` gains `if (_supervision.Enabled && !_supervision.InFlight) return;` and attaches only when `IsEnablingInFlight`.

- [x] **Step 1: Write the failing tests** (harness facts: `_cards` is `List<(string Message, IReadOnlyList<ModalChoice> Choices)>`; `Top`/`Pick(id)` → `.OnChosen!.Invoke()`; helpers `WorkingSupervision` (sync), `WorkingAsyncSupervision` (queues to `_background`, `isSupervisorConnected: () => true`), `FailingAsyncSupervision`):

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

- [x] **Step 2: Run → FAIL. Step 3: Implement** — `IsEnablingInFlight` beside `InFlight` (:88):

```csharp
    /// <summary>Whether the in-flight operation (if any) is an ENABLE. The crash-recovery chain
    /// may attach only to its own enable — an Ensuring or Disabling flight is not its request
    /// (spec §9, r1 F9/r3 F5).</summary>
    public bool IsEnablingInFlight => _inFlight && _kind == RequestKind.Enabling;
```

`SafetySetupFlow.cs` — `RequestCrashRecovery()` becomes:

```csharp
    private void RequestCrashRecovery()
    {
        if (_supervision is null) return;

        // r1 F9 / r3 F5: an unrelated flight (the startup ensure, a disable) must never be
        // mistaken for this answer's enable. Already-on is a no-op success; only an in-flight
        // ENABLE is awaited. (RequestEnable itself no-ops on _inFlight || Enabled.)
        if (_supervision.Enabled && !_supervision.InFlight) return;
        _supervision.RequestEnable();
        if (_supervision.IsEnablingInFlight)
        {
            _waitingForSupervision = true;
            _supervision.StateChanged += OnSupervisionStateChanged;
            _ceiling = _schedule(CrashRecoveryCeiling, OnCrashRecoveryCeiling);
            return;
        }

        HoldFailureIfRolledBack();
    }
```

and after `Start()`:

```csharp
    /// <summary>Runs the question chain WITHOUT the intro card — the first-run guide shows its own
    /// transition card and then drives the questions (spec §7 step 7). Same initialization as
    /// <see cref="Start"/> minus the intro modal: the chain begins at question 0 and ends with the
    /// same onEnded semantics. Accepted residual (r1 S3): a "Yes" while a Disabling flight is in
    /// flight does not attach and leaves supervision off — the Settings row remains the remedy.</summary>
    internal void StartQuestions()
    {
        _active = true;
        _pendingFailure = null;
        Advance(0);
    }
```

- [x] **Step 4: Run → PASS** (`--filter "FullyQualifiedName~SafetySetupFlowTests"`).
- [x] **Step 5: Commit** `feat(guide): SafetySetupFlow.StartQuestions + supervision flight-kind guard`.

---

### Task 3: `SplashThreadHandle.WhenExited` — latched, shutdown-subscribed continuation

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs` (new, STA, no UI trait)

**Interfaces:** Produces `WhenExited(Action)` — latched; fires on the REGISTERING thread's dispatcher; the ctor subscribes `_splashDispatcher.ShutdownStarted` with the `EventHandler` signature (r3 F2) so a faulted splash thread still releases; `HasShutdownStarted` also checked at registration (r1 F20).

- [x] **Step 1: Write the failing tests**

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
        // registration on THIS thread sees HasShutdownStarted. Pump before asserting (r2 F1).
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

- [x] **Step 2: Run → FAIL. Step 3: Implement** — in `SplashThreadHandle`, the latch fields + methods:

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
        splashDispatcher.ShutdownStarted += (_, _) => OnWindowExitFinished();  // EventHandler (r3 F2)
    }
```

- [x] **Step 4: Run → PASS** (`--filter "FullyQualifiedName~SplashThreadHandle"`).
- [x] **Step 5: Commit** `feat(guide): SplashThreadHandle.WhenExited — latched, shutdown-subscribed continuation`.

---

### Task 4: The `guide` string surface — schema, validator, sorted keys, German

**Files:**
- Modify: `StringTableSchema.cs`, `tools/strings_validate.py`, `strings.json`, `src/EyeCtrl.App/Localization/strings.de.json` (+ manifest), `localization/strings.identical-allowlist.json`

**Interfaces:** Produces the 36 `guide.*` keys below (incl. the four theme labels — r3 F12's placement fix). `strings.json` keys SORTED; every key referenced as a literal in Task 12's switches; German rows `{"text", "src"}`, Sie form; identical values allowlisted.

- [x] **Step 1:** `guide` into both `Surfaces`/`SURFACES` lists (after `"eye-care",`).

- [x] **Step 2: English keys, SORTED** (fit: titles `modal460`, buttons `modal170`, prose `prose`):

```json
  "guide.calibrate.body": { "text": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.calibrate.now": { "text": "Calibrate now", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.title": { "text": "Calibration", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.chrome.close": { "text": "Close guide", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.done.body": { "text": "The docked edge summons the toolbar — your main menu. The gaze keyboard opens from it, and every setting lives in Settings.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.done.finish": { "text": "Finish", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.done.title": { "text": "You're set.", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwell.body": { "text": "Rest your gaze on the circle and hold still: the ring fills, and on completion the target activates. Try it now.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwell.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwell.title": { "text": "Dwell — your click", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwelltime.body": { "text": "The − and + cards set how long a desktop click takes — the cursor dwell that clicks. Try values on the circle; the guide's own buttons keep the standard UI dwell.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwelltime.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwelltime.title": { "text": "Dwell time", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.later": { "text": "Later", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.safety.begin": { "text": "Begin", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.safety.body": { "text": "Five quick questions keep crash recovery, startup and the audible cue set up for you.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.safety.title": { "text": "5 quick questions to keep you safe", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.theme.body": { "text": "Pick a look — it takes effect after a restart; the card here previews your choice.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.theme.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.contrast": { "text": "Contrast", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.daylight": { "text": "Daylight", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.ember": { "text": "Ember", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.midnight": { "text": "Midnight", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
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

- [x] **Step 3: German rows, Sie form** (r3 F3; translate every key — `src` = the exact English text):

```json
  "guide.calibrate.body": { "text": "Stellen Sie sicher, dass der Cursor Ihren Augen folgt. Sie können später auch über Einstellungen oder die Symbolleiste kalibrieren.", "src": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar." },
  "guide.dwelltime.body": { "text": "Die Karten − und + legen fest, wie lange ein Desktop-Klick dauert — der Cursor-Dwell, der klickt. Probieren Sie Werte am Kreis aus; die eigenen Schaltflächen der Anleitung behalten das übliche UI-Dwell.", "src": "The − and + cards set how long a desktop click takes — the cursor dwell that clicks. Try values on the circle; the guide's own buttons keep the standard UI dwell." },
  "guide.theme.contrast": { "text": "Kontrast", "src": "Contrast" },
  "guide.theme.daylight": { "text": "Tageslicht", "src": "Daylight" },
  "guide.theme.ember": { "text": "Glut", "src": "Ember" },
  "guide.theme.midnight": { "text": "Mitternacht", "src": "Midnight" },
  "guide.welcome.start": { "text": "Start", "src": "Start" },
  "guide.welcome.title": { "text": "Willkommen bei EyeCtrl", "src": "Welcome to EyeCtrl" }
```

(`"guide.welcome.start"`'s identical value goes into `localization/strings.identical-allowlist.json`; the four theme labels match the existing `settings.general.theme.option.*` translations.)

- [x] **Step 4: Manifest + refresh** — `"guide"` between `"eye-care"` and `"language-recovery"` in `strings.de.manifest.json`; `python -I tools/strings_validate.py --refresh-de-src`.
- [x] **Step 5: Parity gates** → clean.
- [x] **Step 6: Commit** `feat(guide): guide string surface — 36 sorted keys, Sie-form DE rows, theme labels, identical allowlist`.

---

### Task 5: `FirstRunGuideFlow` + `FirstRunGuideHost` — the headless state machine

**Files:**
- Create: `src/EyeCtrl.App/Services/FirstRunGuideFlow.cs` + `FirstRunGuideHost.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` (new)

**Interfaces:** Produces `FirstRunGuideStep`; `GuideExitReason { Finished, Abandoned }`; `IGuideSurface` (`ShowStep(FirstRunGuideStep)`, `ShowSafetyCard(string, IReadOnlyList<ModalChoice>)`, `HideCard()`, `ShowCard()`, `Close()`); `FirstRunGuideFlow` — factory `Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow>` with the flow routing `showModal` to the surface; `BeginSafetyQuestions()`; `Start/Advance/Skip/Finish/Close/NotifyTaskSucceeded/NotifyCalibrationEnded/NotifySafetyEnded/ForwardSafetyChoice/WindowClosed/IsOnStep/LastStepId` + `StepEntered/ToolbarTaskEntered/ToolbarTaskExited/Closed`; `FirstRunGuideHost.TryBegin/End` + lease.

- [x] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

public class FirstRunGuideFlowTests
{
    private sealed class FakeSurface : IGuideSurface
    {
        public List<FirstRunGuideStep> Shown { get; } = new();
        public List<string> SafetyMessages { get; } = new();
        public IReadOnlyList<ModalChoice>? LastSafetyChoices;
        public int HideCount; public int ShowCount; public int CloseCount;
        public void ShowStep(FirstRunGuideStep step) => Shown.Add(step);
        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            SafetyMessages.Add(message);
            LastSafetyChoices = choices;
        }
        public void HideCard() => HideCount++;
        public void ShowCard() => ShowCount++;
        public void Close() => CloseCount++;
    }

    private static FirstRunGuideFlow Flow(
        FakeSurface surface, EyeCtrlSettings settings, bool tracker = true, bool dockedToolbar = true)
        => new(settings, trackerPresent: tracker, toolbarTaskAvailable: dockedToolbar, surface,
               createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, supervision: null,
                   showModal, applyAndSave: () => { }, setSimpleSettingsUi: _ => { }, onEnded: onEnded));

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
        flow.Advance();                                   // Start button
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.MeetDwell);
        flow.NotifyTaskSucceeded();                       // practice dwell
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // Continue
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Calibrate);
        flow.NotifyCalibrationEnded();                    // Idle/Cancelled
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
        flow.NotifyTaskSucceeded();                       // summon
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Theme);
        flow.Advance();                                   // Continue
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Safety);
        Check.That(s.SafetyMessages).IsEmpty();            // transition card, no chain yet (r2 F8)

        flow.BeginSafetyQuestions();                       // the transition card's Begin
        Check.That(s.SafetyMessages).HasSize(1);           // question 0 arrived on the surface
        for (var i = 0; i < 4; i++)
            s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        s.LastSafetyChoices!.Single(c => c.Id == "safety_basic").OnChosen!.Invoke();
        Check.That(settings.General.SafetySetupDone).IsTrue();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Done);
        flow.Advance();                                     // Finish (r1 F4)
        Check.That(exit).IsEqualTo(GuideExitReason.Finished);
        Check.That(s.CloseCount).IsEqualTo(1);
    }

    [Fact]
    public void NoTracker_OmitsCalibrate_AndFloatingToolbar_OmitsToolbarTask()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings, tracker: false, dockedToolbar: false);

        flow.Start();
        flow.Advance();
        flow.NotifyTaskSucceeded();
        flow.Advance();                                   // → Theme (Calibrate AND ToolbarTask omitted)
        Check.That(s.Shown).ContainsOnlyElementsThatMatch(
            st => st is FirstRunGuideStep.Welcome or FirstRunGuideStep.MeetDwell
                       or FirstRunGuideStep.DwellTime or FirstRunGuideStep.Theme);
    }

    [Fact]
    public void Skip_WorksOnTasksAndCalibrate_Only()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Advance();                                   // Welcome → MeetDwell
        flow.Skip();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Skip();                                      // INVALID on DwellTime (Continue-only)
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // → Calibrate
        flow.Skip();                                      // → ToolbarTask
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
    }

    [Fact]
    public void Later_OnWelcome_AndChromeClose_AbandonWithoutTheFlag()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        GuideExitReason? exit = null;
        flow.Closed += reason => exit = reason;
        flow.Start();
        flow.Close();
        Check.That(s.CloseCount).IsEqualTo(1);
        Check.That(exit).IsEqualTo(GuideExitReason.Abandoned);
        Check.That(settings.General.SafetySetupDone).IsFalse();
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

    [Fact]
    public void Host_RefusesASecondFlow_WhileOneIsOpen()
    {
        var host = new FirstRunGuideHost();
        Check.That(host.TryBegin(out var lease1)).IsTrue();
        Check.That(host.TryBegin(out _)).IsFalse();
        lease1.Dispose();
        Check.That(host.TryBegin(out var lease2)).IsTrue();
        lease2.Dispose();
    }
}
```

- [x] **Step 2: Run → FAIL. Step 3: Implement `FirstRunGuideFlow.cs`** — exactly the rev 4 body, restated here in full:

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
    /// the toolbar hold (§5 claim 6) and the ring lifecycle (spec §5) here.</summary>
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

    /// <summary>The transition card's Begin: starts the absorbed question chain (spec §7 step 7).
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
            // chain is created here but NOT started — the transition card's Begin calls
            // BeginSafetyQuestions() (r2 F8).
            _safetyFlow = _createSafetyFlow(
                (message, choices) => _surface.ShowSafetyCard(message, choices),
                completed => NotifySafetyEnded(completed));
        }

        _surface.ShowStep(step);
    }
}
```

`FirstRunGuideHost.cs`:

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

- [x] **Step 4: Run → PASS** (`--filter "FullyQualifiedName~FirstRunGuideFlowTests"`).
- [x] **Step 5: Commit** `feat(guide): headless flow + host — transition-gated safety block, Finish exit`.

---

### Task 6: `FirstRunGuideViewModel` — binding surface, gaze contract, latch protocol

**Files:**
- Create: `src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs`
- Modify: `tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs` (census row — the file sits at the App.Tests root) + `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` (expected map)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs` (new)

**Interfaces:** Produces the VM with: `IsVisible` (the wiring sets true at Show; the test helper sets it); `DwellActiveWhilePaused => true`; `DpiHelper` via `using EyeCtrl.Core.Services`; `ChoiceRadiusDip = 70` computed per registration; `ShowCard`; `ShowSafetyCard`/`SafetyChoices`; `RegisterChoice(id)` at origin + `Hold(id)` — uniform for every chrome target — firing `ChoiceActivated(id)` (or `CloseRequested` for `guide_close`); `UpdateChoiceCentre`; `ClearChoices`; `RemoveChromeChoice(id)`; `SetPracticeCentre`/`ReTimePracticeTarget`/`SetDwellTime` (live re-timing); `Set*Visible`; `Receive` with `_latch.Filter`; `ChoiceProgress`/`PracticeProgress` events; `OnChromeChoice(id)` (the window's uniform route); `ChoiceMayDwell(id)` probe.

- [x] **Step 1: The gate entries** — `OverlayTelemetryWiringTests`' map gains `[nameof(FirstRunGuideViewModel)] = DwellContext.Other`; `DwellSurfaceCensusTests`' `DetectorSites` array gains a tuple (the array is `(string Path, string Classification, string Reason)[]`, `DwellSurfaceCensusTests.cs:17-33` — NOT dictionary-indexer form, r6 F1):

```csharp
        ("EyeCtrl.ViewModels/FirstRunGuideViewModel.cs", "cue", "first-run guide chrome cards + practice target"),
```

- [x] **Step 2: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

public class FirstRunGuideViewModelTests
{
    private static FirstRunGuideViewModel Vm() =>
        new(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };

    [Fact]
    public void DwellActiveWhilePaused_IsTrue()
    {
        Check.That(Vm().DwellActiveWhilePausedForTest).IsTrue();
    }

    [Fact]
    public void PracticeTarget_UsesActivationDwell_AndFeedsBothDetectors()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 300;
        settings.Dwell.ToolbarButtonDwellMs = 900;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("practice");

        long t = 0;
        for (var i = 0; i < 12; i++)   // 300 ms at 30 ms samples — the ACTIVATION knob, not 900
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        // The activation knob fired (the cooldown suppresses a re-fire inside the 360 ms window).
        Check.That(activated).Not.IsEmpty();
    }

    [Fact]
    public void Gaze_GatesOnIsInGap_NotOnIsPaused()
    {
        var vm = Vm();
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);
        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("p");

        long t = 0;
        for (var i = 0; i < 30; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t) { IsPaused = true });
            t += 30_000;
        }
        Check.That(activated).Not.IsEmpty();
    }

    [Fact]
    public void Latch_HoldsNewChoices_UntilGazeLeaves_CentrePushArms_OnePressDispatches()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ToolbarButtonDwellMs = 300;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        var fired = new List<string>();
        vm.ChoiceActivated += id => fired.Add(id);
        vm.RegisterChoice("c_yes");                           // at origin, held (Settings pattern)
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();

        vm.UpdateChoiceCentre("c_yes", new PhysicalPoint(400, 300));
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();     // gaze never left it yet

        long t = 0;
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(900, 900), t)); t += 30_000;
        for (var i = 0; i < 11; i++)                          // 300 ms at 30 ms: arm + dwell + fire
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(400, 300), t));
            t += 30_000;
        }
        Check.That(fired).HasSize(1);                         // armed, dwelled, dispatched
    }

    [Fact]
    public void SetDwellTime_ReTimesThePracticeTarget()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 900;
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), dwellCooldownMs: 0,
            settings: settings, sink: null, dwellRegistry: null) { IsVisible = true };
        vm.SetPracticeCentre(new PhysicalPoint(280, 220));
        vm.SetPracticeVisible(true);

        vm.SetDwellTime(200);
        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("p");

        long t = 0;
        for (var i = 0; i < 8; i++)   // 240 ms — under the OLD 900, over the NEW 200
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        Check.That(activated).Not.IsEmpty();
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(200);
    }

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
        Check.That(progress[^1]).IsStrictlyGreaterThan(0);
    }
}
```

- [x] **Step 3: Run → FAIL** (both filters from rev 4).

- [x] **Step 4: Implement** — the rev 4 VM body with r4's corrections restated in full:

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
/// latch.Positioned, :208-212); the latch's filter rides every DwellDetector.Update (:166); ALL
/// chrome targets clear before a fired id dispatches (:317-318) — the wiring re-renders the card
/// when the press did not change the step. The practice target carries its OWN detector at
/// ActivationButtonDwellMs (two-knob split) with the forgiving-dwell grace, re-timed live by
/// SetDwellTime. Gaze routes through SampleMayDwell (gates !IsInGap, NOT IsPaused) —
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
    /// <summary>The dwell-time changed (clamped 200–2000 ms).</summary>
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

    /// <summary>The two knob values the step-3 writes through (200–2000).</summary>
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
    /// real centre through <see cref="UpdateChoiceCentre"/>; a fired id clears ALL chrome targets
    /// before dispatching (the Settings precedent, :317-318) — the wiring re-renders the card
    /// when the press did not change the step (r4 F3). <paramref name="radiusDip"/> defaults to
    /// <see cref="ChoiceRadiusDip"/> (70); the Theme chips pass 40 — a 2×2 grid at this card size
    /// would overlap 70-DIP circles (r5 F4).</summary>
    public void RegisterChoice(string id, double? radiusDip = null)
    {
        DwellDetector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0),
            DpiHelper.LogicalToPhysicalX(radiusDip ?? ChoiceRadiusDip), _settings.Dwell.ToolbarButtonDwellMs));
        _latch.Hold(id);
    }

    /// <summary>Removes one chrome target and its latch state (the dwell ± on a step that lost
    /// its knob).</summary>
    public void RemoveChromeChoice(string id)
    {
        DwellDetector.RemoveTarget(id);
        _latch.Forget(id);
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

- [x] **Step 5: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests|FullyQualifiedName~DwellSurfaceCensus"` → PASS; `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring"` → PASS.

- [x] **Step 6: Commit** — the REAL census path (`tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs`, r3 F11):

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideViewModel — unified chrome targets, latch protocol, progress events"
```

---

### Task 7: `FirstRunGuideWindow` — code-built visuals, one slot per builder, real centres

**Files:**
- Create: `src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml` + `.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row, `exempt:`)
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`RaiseToTop()` default method on `IToolbarOverlay`, `ICursorCircleOverlay`, `IPauseIndicatorOverlay`)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` (new, STA, `[Trait("Category", "UI")]`)

**Interfaces:** Produces the window with: fullscreen geometry (read live); `ShowCardContent`; `SetChoices(IReadOnlyList<(string Label, string Id)>)` — brushed cards with per-card progress bars, each registered via `vm.RegisterChoice(id)`, centres pushed via `PointToScreen`; `SetPractice(bool)` building the stroked ring + fill element into `StepContent` and pushing its centre; **`SetDwellKnob(bool)` building the value read-out + −/+ cards into its own `KnobRow`** (r4 F4 — never `StepContent`), removing the ± targets when the knob leaves; `HideGuide()/ShowGuide()`; `CardResources`; **NO `Close()` member** (r4 F2); NO `HideCard`/`ShowCard` (unused).

- [ ] **Step 1: XAML** — five content rows + the chrome Close's own host (row 0, right of the title); the card grows to 480 DIP for the DwellTime layout budget (r3 F5's geometry):

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
        <Border x:Name="Card" Width="560" Height="620"
                Background="{DynamicResource BrushSurfaceDark}"
                CornerRadius="{DynamicResource LargeCornerRadius}"
                HorizontalAlignment="Center" VerticalAlignment="Center">
            <Grid Margin="32">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <TextBlock x:Name="TitleText" Grid.Row="0" FontSize="24" FontWeight="Medium"
                           Text="{Binding Title}" TextWrapping="Wrap"
                           Margin="0,0,186,0"
                           Foreground="{DynamicResource BrushTextPrimary}"/>
                <ContentControl x:Name="ChromeCloseHost" Grid.Row="0"
                                HorizontalAlignment="Right" VerticalAlignment="Top"/>
                <TextBlock x:Name="BodyText" Grid.Row="1" Margin="0,10,0,0" TextWrapping="Wrap"
                           Text="{Binding Body}" Foreground="{DynamicResource BrushTextPrimary}"/>
                <ContentControl x:Name="StepContent" Grid.Row="2" Margin="0,16,0,0"/>
                <StackPanel x:Name="KnobRow" Grid.Row="3" Orientation="Horizontal"
                            HorizontalAlignment="Center"/>
                <StackPanel x:Name="ChoiceRow" Grid.Row="4" Orientation="Horizontal"
                            HorizontalAlignment="Right"/>
            </Grid>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Code-behind** — one slot per builder; `Card.FindResource` so the theme preview reaches the code-built cards (r4's S3); cards sized as the modal buttons (170 × 100 DIP, r3 F7's geometry); the chrome Close lives in its OWN host away from the action row (r3 F7); the knob re-registers on re-renders (r5 F2):

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
/// interactive card. RENDER-ONLY for text; the window BUILDS the step visuals and pushes every
/// dwell target's real centre into the VM after layout — PointToScreen yields device (physical)
/// pixels (r2 F9). ONE SLOT PER BUILDER (r4 F4): SetPractice builds into StepContent, the dwell
/// knob into KnobRow, the chrome Close into ChromeCloseHost — neither overwrites another. Every
/// builder's already-built branch RE-REGISTERS (r5 F2: a re-render cleared the targets). No
/// Close() member (r4 F2). Card-local brushes resolve through Card.FindResource so the theme
/// preview reaches code-built cards.
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;
    private readonly Dictionary<string, FrameworkElement> _choiceCards = new();
    private readonly Dictionary<string, Rectangle> _progressBars = new();
    private readonly Dictionary<string, Border> _knobCards = new();      // the knob's OWN references — SetChoices never clears them
    private readonly Dictionary<string, Rectangle> _knobBars = new();
    private readonly Dictionary<string, Border> _themeCards = new();     // the chip grid's OWN references — SetChoices never clears them
    private readonly Dictionary<string, Rectangle> _themeBars = new();
    private Border? _chromeCloseCard;
    private Rectangle? _chromeCloseBar;
    private Grid? _themeGrid;                                            // re-attached on a Theme re-render (r6 F4)
    private Ellipse? _practiceFill;
    private TextBlock? _dwellValue;
    private bool _knobBuilt;
    private bool _chromeCloseBuilt;

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
        vm.PracticeProgress += SetPracticeFill;

        // The dwell read-out follows the knob (ONE subscription in the ctor — no pile-up, r4 S3).
        vm.DwellTimeChanged += ms =>
        {
            if (_dwellValue is { } value) value.Text = $"{ms} ms";
        };
    }

    /// <summary>The theme preview's card-local target (Task 12 applies the resolved theme here;
    /// DynamicResource on the card's brushes picks it up, Tokens.Current stays untouched).</summary>
    public ResourceDictionary CardResources => Card.Resources;

    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowCard(title, body, chromeClose);

    /// <summary>Builds one visual card per (label, id) pair — background, border, label,
    /// progress bar — registers each as a chrome dwell target through the VM (Task 6), then
    /// pushes the laid-out centres (r3 F5: EVERY card gets a visual and a centre). Cards are
    /// modal-button sized (170 × 100 DIP, r3 F7).</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id)> choices)
    {
        _vm.ClearChoices();
        _choiceCards.Clear();
        _progressBars.Clear();
        ChoiceRow.Children.Clear();
        foreach (var (label, id) in choices)
        {
            var card = BuildCard(label, id);
            _choiceCards[id] = card;
            ChoiceRow.Children.Add(card);
            _vm.RegisterChoice(id);
        }
        PushCentres();
    }

    private Border BuildCard(string label, string id)
    {
        var bar = new Rectangle
        {
            Height = 3,
            Width = 0,
            Fill = (Brush)Card.FindResource("BrushAccent"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _progressBars[id] = bar;
        return new Border
        {
            Width = 170,
            Height = 100,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Margin = new Thickness(12, 8, 12, 4),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Card.FindResource("BrushTextPrimary"),
                    },
                    bar,
                },
            },
            Margin = new Thickness(16, 0, 0, 0),
            Background = (Brush)Card.FindResource("BrushSurfaceMedium"),
            BorderBrush = (Brush)Card.FindResource("BrushAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
    }

    /// <summary>The chrome Close card in its OWN host (top-right of the card, r3 F7 — away from
    /// the action row, so abandoning is never the neighbour of continuing). Idempotent; the
    /// already-built branch RE-REGISTERS (r5 F2: a re-render cleared the targets).</summary>
    public void SetChromeClose(bool visible, string label)
    {
        if (visible && !_chromeCloseBuilt)
        {
            _chromeCloseCard = BuildCard(label, "guide_close");
            _chromeCloseBar = (Rectangle)((StackPanel)_chromeCloseCard.Child).Children[1];
            _choiceCards["guide_close"] = _chromeCloseCard;
            _progressBars["guide_close"] = _chromeCloseBar;
            ChromeCloseHost.Content = _chromeCloseCard;
            _vm.RegisterChoice("guide_close");
            _chromeCloseBuilt = true;
            PushCentres();
        }
        else if (visible && _chromeCloseBuilt)
        {
            // Re-render (r6 F4): SetChoices cleared the card and bar entries — restore BOTH from
            // the Close's own fields, so an accidental dwell on Close fills its bar visibly.
            _choiceCards["guide_close"] = _chromeCloseCard;
            _progressBars["guide_close"] = _chromeCloseBar;
            _vm.RegisterChoice("guide_close");
            PushCentres();
        }
        else if (!visible && _chromeCloseBuilt)
        {
            _vm.RemoveChromeChoice("guide_close");
            _choiceCards.Remove("guide_close");
            _progressBars.Remove("guide_close");
            ChromeCloseHost.Content = null;
            _chromeCloseCard = null;
            _chromeCloseBar = null;
            _chromeCloseBuilt = false;
        }
    }

    /// <summary>The practice target: a stroked ring with a fill element whose size follows the
    /// VM's PracticeProgress (r3 F7 — MeetDwell's lesson is "the ring fills"). Builds into
    /// StepContent (r4 F4's slot rule).</summary>
    public void SetPractice(bool visible)
    {
        _vm.SetPracticeVisible(visible);
        if (visible && StepContent.Content is not Grid)
        {
            _practiceFill = new Ellipse
            {
                Width = 0,
                Height = 0,
                Fill = (Brush)Card.FindResource("BrushAccent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ring = new Ellipse
            {
                Width = 120,
                Height = 120,
                Stroke = (Brush)Card.FindResource("BrushAccent"),
                StrokeThickness = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid();
            grid.Children.Add(ring);
            grid.Children.Add(_practiceFill);
            StepContent.Content = grid;
        }
        else if (!visible)
        {
            StepContent.Content = null;
            _practiceFill = null;
        }
        PushCentres();
    }

    /// <summary>The dwell knob (r3 F4): a value read-out plus −/+ chrome cards, built into its
    /// OWN row (r4 F4 — SetPractice's StepContent slot is never touched). Arriving registers the
    /// ± targets; an already-built knob RE-REGISTERS on a re-render (r5 F2); leaving removes
    /// them.</summary>
    public void SetDwellKnob(bool visible)
    {
        _vm.SetSliderVisible(visible);
        if (visible && !_knobBuilt)
        {
            _dwellValue = new TextBlock
            {
                Text = $"{_vm.DwellTimeMs} ms",
                FontSize = 20,
                Foreground = (Brush)Card.FindResource("BrushTextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
            };
            var plusMinus = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (glyph, id) in new[] { ("−", "guide_dwell_minus"), ("+", "guide_dwell_plus") })
            {
                var card = BuildCard(glyph, id);
                _choiceCards[id] = card;
                _progressBars[id] = (Rectangle)((StackPanel)card.Child).Children[1];
                _knobCards[id] = card;
                _knobBars[id] = _progressBars[id];
                plusMinus.Children.Add(card);
                _vm.RegisterChoice(id);
            }
            KnobRow.Children.Add(_dwellValue);
            KnobRow.Children.Add(plusMinus);
            _knobBuilt = true;
            PushCentres();
        }
        else if (visible && _knobBuilt)
        {
            // Re-render (r5 F2): SetChoices cleared every chrome target — put the ± back from
            // the knob's OWN card references (SetChoices never touches _knobCards/_knobBars).
            foreach (var id in new[] { "guide_dwell_minus", "guide_dwell_plus" })
            {
                _choiceCards[id] = _knobCards[id];
                _progressBars[id] = _knobBars[id];
                _vm.RegisterChoice(id);
            }
            PushCentres();
        }
        else if (!visible && _knobBuilt)
        {
            KnobRow.Children.Clear();
            _vm.RemoveChromeChoice("guide_dwell_minus");
            _vm.RemoveChromeChoice("guide_dwell_plus");
            _choiceCards.Remove("guide_dwell_minus");
            _choiceCards.Remove("guide_dwell_plus");
            _progressBars.Remove("guide_dwell_minus");
            _progressBars.Remove("guide_dwell_plus");
            _dwellValue = null;
            _knobBuilt = false;
        }
    }

    private void SetProgressBar(string id, double progress)
    {
        if (_progressBars.TryGetValue(id, out var bar) && _choiceCards.TryGetValue(id, out var card))
            bar.Width = (card.ActualWidth - 2) * Math.Clamp(progress, 0, 1);
    }

    /// <summary>The Theme step's four chips as a 2×2 grid in StepContent (r5 F4's geometry —
    /// six 170-wide cards in one right-aligned row overflow the 496-DIP inner width, and
    /// 70-DIP circles would overlap). Each chip registers at radius 40 DIP through
    /// <see cref="FirstRunGuideViewModel.RegisterChoice(string, double?)"/>. Idempotent; the
    /// already-built branch RE-REGISTERS (r5 F2). Chips live in `_themeCards` — `SetChoices`
    /// never clears them.</summary>
    public void SetThemeGrid(bool visible, IReadOnlyList<(string Label, string Id)> chips)
    {
        if (visible)
        {
            if (_themeCards.Count == 0)
            {
                var grid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < chips.Count; i++)
                {
                    var (label, id) = chips[i];
                    var card = BuildChipCard(label, id);
                    _themeCards[id] = card;
                    _themeBars[id] = (Rectangle)((StackPanel)card.Child).Children[1];
                    Grid.SetRow(card, i / 2);
                    Grid.SetColumn(card, i % 2);
                    grid.Children.Add(card);
                    _choiceCards[id] = card;                       // r6 F4: progress + centres via _choiceCards
                    _progressBars[id] = _themeBars[id];
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
                _themeGrid = grid;
                StepContent.Content = grid;
            }
            else
            {
                // Re-render (r6 F4): SetPractice(false) emptied StepContent — re-attach the grid
                // AND restore the chips' card/bar entries (SetChoices cleared them), so a theme
                // can be picked on every visit and the Continue/clear bookkeeping stays live.
                if (StepContent.Content is not Grid g || !ReferenceEquals(g, _themeGrid))
                    StepContent.Content = _themeGrid;
                foreach (var (id, _) in _themeCards)
                {
                    _choiceCards[id] = _themeCards[id];
                    _progressBars[id] = _themeBars[id];
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
            }
            PushCentres();
        }
        else if (_themeCards.Count > 0)
        {
            foreach (var id in _themeCards.Keys)
                _vm.RemoveChromeChoice(id);
            _themeCards.Clear();
            _themeBars.Clear();
            _themeGrid = null;
            StepContent.Content = null;
        }
    }

    private Border BuildChipCard(string label, string id)
    {
        var bar = new Rectangle
        {
            Height = 3,
            Width = 0,
            Fill = (Brush)Card.FindResource("BrushAccent"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _progressBars[id] = bar;
        return new Border
        {
            Width = 170,
            Height = 80,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Margin = new Thickness(12, 8, 12, 4),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Card.FindResource("BrushTextPrimary"),
                    },
                    bar,
                },
            },
            Margin = new Thickness(16, 20, 16, 20),
            Background = (Brush)Card.FindResource("BrushSurfaceMedium"),
            BorderBrush = (Brush)Card.FindResource("BrushAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
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
            if (StepContent.Content is Grid { Children.Count: > 1 } grid
                && grid.Children[0] is Ellipse ring && ring.IsVisible)
            {
                var centre = ring.PointToScreen(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2));
                _vm.SetPracticeCentre(new PhysicalPoint(centre.X, centre.Y));
            }
            foreach (var (id, chip) in _themeCards)                       // the chip grid's centres
            {
                if (!chip.IsVisible) continue;
                var centre = chip.PointToScreen(new Point(chip.ActualWidth / 2, chip.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
        });
    }
}
```

(The knob's ± cards and the chrome Close card keep their OWN field references (`_knobCards`/`_knobBars`/`_chromeCloseCard`), which `SetChoices` never clears — the re-register branches read from them (r5 F2).)

- [ ] **Step 3: Overlay inventory row** (`exempt:`) + **Step 4: `IOverlayWindows.cs`** — the default method on THREE interfaces:

```csharp
    /// <summary>Raises this window above any later-created fullscreen topmost overlay (the
    /// guide). Default no-op so the test fakes need no member; the real OverlayWindow's
    /// implementation (:377) overrides it.</summary>
    void RaiseToTop() { }
```

- [ ] **Step 5: STA test** — the rev 4 test with `IsStrictlyGreaterThan` (r4's S3):

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
            Check.That(window.Width).IsStrictlyGreaterThan(SystemParameters.PrimaryScreenWidth - 1);
            Check.That(window.Height).IsStrictlyGreaterThan(SystemParameters.PrimaryScreenHeight - 1);
        }
        finally
        {
            window.Close();
        }
    }
}
```

- [ ] **Step 6: Run** — the two filters → PASS; `--filter "FullyQualifiedName~Toolbar"` → PASS.

- [ ] **Step 7: Commit** `feat(guide): window builds brushed visuals one-slot-per-builder with real centres; RaiseToTop on three interfaces`.

---

### Task 8: `ToolbarAutoHideController` — guide-hidden hold + `ResetToHidden`

**Files:** Modify `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`; Test extend `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` (harness: `NewController` at :755, `Attach`, `FakeVisibility` with `ShowCalls/HideCalls/ForceShowCalls/SetActiveCalls`).

**Interfaces:** `SetGuideHiddenHold(bool)` (suppress `ForceShow` + `ReassertVisibility`'s `SetActive(true)` re-arm, force Hidden) + `ResetToHidden()` (mirror `ResetToVisible()` at :551). **Order rule (r1 F11): the hold arms BEFORE any `SetSuspended(true)`.**

- [ ] **Step 1: Write the failing tests** (the sample-drive helper is extracted from `GazeBackOnToolbar_ResetsHideCountdown` :83 — the same `sut.Feed(CreateSample(x, y, valid: true), timestampUs)` loop advanced past `_hideDelayUs`):

```csharp
    [Fact]
    public void GuideHiddenHold_SuppressesTheAsyncForceShow_FromSuspend()
    {
        var sut = NewController();
        var fake = new FakeVisibility();
        sut.Attach(fake);

        DriveToHidden(sut);                       // the extracted off-toolbar sample loop
        Check.That(sut.StateName).IsEqualTo("Hidden");

        sut.SetGuideHiddenHold(true);             // the hold FIRST (r1 F11's order)...
        sut.SetSuspended(true);                   // ...then the suspend
        Check.That(fake.ForceShowCalls).IsEqualTo(0);   // the posted ForceShow is suppressed (r3 F2a)
        Check.That(sut.StateName).IsEqualTo("Hidden");
    }

    [Fact]
    public void GuideHiddenHold_NeutralizesReassertVisibility()
    {
        var sut = NewController();
        var fake = new FakeVisibility();
        sut.Attach(fake);
        sut.SetGuideHiddenHold(true);

        sut.ReassertVisibility();                 // calibration transitions call this unconditionally

        Check.That(fake.SetActiveCalls).IsEmpty(); // no dwell re-arm (r3 F2b)
    }

    [Fact]
    public void ResetToHidden_PutsTheControllerInHidden_ForAnImmediateSummon()
    {
        var sut = NewController();
        var fake = new FakeVisibility();
        sut.Attach(fake);
        sut.ResetToVisible();
        Check.That(sut.StateName).IsEqualTo("Visible");

        sut.ResetToHidden();

        Check.That(sut.StateName).IsEqualTo("Hidden");
        Check.That(fake.HideCalls).IsGreaterOrEqualThan(1);
        // A past-edge summon now detects immediately — Feed's Hidden case runs with no
        // 5 s hide-delay wait (r4 F2).
    }
```

- [ ] **Step 2: Implement** — the `_guideHiddenHold` field + the two methods:

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

and the two suppression points (the state write is gated TOO — `SetSuspended`'s suspended branch unconditionally sets `_state = State.Visible` (:208), which would break the hold's own promise, r6 F2):

```csharp
    // in SetSuspended's suspended branch, replacing the ForceShow line AND the state write:
            if (_state == State.Hidden && _enabled && !_guideHiddenHold)
                _visibility?.ForceShow();
            if (!_guideHiddenHold)
                _state = State.Visible;

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

**Interfaces:** `SetGuideHold(bool)` — **hidden hold FIRST, then `SetAutoHideSuspended("guide", …)`** (r1 F11); `ResetToolbarForGuideTask()`; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` + `guideOpen` (all call sites + source pins); `ToolbarViewModel.GuideOpenGate` (assigned in Task 12, checked AFTER the :537-541 progress/highlight reset lines). The gated press does not stop the dwell cue (the base publishes after `OnTargetActivated` returns) — accepted, documented (r3 F19). `ToolbarVisibilityChanged` REUSED (:52/:366).

- [ ] **Step 1: Failing tests**

```csharp
    [Fact]
    public void ShouldFeedKeyboardSample_RejectsWhileGuideOpen()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true,
            isInGap: false, probeOpen: false, exerciseActive: false, guideOpen: true)).IsFalse();
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true,
            isInGap: false, probeOpen: false, exerciseActive: false, guideOpen: false)).IsTrue();
    }

    [Fact]
    public void ShouldFeedSettingsSample_RejectsWhileGuideOpen()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedSettingsSample(
            exerciseActive: false, guideOpen: true)).IsFalse();
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedSettingsSample(
            exerciseActive: false, guideOpen: false)).IsTrue();
    }
```

- [ ] **Step 2: Implement** — the two static feeds gain the parameter and term (EVERY call site — 11 in the coordinator + the `ProbeGateWiring_IsPinnedAtTheSource` source pins at :361-376 — updated in this task; the gaze-thread call sites pass `_modal.GuideOpen`):

```csharp
    internal static bool ShouldFeedKeyboardSample(bool paused, bool modalKeyboardVisible, bool keyboardIsVisible,
        bool isInGap, bool probeOpen, bool exerciseActive, bool guideOpen) =>
        !paused && modalKeyboardVisible && keyboardIsVisible && !isInGap && !probeOpen && !exerciseActive && !guideOpen;

    internal static bool ShouldFeedSettingsSample(bool exerciseActive, bool guideOpen) =>
        !exerciseActive && !guideOpen;
```

plus two methods:

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

`ToolbarViewModel.cs` — the gate property + the gate INSIDE `OnTargetActivated` AFTER the progress/highlight reset lines (:537-541) so a gated press still cleans the button's visual state; the gated press does not stop the dwell cue (the base publishes after `OnTargetActivated` returns) — accepted, documented (r3 F19):

```csharp
    /// <summary>While the first-run guide owns the screen, every non-Pause activation is inert
    /// (§5 claim 7): the summoned toolbar is a display, not a control surface. Pause stays live
    /// (the deliberate emergency path). Null (tests, non-guide wiring) = not gated. Assigned by
    /// Task 12's wiring.</summary>
    internal Func<bool>? GuideOpenGate { get; set; }
```

```csharp
    protected override void OnTargetActivated(string targetId)
    {
        // ... the existing progress-reset / highlight lines first (:537-541) ...
        if (targetId != "Pause" && GuideOpenGate?.Invoke() == true)
            return;
        // ... the existing switch ...
```

- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ordered toolbar hold + GuideOpen feed terms + OnTargetActivated gate`.

---

### Task 10: Ring lifecycle — the coordinators' gate + restore

**Files:** Modify `OverlayVmBindingCoordinator.cs`, `OverlayMessageCoordinator.cs`; Test extend the coordinators' existing test files.

**Interfaces:** `OverlayVmBindingCoordinator.RingMayReshow => !_modal.GuideOpen` (the message coordinator has NO `_modal` — its ctor takes `(toolbar, vmBinding)`, `EventCoordinator.cs:212`); both calibration re-show sites consult it. The FULL ring lifecycle lives in Task 12's wiring.

- [ ] **Step 1: Failing test** — `RingMayReshow_IsFalse_WhileGuideOpen`, in the coordinators' existing test file with its harness (`modal`, `vmBinding`):

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

- [ ] **Step 2: Implement** — the flag on `OverlayVmBindingCoordinator`; its `IsVisible` case gains `&& !_modal.GuideOpen`; both calibration `cursorCircle.Show()` sites (:308/:318) become `if (Context.CursorVM.IsVisible && _vmBinding.RingMayReshow) cursorCircle.Show();`.
- [ ] **Step 3: Run → PASS. Step 4: Commit** `feat(guide): ring re-show gated on GuideOpen via the binding coordinator's flag`.

---

### Task 11: Proactive surfaces — BannerSlot hold, hosts, SetupNoticeWatch

**Files:** Modify `BannerSlot.cs`, `RestartNoticeHost.cs`, `LagMarkHost.cs`, `SetupNoticeWatch.cs`; Test `BannerSlotHoldTests.cs` (new) + host extensions.

**Interfaces:** `BannerSlot.Hold()/Release()` (front re-queue; `Preempt` deliberately bypasses — caregiver alerts take the slot mid-guide, pinned by test); optional `Func<bool>? isGuideOpen = null` on both hosts + `OnGuideOpenChanged` (defer-newest replay); `SetupNoticeWatch`: `CheckStartupWhenGuideClosed()` at the three call sites + expiry skip while GuideOpen + close-time poke.

- [ ] **Step 1: Failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;
using Xunit;

public class BannerSlotHoldTests
{
    private sealed class FakeBanner : IBannerSlotOccupant
    {
        public event EventHandler? Closed;
        public int Shows; public int Hides;
        public void ShowInSlot() => Shows++;
        public void HideFromSlot() => Hides++;
        public bool TryGetVisibleBounds(out Core.Models.PhysicalRect bounds) { bounds = default; return false; }
        public IReadOnlyList<Core.Models.PhysicalRect> LiveBoundsOrEmpty() => Array.Empty<Core.Models.PhysicalRect>();
        public void ReassertForOrder() { }
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void Hold_HidesAndRequeuesLive_AtTheFront_ReleaseResumesWithoutDrops()
    {
        var slot = new BannerSlot();
        var a = new FakeBanner(); var b = new FakeBanner();
        slot.Enqueue(a);
        Check.That(slot.Live).IsSameReferenceAs(a);

        slot.Hold();
        Check.That(a.Hides).IsEqualTo(1);
        Check.That(slot.Live).IsNull();

        slot.Enqueue(b);                       // queued, not shown during the hold
        Check.That(b.Shows).IsEqualTo(0);

        slot.Release();
        Check.That(slot.Live).IsSameReferenceAs(a);   // front of the queue: resumes first
        Check.That(b.Shows).IsEqualTo(0);             // still waiting its turn — nothing dropped
    }

    [Fact]
    public void Preempt_BypassesTheHold_CaregiverAlertsAreTheSafetyChannel()
    {
        var slot = new BannerSlot();
        var live = new FakeBanner(); var alert = new FakeBanner();
        slot.Enqueue(live);
        slot.Hold();
        Check.That(slot.Live).IsNull();

        slot.Preempt(alert);                   // pinned behaviour (r1 F24): the alert TAKES the slot

        Check.That(slot.Live).IsSameReferenceAs(alert);
        Check.That(alert.Shows).IsEqualTo(1);
        Check.That(live.Shows).IsEqualTo(1);   // re-queued at the front; resumes after the alert
        slot.Release();                        // the hold release does not disturb the live alert
        Check.That(slot.Live).IsSameReferenceAs(alert);
    }

    [Fact]
    public void Hold_WithAnEmptySlot_QueuesWithoutShowing()
    {
        var slot = new BannerSlot();
        var a = new FakeBanner();
        slot.Hold();
        slot.Enqueue(a);
        Check.That(slot.Live).IsNull();
        slot.Release();
        Check.That(slot.Live).IsSameReferenceAs(a);
    }
}
```

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

(each show path: `if (_isSettingsOpen() || _isProbeOpen()) return; if (_isGuideOpen?.Invoke() == true) { _pendingOnGuideClosed = () => ShowInProgress(); return; } ShowInProgress();`)

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

`SetupNoticeWatch.cs` — optional `Func<bool>? isGuideOpen = null` (App.xaml.cs:577); ONE gated helper replaces the three raw `CheckStartup()` call sites (Start :91, Poll :110, Poke :136 — each becomes `if (CheckStartupWhenGuideClosed())`), and the expiry check also skips while the guide is open (the 90 s window must not run out behind the guide — r1 F24):

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

**Interfaces:** Consumes Tasks 1–11. Produces the complete raise with the unified choice list and re-render after in-place presses.

- [ ] **Step 1: Rename the message** — `FirstRunGuideRequestedMessage` replaces `SafetySetupRequestedMessage` (delete the old record; `SafetySetupStartup.MaybeRequest` sends the new one).

- [ ] **Step 2: The per-run factory in `SettingsUiCoordinator`** — fields + registration:

```csharp
    private FirstRunGuideHost? _guideHost;
    private SplashThreadHandle? _splashHandle;
    private FirstRunGuideWindow? _guideWindow;
    private FirstRunGuideViewModel? _guideVm;
    private FirstRunGuideFlow? _guideFlow;
    private GuideSurface? _guideSurface;
    private Action<bool>? _onToolbarVisibility;   // a FIELD, not a local function (r3 F6)
```

```csharp
        // First-run guide: raised at startup (the renamed gate) and from the Settings → General row.
        messenger.Register<SettingsUiCoordinator, FirstRunGuideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(StartFirstRunGuide));
```

```csharp
    /// <summary>Hands the splash handle in for the latched show. Called once from App.xaml.cs
    /// via EventCoordinator's forwarder (r1 F15) after Initialize.</summary>
    public void SetSplashHandle(SplashThreadHandle? handle) => _splashHandle = handle;

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
    /// the session back — toolbar restored, ring restored, lease released, cards started. No
    /// re-entry: the flow is nulled BEFORE the window closes, and the toolbar handler is
    /// unsubscribed first (r3 F9).</summary>
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
    /// starts even when the raise failed (r3 F6 / r2 F14). Cleared in Restore (r2 F19).</summary>
    internal Action? StatusCardsFallback { get; set; }

    private void BuildAndRaiseGuide(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        var settings = context.Settings;
        bool tracker = App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool toolbarTask = tracker && settings.Toolbar.IsDocked;   // spec §7 step 5

        _guideVm = new FirstRunGuideViewModel(context.Messenger,
            dwellCooldownMs: settings.Dwell.DwellCooldownMs,       // a COOLDOWN, not a dwell time (r1 F23)
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

        // Theme pick: card-local preview + live-apply + save, then RE-RENDER the card (r4 F3 —
        // OnTargetActivated cleared every chrome target, and a chip press does not change the step).
        _guideVm.ThemeChosen += name => context.Dispatcher.BeginInvoke(() =>
        {
            var theme = ThemeRegistry.Resolve(name);
            ThemeResourceApplier.Apply(_guideWindow!.CardResources, theme);   // card-local (r1 F2)
            settings.Theme.Name = name;
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
            _guideSurface?.RenderChoicesForCurrentStep();      // re-register the cleared targets
        });

        // Dwell ±: live-apply + save, then RE-RENDER the card (r4 F3 — same cleared-targets rule).
        _guideVm.DwellTimeChanged += _ =>
        {
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
            _guideSurface?.RenderChoicesForCurrentStep();
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
                context.OverlayManager.CursorCircle.Hide();            // the ring hides — INSIDE Show
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

        // The summon observation — a FIELD-SUBSCRIBED private method (r3 F6), gated on the task,
        // swapping the body to the success text (spec §7.5). NO re-render: the summon is a
        // toolbar event, not a card activation — no targets were cleared (r4 F5).
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
                    StringTable.Current.T("guide.toolbar.title"),
                    StringTable.Current.T("guide.toolbar.success"), chromeClose: true);
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

- [ ] **Step 3: `GuideSurface` — per-builder choice lists + `TextsFor`/`EdgeWord` DEFINED HERE (r4 F1's missing pieces; r5 F5's string switch; r5 F3's chrome Close on the transition card)**

```csharp
    /// <summary>The flow's window surface. Per-BUILDER choice lists: the action row gets the
    /// step's buttons via SetChoices; the chrome Close gets its own host via SetChromeClose;
    /// the Theme chips get a 2×2 grid in StepContent via SetThemeGrid (r5 F4's geometry — six
    /// cards in one row overflow the card and their 70-DIP circles overlap). Every builder
    /// registers AND visualizes what it builds (r3 F5) and re-registers on a re-render (r5 F2).
    /// Keys are LITERALS (r2 F12). In-place presses (dwell ±, theme chips) re-render through
    /// RenderChoicesForCurrentStep (r4 F3).</summary>
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
            window.SetChoices(RowChoicesFor(step));             // the action row, registered AND visualized
            window.SetChromeClose(chromeClose, T("guide.chrome.close"));
            window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
            window.SetDwellKnob(step == FirstRunGuideStep.DwellTime);
            window.SetThemeGrid(step == FirstRunGuideStep.Theme, ThemeChips());
            _lastStep = step;
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

        /// <summary>The docked edge's localized word. `DockedEdge` is a STRING ("Top"/"Bottom"/
        /// "Left"/"Right", ToolbarSettings.cs:41) and `Context` is the outer coordinator's
        /// protected property, read through `owner` (r5 F5).</summary>
        private string EdgeWord() => owner.Context!.Settings.Toolbar.DockedEdge switch
        {
            "Top" => T("guide.toolbar.edge.top"),
            "Bottom" => T("guide.toolbar.edge.bottom"),
            "Left" => T("guide.toolbar.edge.left"),
            _ => T("guide.toolbar.edge.right"),
        };

        /// <summary>The action row's buttons per step — the chrome Close is NOT in this list
        /// (it lives in its own host, r5 F4).</summary>
        private static (string Label, string Id)[] RowChoicesFor(FirstRunGuideStep step) => step switch
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
            FirstRunGuideStep.Theme => [(T("guide.theme.continue"), "guide_theme_continue")],
            FirstRunGuideStep.Safety => [(T("guide.safety.begin"), "guide_safety_begin")],
            FirstRunGuideStep.Done => [(T("guide.done.finish"), "guide_done_finish")],
            _ => [],
        };

        /// <summary>The Theme step's four chips, laid out as a 2×2 grid in StepContent
        /// (radius 40 DIP — the 70-DIP circles of a 2×2 grid at this card size would overlap).</summary>
        private static (string Label, string Id)[] ThemeChips() =>
        [
            (T("guide.theme.midnight"), "guide_theme_midnight"),
            (T("guide.theme.ember"), "guide_theme_ember"),
            (T("guide.theme.daylight"), "guide_theme_daylight"),
            (T("guide.theme.contrast"), "guide_theme_contrast"),
        ];

        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            window.ShowCardContent(T("guide.safety.title"), message, chromeClose: true);
            window.SetChoices(choices.Select(c => (c.Label, c.Id)).ToArray());
            window.SetChromeClose(true, T("guide.chrome.close"));   // chrome Close through step 7 (r3 F10)
            owner.RememberSafetyActions(choices);                 // id → OnChosen, for the dispatch
        }

        /// <summary>Re-renders the CURRENT step (r4 F3): an in-place press (dwell ±, theme chip)
        /// cleared every chrome target, and the press did not change the step.</summary>
        public void RenderChoicesForCurrentStep()
        {
            if (_lastStep is { } step) ShowStep(step);
        }

        public void HideCard() => window.HideGuide();
        public void ShowCard() => window.ShowGuide();
        public void Close() => window.Close();

        private static string T(string key) => StringTable.Current.T(key);
    }
```

(The `GuideSurface` reads the outer coordinator's protected `Context` through `owner.Context!` — a nested type may do that; `DockedEdge` is a `string`, so the switch is over string literals and the note about matching an enum is gone.)

- [ ] **Step 4: The dispatch + the safety actions map**

```csharp
    private readonly Dictionary<string, Action?> _safetyActions = new();

    /// <summary>The safety chain's id → OnChosen map, populated when a question card renders;
    /// the dispatch consults it first (r3 F5's one route).</summary>
    internal void RememberSafetyActions(IReadOnlyList<ModalChoice> choices)
    {
        _safetyActions.Clear();
        foreach (var choice in choices)
            _safetyActions[choice.Id] = choice.OnChosen;
    }

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
                    _guideVm?.ThemeChosenFire(char.ToUpperInvariant(theme[0]) + theme[1..]);
                }
                break;
        }
    }
```

- [ ] **Step 5: Calibration — per-"Calibrate-now" subscription + the refusal guard (r2 F10/F27)**

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

(`SafetySetupFlow.DispatcherDelay` is the existing one-shot dispatcher timer seam (:404-414) — reused instead of a new timer helper. The refusal re-show runs the FULL `ShowStep(Calibrate)` — the choice targets were cleared when "Calibrate now" fired, so a bare `ShowGuide()` would strand. `Restore`/`UnwindPartialRaise` unregister unconditionally with the same one-type-arg overload.)

- [ ] **Step 6: App.xaml.cs wiring**

  1. after `_eventCoordinator.Initialize(...)`: `_eventCoordinator.SetSplashHandle(_splashHandle);` — with the EventCoordinator forwarder following `SetSupervisionToggle` (`EventCoordinator.cs:124`):
  ```csharp
      /// <summary>Hands the splash handle to the settings-UI coordinator (the guide's latched
      /// show). The coordinator stores it (r1 F15).</summary>
      public void SetSplashHandle(SplashThreadHandle? handle) => _settingsUi?.SetSplashHandle(handle);
  ```
  2. `:704` becomes `canInterrupt: im.CurrentState == InteractionState.FreeLook && !_eventCoordinator.IsGuideOpen`.
  3. the banner hold + the status-card block REUSING the existing `bannerSlot` local (:518) and keeping the `!IsTestMode` guard — placed after the notice hosts' construction (r2 F13's CS0841 fix):
  ```csharp
            // The guide's banner hold (claim 4): live banner hides + re-queues; dequeue pauses.
            // Preempt still bypasses it (caregiver alerts — Task 11's pinned behaviour).
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
  (the existing `TheApp_StartsTheCards_…` pin test (:735-736) updates to cover both branches — r1 F14.)
  4. the notice hosts gain their optional args + `GuideOpenChanged` subscriptions at the existing construction sites: `RestartNoticeHost` (:519) `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `_eventCoordinator.GuideOpenChanged += _restartNoticeHost.OnGuideOpenChanged;`; `LagMarkHost` (:605) likewise; `SetupNoticeWatch` (:577) `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `_eventCoordinator.GuideOpenChanged += _setupNoticeWatch.OnGuideOpenChanged;`.
  5. `SafetySetupStartup.MaybeRequest`'s call site (:666) unchanged except the renamed message.

- [ ] **Step 7: Retire the machinery + swap the row** — delete `SafetySetupLaunch`/`SafetySetupHost` from `SafetySetupFlow.cs`; in `SettingsWindow.xaml.cs` remove the `_safetySetup` field + construction (:521-524), `StartSafetySetup` (:1227), `CreateSafetySetupFlow` (:1233); in `SettingsPageContext.cs` remove `RunSafetySetup` (:97) and the constructor argument (:667); in `GeneralPageSource.cs` replace the `g_safety` ButtonSpec with:

```csharp
                new ButtonSpec("g_guide",
                    StringTable.Current.T("settings.general.guide.label"),
                    () => { _ctx.Close(); _ctx.Messenger.Send(new FirstRunGuideRequestedMessage()); },
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
```

plus `"settings.general.guide.label"` ("Run the introduction guide" / DE "Einführung erneut starten", fit `B260x2` — replacing `settings.general.safety.label` in BOTH strings files; `--refresh-de-src` runs again HERE, r1 F19). **Retirement consumers updated in the same task (r1 F19):** `SafetySetupFlowTests.cs:78-170` (the `SafetySetupLaunch`/`SafetySetupHost` tests — DELETE; the flow-level tests stay) + the `RequestProbe : IRecipient<SafetySetupRequestedMessage>` recipient (:656 — retarget to `FirstRunGuideRequestedMessage`); `TestSettingsPageContext.cs:46` (`RunSafetySetup = ...` removed); `SimpleModeCurationTests.cs:103/:108` (`"Button:g_safety"` → `"Button:g_guide"`); `localization/settings-row-english.golden.json:1177` (regenerate the golden row).

- [ ] **Step 8: Wiring tests** — `SafetySetupWiringTests` (new seams: `FirstRunGuideRequestedMessage` registration, per-run factory present, NO `SafetySetupLaunch.Run`/`SafetySetupHost`/`RunSafetySetup` strings anywhere in `SettingsUiCoordinator.cs`/`SettingsWindow.xaml.cs`) + `GeneralPageSourceTests` (`g_guide` replaces the `g_safety` label/parameter).

- [ ] **Step 9: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → PASS; `node .gitnexus/run.cjs detect-changes --scope all` → not partial.

```bash
git -C .worktrees/first-run-guide add -A
git -C .worktrees/first-run-guide commit -m "feat(guide): per-run raise, unified choice targets with re-render, calibration guards, retire the safety-setup machinery"
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

## Self-review notes (rev 7 — the plan-review r5 + r6 fixes folded in)

1. **Self-contained, verified** (r4 F1, r5 F1) — every task's code is inline; the back-reference grep (`rev [0-9]'s … verbatim/test/code/Step`) matches only the changelog.
2. **The window can close (r4 F2)** — no `Close()` member on `FirstRunGuideWindow`; the unused `HideCard`/`ShowCard` are gone.
3. **In-place presses re-render (r4 F3, r5 F2)** — the wiring calls `_guideSurface?.RenderChoicesForCurrentStep()` after `SetDwellTime` (±) and after a theme pick; the press cleared every chrome target and these presses don't change the step; the knob's and chrome Close's already-built branches re-register from their own field references (`_knobCards`/`_knobBars`/`_chromeCloseCard`), which `SetChoices` never clears.
4. **One slot per builder (r4 F4)** — the knob builds into `KnobRow`; `SetPractice` into `StepContent`; the chrome Close into `ChromeCloseHost` (row 0, right of the title — away from the action row, r5 F4); the Theme chips into a 2×2 `SetThemeGrid` in `StepContent` (r5 F4 — six 170-wide cards in one row overflow the 496-DIP inner width and 70-DIP circles would overlap; chips register at radius 40 DIP via `RegisterChoice(id, radiusDip)`).
5. **Card geometry (r5 F4)** — choice cards are modal-button sized (170 × 100 DIP, spacing 16 → centres 186 ≥ 2×70 apart); the chip grid's vertical spacing is 40 (centres 130 ≥ 2×40 apart); a clipped card cannot fire unseen because `PushCentres` skips `!IsVisible` elements and the fixed grid leaves no overflow.
6. **EdgeWord (r5 F5)** — `DockedEdge` is a string (`ToolbarSettings.cs:41`); the switch is over `"Top"/"Bottom"/"Left"` literals via `owner.Context!` (a nested type reads the outer coordinator's protected `Context`); the enum note is deleted.
7. **Summon handler (r4 F5)** — swaps the body to the success text, no re-render (a toolbar event clears no targets).
8. **Theme labels in Task 4** — the four `guide.theme.*` keys are in the sorted block + the DE files (r4 S3; the duplicate DE rows introduced mid-edit are gone).
9. **Preview reach** — the builders resolve brushes through `Card.FindResource`, so the card-local previewed theme reaches the code-built cards (r4 S3).
10. **NFluent/census/commits** — `IsStrictlyGreaterThan`; the census entry in `DetectorSites`' tuple shape; Task 1's commit stages the new test file explicitly (no `-am`).
11. **Spec coverage** — §4 (Tasks 4, 12), §5 (Tasks 1, 8, 9, 10, 11, 12), §6 (Tasks 5, 6, 12), §7 steps 1–8 (Tasks 4–7, 12), §8 (Tasks 6, 12), §9 (Tasks 8–12), §12 (per-task tests + Task 13). No known gaps.
12. **Census tuple (r6 F1)** — `DetectorSites` is `(string Path, string Classification, string Reason)[]`; the guide's entry is the three-element tuple, not an indexer.
13. **The hold keeps the controller Hidden (r6 F2)** — `SetSuspended`'s state write is gated on `!_guideHiddenHold` alongside the `ForceShow` gate; the test's `StateName == "Hidden"` assertion now matches the code.
14. **Layout budget (r6 F3)** — the card is 560 × 620; the title wraps with a 186 DIP right margin so it stops at the Close card; DwellTime's practice circle and Theme's chip grid both fit.
15. **Progress visibility (r6 F4)** — chips and the chrome Close join `_choiceCards`/`_progressBars` (bars restored from `_themeBars`/`_chromeCloseBar` on a re-render), and `SetThemeGrid` re-attaches `_themeGrid` when `SetPractice(false)` emptied `StepContent`.
