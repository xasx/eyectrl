# First-Run Introduction Guide — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Rev 2** — external plan-review r1 processed (verdict FIX-FIRST: 19×S1, 9×S2; all verified against source and folded in, with the reviewer's Q1–Q5 protocols adopted verbatim). Rev 1's headline corrections: the flow owns the safety chain's `showModal` route; the Done card gets a real exit; the choice cards follow the Settings-modal latch protocol exactly (Register-at-origin + Hold + `UpdatePosition`→`Positioned`, radius `LogicalToPhysicalX(70)`, margin 35 DIP); `RaiseToTop` is a default interface method; the toolbar hold arms the hidden hold BEFORE suspending; the existing `ToolbarVisibilityChanged` is reused; claim 1 is actually applied; the calibration subscription is per-"Calibrate-now"; German rows are `{text, src}`; the retirement inventory is complete.

**Goal:** Build the first-run introductory guide exactly as specified in `docs/superpowers/specs/2026-11-16-first-run-guide-design.md` (rev 2.4, SHIP): an eight-step gaze-driven first-run experience that absorbs the safety setup and holds seven `GuideOpen` claims.

**Architecture:** A headless `FirstRunGuideFlow` state machine (the `SafetySetupFlow` pattern — callback-driven surface, headlessly testable) drives a per-run `FirstRunGuideWindow` through `IGuideSurface`. `SettingsUiCoordinator` builds the per-run trio behind the renamed `FirstRunGuideRequestedMessage`, acquires the guide's claims at the actual `Show()` inside the latched `SplashThreadHandle.WhenExited` continuation, and releases/restores them on close. The five safety questions run through the existing `SafetySetupFlow` via a new `StartQuestions()` entry; the flow itself routes the chain's `showModal` to the surface.

**Tech Stack:** .NET 10 WPF (net10.0-windows), CommunityToolkit.Mvvm, NFluent 3.1.0 + xUnit v3, Serilog.

## Global Constraints

- **Worktree:** every command runs inside `.worktrees/first-run-guide`. Test commands use ONE project per invocation (MSB1008 on multi-project args). Before committing code, `node .gitnexus/run.cjs detect-changes --scope all` (GitNexus rule); run `gitnexus impact` (upstream) before editing any symbol this plan modifies for the first time.
- **Assertions:** NFluent 3.1.0 (`Check.That(...)`), xUnit v3 (`xunit.v3`), no mocking library — private nested fakes only. NFluent trap: inclusive comparisons are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`.
- **No visible windows in App tests:** every STA test that shows the guide window calls `InvisibleTestWindow.Prepare(window)` first (`AllowsTransparency=true`, never write/animate `Opacity` after `Show`). `NoVisibleTestWindowGuardTests` scans sources. Window tests carry `[Category("UI")]` (they run in the non-launching UI subset).
- **Naming/style:** file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled, no `#region`.
- **Strings:** every `guide.*` key sets `where:"guide"`; `StringTableSchema.Surfaces` and `tools/strings_validate.py`'s `SURFACES` stay in exact parity (add `guide` to BOTH, same position); `strings.de.json` rows are `{ "text": ..., "src": ... }` ONLY (the validator rejects other fields); placeholders are positional `{0}`; alphabetical order enforced only on `strings.de.manifest.json` (`guide` between `"eye-care"` and `"language-recovery"`); refresh German `src` hashes with `python -I tools/strings_validate.py --refresh-de-src` whenever EN text changes.
- **Privacy:** guide log lines carry step ids and state transitions only — never localized text or user content. The two lines: `First-run guide: shown` and `First-run guide: closed (step=<id>, completed=<bool>)`.
- **Spec §10 ledger:** L1–L9 decisions are settled; do not reopen them in implementation.
- **Dwell geometry conventions (verified):** chrome choice targets register at the ORIGIN and are held (`Hold(id)`); centres arrive later via `UpdateTargetCenter` + `latch.Positioned(id)` (the `SettingsWindowViewModel.Register`/`UpdatePosition` pattern, `SettingsWindowViewModel.cs:186-215`); choice radius = `DpiHelper.LogicalToPhysicalX(SettingsWindowViewModel.ModalBtnRadius)` (70 DIP); latch release margin = `DpiHelper.LogicalToPhysicalX(LatchReleaseMarginDip)` (35 DIP); chrome dwell time = `DwellSettings.ToolbarButtonDwellMs` (`SettingsWindow.xaml.cs:510`); the practice target is the one `ActivationButtonDwellMs` consumer.

---

### Task 1: `GuideOpen` seams — suppressor member, modal flag + event wiring, offers, coordinator exposure

**Files:**
- Modify: `src/EyeCtrl.Core/FixationSuppressor.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` (subscribe + RecomputeSuspension)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs` (new)

**Interfaces:**
- Produces: `FixationSuppressor.Guide`; `ModalInteractionState.GuideOpen` + `GuideOpenChanged`; `ActivationOfferCoordinator` recomputes on the flip; `EventCoordinator.IsGuideOpen` + `GuideOpenChanged` forwarders. Later tasks gate on these.

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

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"`
Expected: FAIL — `ModalInteractionState` has no `GuideOpen`/`GuideOpenChanged`.

- [ ] **Step 3: Implement**

`src/EyeCtrl.Core/FixationSuppressor.cs` — add a member (each owner owns its claim; never share):

```csharp
    /// <summary>The first-run guide, while its fullscreen card is up.</summary>
    Guide,
```

`src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs` — after the `ProbeOpen` region (same volatile/write-on-UI/read-on-gaze contract, same per-subscriber try/catch raise as `ProbeOpenChanged`):

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

`src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` — **the flip must drive a recompute** (review-r1 F1: the coordinator only recomputes on change events; it subscribes to `SettingsOpenChanged`/`ProbeOpenChanged` at :112-113 and unsubscribes at :366-367). Add the subscription beside :113, the unsubscription beside :367, and the handler:

```csharp
        _modal.GuideOpenChanged += OnGuideOpenChanged;   // beside the Settings/Probe subscriptions
```

```csharp
        _modal.GuideOpenChanged -= OnGuideOpenChanged;   // beside the matching unsubscriptions
```

```csharp
    private void OnGuideOpenChanged(bool _) => RecomputeSuspension();
```

and in `RecomputeSuspension`, directly after the `_modal.ProbeOpen` check:

```csharp
        if (_modal.GuideOpen)
            reasons.Add("guide");
```

`src/EyeCtrl.App/Services/EventCoordinator.cs` — beside the existing `IsSettingsOpen`/`SettingsOpenChanged` forwarders:

```csharp
    /// <summary>Whether the first-run guide owns the screen (forwards
    /// <see cref="ModalInteractionState.GuideOpen"/>).</summary>
    internal bool IsGuideOpen => _modal.GuideOpen;

    /// <summary>Raised after <see cref="IsGuideOpen"/> flips. Forwards
    /// <see cref="ModalInteractionState.GuideOpenChanged"/>; subscribers read
    /// <see cref="IsGuideOpen"/>, not the argument.</summary>
    internal event Action<bool>? GuideOpenChanged
    {
        add => _modal.GuideOpenChanged += value;
        remove => _modal.GuideOpenChanged -= value;
    }
```

- [ ] **Step 4: Run to verify pass + no regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` → PASS; then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActivationOffer"` → PASS.

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.Core/FixationSuppressor.cs src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): GuideOpen modal flag + FixationSuppressor.Guide + offers subscription and reason"
```

---

### Task 2: `SafetySetupFlow.StartQuestions()` + flight-kind guard

**Files:**
- Modify: `src/EyeCtrl.App/Services/SupervisionToggleService.cs`
- Modify: `src/EyeCtrl.App/Services/SafetySetupFlow.cs` (StartQuestions + RequestCrashRecovery)
- Test: `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs` (extend)

**Interfaces:**
- Produces: `SafetySetupFlow.StartQuestions()` — chain from question 0, no intro card; `SupervisionToggleService.IsEnablingInFlight`. Task 5's flow calls `StartQuestions()` and reports `onEnded`.

**Test-harness facts (verified):** `_cards` is `List<(string Message, IReadOnlyList<ModalChoice> Choices)>`; the harness exposes `Top` and `Pick(id)` (`Top.Choices.Single(c => c.Id == id).OnChosen!.Invoke()`); `ModalChoice` is `(string Id, string Label, ModalChoiceKind Kind, Action? OnChosen)` — the action property is **`OnChosen`**, not `Action`. Existing helpers: `WorkingSupervision` (synchronous), `WorkingAsyncSupervision` (queues into `_background`; `isSupervisorConnected: () => true`), `FailingAsyncSupervision`.

- [ ] **Step 1: Write the failing tests** (append to `SafetySetupFlowTests`)

```csharp
    [Fact]
    public void StartQuestions_AskesQuestion0_WithoutTheIntroCard()
    {
        var flow = Flow();
        flow.StartQuestions();

        Check.That(_cards).HasSize(1);
        // Question 0 is the crash-recovery question — its Yes choice is on screen, the intro
        // card's Start/Later pair is not.
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
        // Regression pin (r1 S3): this PASSES on current code too — RequestEnable no-ops on
        // Enabled — but pins that the explicit guard did not change the outcome.
        _settings.Supervision.Enabled = true;
        var (toggle, persisted) = WorkingSupervision(_settings);
        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        Pick("safety_yes");

        Check.That(_cards.Count).IsEqualTo(2);   // question 1 is up: no waiting card
        Check.That(persisted).IsEmpty();
    }

    /// <summary>A switch whose ENSURE actually takes flight: supervision ON (the ensure's gate
    /// needs it) and a supervisor that is NOT connected (its other gate — r1 F2).</summary>
    private SupervisionToggleService EnsuringInFlightSupervision(EyeCtrlSettings settings) =>
        new(settings,
            new SupervisorBringUp(settings, () => { }, () => true, _ => { }, () => { }, () => true),
            new SupervisorStartupEnsure(settings, () => { }, () => true, () => true),
            persistEnabled: _ => { }, supervisorInstalled: () => true, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: _background.Add, postToUi: w => w());

    [Fact]
    public void CrashRecovery_Yes_WithAnUnrelatedFlightInFlight_DoesNotWait()
    {
        // An unrelated ENSURE in flight (kind=Ensuring): the chain must not attach to it.
        _settings.Supervision.Enabled = true;
        var toggle = EnsuringInFlightSupervision(_settings);
        toggle.RequestEnsure();
        Check.That(toggle.InFlight).IsTrue();

        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        Pick("safety_yes");

        Check.That(_cards.Count).IsEqualTo(2);   // question 1 is up: not attached to the flight
    }

    [Fact]
    public void StartQuestions_CanBeAbandoned_ByWindowClose()
    {
        var flow = Flow();
        flow.StartQuestions();
        Pick("safety_yes");                       // Q0 answered, applied
        flow.Abandon();                           // window closed mid-chain

        Check.That(_ended).IsEmpty();             // abandon reports nothing
        Check.That(_settings.General.SafetySetupDone).IsFalse();
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SafetySetupFlowTests"`
Expected: FAIL — no `StartQuestions`; the unrelated-flight test cannot attach correctly on current code.

- [ ] **Step 3: Implement**

`SupervisionToggleService.cs` — beside `InFlight` (:88):

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

and after `Start()` add:

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

- [ ] **Step 4: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SafetySetupFlowTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/SupervisionToggleService.cs src/EyeCtrl.App/Services/SafetySetupFlow.cs tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): SafetySetupFlow.StartQuestions + supervision flight-kind guard"
```

---

### Task 3: `SplashThreadHandle.WhenExited` — the latched main-thread continuation

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs` (SplashThreadHandle)
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs` (new, STA)

**Interfaces:**
- Produces: `SplashThreadHandle.WhenExited(Action onMainThread)` — latched; fires on the REGISTERING thread's dispatcher when already exited, disposed, or the splash dispatcher has shut down (r1 F20: a faulted splash thread never raises ExitFinished — the shutdown latch covers it); attaches otherwise; runs once per registration. The null-splash case is the caller's (Task 12).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

public class SplashThreadHandleWhenExitedTests
{
    // A minimal ISplashWindow fake: only ExitFinished matters here.
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
    [Category("UI")]
    public void WhenExited_Attaches_BeforeExit_AndFiresOnce_OnTheRegisteringDispatcher()
    {
        var window = new FakeSplashWindow();
        var handle = new SplashThreadHandle(Dispatcher.CurrentDispatcher, window);
        var fired = new List<string>();

        handle.WhenExited(() => fired.Add("a"));          // attached: waits
        Check.That(fired).IsEmpty();

        window.RaiseExit();                                // ExitFinished fires (here: the test thread)
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain

        Check.That(fired).HasSize(1);
        handle.WhenExited(() => fired.Add("late"));        // already exited: fires at once
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain
        Check.That(fired).HasSize(2);
        Check.That(fired[0]).IsEqualTo("a");
    }

    [StaFact]
    [Category("UI")]
    public void WhenExited_AfterTheSplashDispatcherShutDown_FiresImmediately()
    {
        // A splash thread that faulted after the handle was produced never raises ExitFinished —
        // but its dispatcher shuts down (Dispose). The registration must not wait forever (r1 F20).
        var window = new FakeSplashWindow();
        var splashDispatcher = Dispatcher.CurrentDispatcher;   // stand-in for the splash dispatcher
        var handle = new SplashThreadHandle(splashDispatcher, window);
        var fired = new List<string>();

        splashDispatcher.Invoke(() => splashDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
        handle.WhenExited(() => fired.Add("x"));           // shutdown started: fires at once

        Check.That(fired).HasSize(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandleWhenExitedTests"`
Expected: FAIL — no `WhenExited`.

- [ ] **Step 3: Implement** (in `SplashThreadHandle`, after `BeginExit()`; constructor gains the event wiring)

```csharp
    // The exit latch: ExitFinished fires on the SPLASH thread; continuations must run on the
    // dispatcher of the thread that REGISTERED (the main thread in production). Latched under a
    // lock so a registration racing the exit cannot be dropped. A splash thread that faults
    // after the handle was produced never raises ExitFinished — its dispatcher's shutdown is
    // the fallback latch (r1 F20).
    private readonly object _exitGate = new();
    private readonly List<(Dispatcher Dispatcher, Action Action)> _exitContinuations = new();
    private bool _exited;

    /// <summary>
    /// Runs <paramref name="onMainThread"/> once, on the registering thread's dispatcher, after
    /// the splash's <see cref="ISplashWindow.ExitFinished"/> — or immediately when the exit
    /// already happened, the splash dispatcher shut down, or the handle is disposed. The
    /// splash-is-null case never reaches here: the caller runs its continuation directly.
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
        window.ExitFinished += OnWindowExitFinished;   // fires on the splash thread
    }
```

- [ ] **Step 4: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandle"` → PASS.

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): SplashThreadHandle.WhenExited — latched, shutdown-aware continuation"
```

---

### Task 4: The `guide` string surface — schema, validator, keys, German

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs` (Surfaces)
- Modify: `tools/strings_validate.py` (SURFACES)
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (guide.* keys)
- Modify: `src/EyeCtrl.App/Localization/strings.de.json` (+ `strings.de.manifest.json`)
- Test: run the parity gates (existing)

**Interfaces:**
- Produces: the `guide.*` keys listed below — consumed by the flow/window from Task 5/7/12. English shape: `{"text", "who", "where", "fit", "args"}` (matching `settings.general.safety.label`); **German rows are `{"text", "src"}` ONLY** — the validator rejects any other field (`strings_validate.py:500-505`); placeholders are POSITIONAL (`{0}`).

- [ ] **Step 1: Add the surface to both lists (exact parity — same position in each)**

`StringTableSchema.cs` Surfaces — insert after `"eye-care",`:

```csharp
        "guide",
```

`tools/strings_validate.py` SURFACES — insert after `"eye-care",`:

```python
    "guide",
```

- [ ] **Step 2: Add the English keys** to `strings.json` (fit classes: titles `modal460` — `modal170` is a ≤162 DIP one-line BUTTON fit and cannot hold a 24 px title (r1 F25); choice buttons `modal170`; body prose `prose`):

```json
  "guide.welcome.title": { "text": "Welcome to EyeCtrl", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.welcome.body": { "text": "EyeCtrl lets you control this PC with your eyes. This short guide shows you the basics — about 2 minutes. Every practice task can be skipped.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.welcome.start": { "text": "Start", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.later": { "text": "Later", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwell.title": { "text": "Dwell — your click", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwell.body": { "text": "Rest your gaze on a target and hold still: the ring fills, and on completion the target activates. Try it on the circle below.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwell.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.dwelltime.title": { "text": "Dwell time", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.dwelltime.body": { "text": "This slider sets how long a desktop click takes — the cursor dwell that clicks. Try values on the practice target; the guide's own buttons keep the standard UI dwell.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.dwelltime.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.title": { "text": "Calibration", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.calibrate.body": { "text": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.calibrate.now": { "text": "Calibrate now", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.calibrate.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.title": { "text": "Your main menu", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.toolbar.body": { "text": "Look at the {0} edge of the screen to summon the toolbar — the keyboard, settings and everything else open from there. Try it now.", "who": "user", "where": "guide", "fit": "prose", "args": ["0"] },
  "guide.toolbar.edge.top": { "text": "top", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.bottom": { "text": "bottom", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.left": { "text": "left", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.edge.right": { "text": "right", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.success": { "text": "Summoned! The toolbar hides again on its own. Continue when ready.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.toolbar.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.toolbar.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.theme.title": { "text": "Pick a look", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.theme.body": { "text": "The theme takes effect after a restart — the card here previews your choice.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.theme.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.safety.title": { "text": "5 quick questions to keep you safe", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.safety.body": { "text": "These keep crash recovery, startup and the audible cue set up for you.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.done.title": { "text": "You're set.", "who": "user", "where": "guide", "fit": "modal460", "args": [] },
  "guide.done.body": { "text": "The docked edge summons the toolbar — your main menu. The gaze keyboard opens from it, and every setting lives in Settings.", "who": "user", "where": "guide", "fit": "prose", "args": [] },
  "guide.done.finish": { "text": "Finish", "who": "user", "where": "guide", "fit": "modal170", "args": [] },
  "guide.chrome.close": { "text": "Close guide", "who": "user", "where": "guide", "fit": "modal170", "args": [] }
```

- [ ] **Step 3: Add the German rows** to `src/EyeCtrl.App/Localization/strings.de.json` — `{"text": ..., "src": ...}` per key, nothing else (r1 F18):

```json
  "guide.welcome.title": { "text": "Willkommen bei EyeCtrl", "src": "Welcome to EyeCtrl" },
  "guide.later": { "text": "Später", "src": "Later" },
  "guide.chrome.close": { "text": "Anleitung schließen", "src": "Close guide" }
```

(translate every key from Step 2 the same way — `src` is the exact English `text`.)

- [ ] **Step 4: Refresh the German manifest** — add `"guide"` between `"eye-care"` and `"language-recovery"` in `src/EyeCtrl.App/Localization/strings.de.manifest.json`, then:

Run: `python -I tools/strings_validate.py --refresh-de-src`
Expected: manifest `src` hashes updated, no errors.

- [ ] **Step 5: Run the parity gates**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~StringsEnumParity|FullyQualifiedName~SingleSurfaceKey"` and `python -I tools/strings_validate.py`
Expected: PASS / no findings.

- [ ] **Step 6: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.Core/Localization/StringTableSchema.cs src/EyeCtrl.Core/Localization/strings.json tools/strings_validate.py src/EyeCtrl.App/Localization/
git -C .worktrees/first-run-guide commit -m "feat(guide): guide string surface — schema, validator, EN+DE keys, manifest"
```

---

### Task 5: `FirstRunGuideFlow` + `FirstRunGuideHost` — the headless state machine

**Files:**
- Create: `src/EyeCtrl.App/Services/FirstRunGuideFlow.cs`
- Create: `src/EyeCtrl.App/Services/FirstRunGuideHost.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs` (new)

**Interfaces:**
- Consumes: `SafetySetupFlow.StartQuestions()` (Task 2); `ModalChoice` (`EyeCtrl.ViewModels`, `public sealed record`, headless-safe — Q4 confirmed).
- Produces: `FirstRunGuideStep`; `GuideExitReason { Finished, Abandoned }`; `IGuideSurface` (`ShowStep(FirstRunGuideStep)`, `ShowSafetyCard(string, IReadOnlyList<ModalChoice>)`, `HideCard()`, `ShowCard()`, `Close()`); `FirstRunGuideFlow` — **the flow owns the chain's `showModal` route**: its `createSafetyFlow` factory is `Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow>` and the flow passes `(message, choices) => _surface.ShowSafetyCard(message, choices)` itself (ONE route — r1 F3/F16); `Start/Advance/Skip/Finish/Close/NotifyTaskSucceeded/NotifyCalibrationEnded/NotifySafetyEnded/ForwardSafetyChoice/WindowClosed/IsOnStep/LastStepId` + `StepEntered/ToolbarTaskEntered/ToolbarTaskExited/Closed` events; `FirstRunGuideHost.TryBegin/End`.

- [ ] **Step 1: Write the failing tests**

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
        for (var i = 0; i < 4; i++)
            s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        s.LastSafetyChoices!.Single(c => c.Id == "safety_basic").OnChosen!.Invoke();
        Check.That(settings.General.SafetySetupDone).IsTrue();
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Done);
        flow.Advance();                                   // Finish — the Done card's exit (r1 F4)
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
        flow.Advance();                                   // Welcome → MeetDwell
        flow.NotifyTaskSucceeded();                       // → DwellTime
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
        flow.Skip();                                      // MeetDwell skipped
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Skip();                                      // INVALID on DwellTime (Continue-only)
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // → Calibrate
        flow.Skip();                                      // Calibrate skipped → ToolbarTask
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
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Safety);

        // The chain's showModal landed on the surface (ONE route — the flow passed it itself).
        Check.That(s.SafetyMessages).Not.IsEmpty();
        Check.That(s.LastSafetyChoices).IsNotNull();

        s.LastSafetyChoices!.Single(c => c.Id == "safety_yes").OnChosen!.Invoke();
        Check.That(s.SafetyMessages.Count).IsEqualTo(2);  // question 1 arrived on the surface
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

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideFlowTests"`
Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `FirstRunGuideFlow.cs`**

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
/// The per-run wiring (Task 12) subscribes the lifecycle events to acquire/release the guide's
/// seven claims and drive the window.
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
    /// the toolbar hold here (§5 claim 6: held on non-task steps, lifted on the task).</summary>
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
    /// Idempotent — the window's Closed event re-enters after surface.Close(). Abandon keeps
    /// applied answers; the flag stays false unless the safety block already completed.</summary>
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
            // The flow owns the chain's showModal route: the surface receives every card, so
            // tests and production share one path (r1 F3/F16).
            _safetyFlow = _createSafetyFlow(
                (message, choices) => _surface.ShowSafetyCard(message, choices),
                completed => NotifySafetyEnded(completed));
            _safetyFlow.StartQuestions();
        }

        _surface.ShowStep(step);
    }
}
```

- [ ] **Step 4: Implement `FirstRunGuideHost.cs`**

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

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideFlowTests"` → PASS.

- [ ] **Step 6: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/FirstRunGuideFlow.cs src/EyeCtrl.App/Services/FirstRunGuideHost.cs tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): headless FirstRunGuideFlow + host — steps, skip policy, Finish exit, safety absorption"
```

---

### Task 6: `FirstRunGuideViewModel` — binding surface, gaze contract, latch protocol

**Files:**
- Create: `src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` (expected map) + `DwellSurfaceCensusTests` (census row)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs` (new)

**Interfaces:**
- Consumes: `DwellOverlayViewModelBase` (chrome detector), `LeaveBeforeArmLatch`, the Settings-modal registration protocol (`Register` at origin + `Hold`, `UpdatePosition` → `UpdateTargetCenter` + `Positioned` — `SettingsWindowViewModel.cs:186-215`).
- Produces: `FirstRunGuideViewModel` with `IsVisible` (the wiring sets it true at Show — `Receive` gates on it), **`DwellActiveWhilePaused => true` override (r1 F5)**, `ShowCard`, `ShowSafetyCard`/`SafetyChoices`, `RegisterChoice(id, onActivated)`/`UpdateChoiceCentre(id, centre)`/`ClearChoices()` (the latch protocol, Q1), `SetPracticeCentre(PhysicalPoint)` + live re-timing in `SetDwellTime` (r1 F8), `SetPracticeVisible/SetSliderVisible/SetThemeChipsVisible`, `DwellTimeMs`/`SetDwellTime`, `ChoiceActivated`/`CloseRequested`/`PracticeTargetActivated`/`ThemeChosen` events, `ChoiceMayDwell(id)` test probe, `IsVisible` as an observable the wiring sets.

- [ ] **Step 1: The telemetry gate entries (they fail first — do them with the test run)**

`tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs` — the `expected` map gains:

```csharp
            [nameof(FirstRunGuideViewModel)] = DwellContext.Other,
```

`DwellSurfaceCensusTests` — add the guide's census row (the same row shape the other detector-owning surfaces use; the census fails until the VM is classified, r1 F6).

- [ ] **Step 2: Write the failing tests**

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
            settings: new EyeCtrlSettings(), sink: null, dwellRegistry: null);

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

        // paused sample, not in gap: accumulates (DwellActiveWhilePaused = true)
        long t = 0;
        for (var i = 0; i < 30; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t) { IsPaused = true });
            t += 30_000;
        }
        Check.That(activated).Not.IsEmpty();
    }

    [Fact]
    public void Latch_HoldsNewChoices_UntilGazeLeaves_AndCentrePushArms()
    {
        var vm = Vm();
        vm.ShowCard("t", "b", chromeClose: true);
        var fired = new List<string>();
        vm.RegisterChoice("c_yes", () => fired.Add("yes"));       // at origin, held (Settings pattern)
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();         // held: a resting gaze cannot fire it

        vm.UpdateChoiceCentre("c_yes", new PhysicalPoint(400, 300));  // real centre pushed → Positioned
        Check.That(vm.ChoiceMayDwell("c_yes")).IsFalse();         // gaze never left it yet

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(900, 900), 0));   // gaze elsewhere
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(400, 300), 1_000)); // then onto it
        Check.That(vm.ChoiceMayDwell("c_yes")).IsTrue();          // armed

        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(400, 300), 1_030_000)); // dwell elapses
        Check.That(fired).HasSize(1);                              // the choice's action ran
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

        vm.SetDwellTime(200);                                     // re-adds the target at 200 ms
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
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests"` and `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring"`
Expected: FAIL — no `FirstRunGuideViewModel`; the telemetry map lacks it.

- [ ] **Step 4: Implement**

```csharp
namespace EyeCtrl.ViewModels;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Binding surface for the first-run guide's current step (spec §5/§6). The chrome detector
/// (base class) dwells at ToolbarButtonDwellMs and carries the choice cards + chrome Close via
/// the Settings-modal latch protocol: targets register AT THE ORIGIN and are HELD
/// (<c>Hold(id)</c>, SettingsWindowViewModel.Register :186-195); the view pushes each real
/// centre through <see cref="UpdateChoiceCentre"/> (→ <c>UpdateTargetCenter</c> +
/// <c>latch.Positioned</c>, :208-212); the latch's filter rides every
/// <c>DwellDetector.Update</c> (:166); targets clear BEFORE a choice's action runs (:317-318).
/// The practice target carries its OWN detector at ActivationButtonDwellMs (two-knob split) with
/// the forgiving-dwell grace, re-timed live by <see cref="SetDwellTime"/>. Gaze routes through
/// SampleMayDwell (gates !IsInGap, NOT IsPaused) — DwellActiveWhilePaused is true, so the guide
/// stays usable under user-pause (§9).
/// </summary>
public sealed partial class FirstRunGuideViewModel : DwellOverlayViewModelBase
{
    /// <summary>The choice cards' radius, in physical px (Settings' ModalBtnRadius 70 DIP).</summary>
    internal static readonly double ChoiceRadiusPhysical = DpiHelper.LogicalToPhysicalX(ModBtnRadiusDip);

    private const double ModBtnRadiusDip = SettingsWindowViewModel.ModalBtnRadius;

    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _practiceDetector;
    private readonly LeaveBeforeArmLatch _latch;
    private readonly double _releaseMarginPhysical;
    private readonly Dictionary<string, Action> _chromeActions = new();
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
    /// <summary>The chrome Close (or Later) fired.</summary>
    public event Action? CloseRequested;
    /// <summary>A chrome target fired (a choice id, or guide_close).</summary>
    public event Action<string>? ChoiceActivated;
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
    /// a target registered under a resting gaze must not arm — r1 F7). The view pushes the real
    /// centre through <see cref="UpdateChoiceCentre"/>; a fired target clears targets before the
    /// action runs (the Settings precedent, :317-318).</summary>
    public void RegisterChoice(string id, Action onActivated)
    {
        _chromeActions[id] = onActivated;
        DwellDetector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0),
            ChoiceRadiusPhysical, _settings.Dwell.ToolbarButtonDwellMs));
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

    /// <summary>Clears every chrome target, action and latch state (the window/wiring calls this
    /// before re-registering a new card's set — guide_close included).</summary>
    public void ClearChoices()
    {
        _chromeActions.Clear();
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
        if (targetId == "guide_close") { CloseRequested?.Invoke(); return; }
        if (_chromeActions.TryGetValue(targetId, out var action))
        {
            DwellDetector.ClearTargets();       // Settings precedent: clear before invoking (:317-318)
            _latch.Clear();
            action();
            return;
        }
        ChoiceActivated?.Invoke(targetId);
    }

    protected override void OnTargetProgress(string targetId, double progress) { /* bound in XAML */ }
}
```

(If `DwellDetector`'s API names differ — `RemoveTarget`/`UpdateTargetCenter`/`ClearTargets` — the executor greps the detector's public surface once and matches; single discovery, not a design change. `DpiHelper` lives where `SettingsWindowViewModel` already imports it from.)

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests"` → PASS; `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring|FullyQualifiedName~DwellSurfaceCensus"` → PASS.

- [ ] **Step 6: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideViewModel — Settings-modal latch protocol, dual dwell knobs, telemetry rows"
```

---

### Task 7: `FirstRunGuideWindow` — render-only window, fullscreen geometry, overlay inventory

**Files:**
- Create: `src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml` + `.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row — `exempt:` classification)
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`RaiseToTop()` as a DEFAULT INTERFACE METHOD on `IToolbarOverlay` + `ICursorCircleOverlay`)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` (new, STA)

**Interfaces:**
- Consumes: `FirstRunGuideViewModel` (Task 6).
- Produces: `FirstRunGuideWindow : OverlayWindow` — fullscreen via explicit `Left/Top/Width/Height` (r1 F26), `IsClickThrough="True"` in XAML ONLY (it is `{ get; init; }` — XAML sets init properties; do not also assign in the ctor), `AllowsTransparency`, no post-show Opacity writes; render-only setters; `HideGuide()`/`ShowGuide()` via the window's own Show/Hide path — never raw `Visibility` writes (r1 F26); NO `Close()` override (r1 F10 — `public void Close() => Close();` is a stack overflow).

- [ ] **Step 1: The window XAML** (`FirstRunGuideWindow.xaml`):

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
                <ItemsControl x:Name="ChoiceRow" Grid.Row="3" Orientation="Horizontal"
                              HorizontalAlignment="Right"/>
            </Grid>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Code-behind** (`FirstRunGuideWindow.xaml.cs`) — fullscreen geometry read live in the constructor (display quirk memory: 3840×2400 physical / 1920×1200 DIP — never hard-code):

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window (spec §5): fullscreen click-through backdrop with a centered
/// interactive card. AllowsTransparency + no post-Show Opacity writes (InvisibleTestWindow.Prepare
/// compatible, r1 F10). RENDER-ONLY: the step→content mapping lives in the wiring's GuideSurface
/// (Task 12), which is the IGuideSurface implementation. The chrome Close is a registered chrome
/// target (guide_close) — not a Close() override (r1 F10). The choice cards' DWELL is the VM's
/// chrome detector (Task 6's protocol); the window renders and the wiring pushes centres.
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;

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

    /// <summary>Card content: title/body + whether the chrome Close card (guide_close) renders
    /// this step. The wiring passes the localized strings.</summary>
    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowCard(title, body, chromeClose);

    /// <summary>Rebuilds the choice row's visuals; the wiring reads the laid-out centres back
    /// and pushes them into the VM (layout + every DPI/display change — Q1).</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id)> choices)
    {
        ChoiceRow.ItemsSource = choices;
        _vm.ClearChoices();
    }

    public void SetPractice(bool visible) => _vm.SetPracticeVisible(visible);
    public void SetSlider(bool visible) => _vm.SetSliderVisible(visible);
    public void SetThemeChips(bool visible) => _vm.SetThemeChipsVisible(visible);

    /// <summary>Step 4 hides the WHOLE window (the calibration window takes the screen) and the
    /// advance re-shows it — through the window's own Show/Hide path, never raw Visibility
    /// writes (r1 F26).</summary>
    public void HideGuide() => Hide();
    public void ShowGuide() => Show();
}
```

- [ ] **Step 3: Overlay inventory row** — `docs/design/overlay-unification-spec.md` §3.1 gains (after the Splash row, same column shape; `exempt:` — a `collision-zone` row asserts a `GazeConsumerInventory` source exists and none is built for the guide, r1 F26):

```
| Guide | `FirstRunGuideWindow` | card on transparent | XAML | exempt:first-run card, transient, own GuideOpen claims |
```

`OverlayInventoryDriftTests` needs no map entry for an `exempt:` row (only `collision-zone` rows are map-backed).

- [ ] **Step 4: `IOverlayWindows.cs`** — `RaiseToTop()` as a DEFAULT INTERFACE METHOD on `ICursorCircleOverlay` and `IToolbarOverlay` (r1 F9 — ~16 test fakes implement these interfaces; a default body keeps them compiling):

```csharp
    /// <summary>Raises this window above any later-created fullscreen topmost overlay (the
    /// guide). Default no-op so the test fakes need no member; the real OverlayWindow's
    /// implementation (:377) overrides it.</summary>
    void RaiseToTop() { }
```

- [ ] **Step 5: STA tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

[Category("UI")]
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

- [ ] **Step 6: Run to verify pass + repo gates**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideWindowTests|FullyQualifiedName~OverlayInventoryDrift"` → PASS; `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Toolbar"` → PASS (the default-interface `RaiseToTop` keeps every fake compiling).

- [ ] **Step 7: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml* docs/design/overlay-unification-spec.md tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs src/EyeCtrl.App/Services/IOverlayWindows.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): render-only FirstRunGuideWindow + exempt inventory row + RaiseToTop default members"
```

---

### Task 8: `ToolbarAutoHideController` — guide-hidden hold + `ResetToHidden`

**Files:**
- Modify: `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` (extend — harness: `NewController(...)` at :755, `Attach`, `FakeVisibility` with `ShowCalls/HideCalls/ForceShowCalls/SetActiveCalls`)

**Interfaces:**
- Produces: `ToolbarAutoHideController.SetGuideHiddenHold(bool)` — while true, `ForceShow` and `ReassertVisibility`'s `SetActive(true)` re-arm are suppressed and the controller stays Hidden; `ResetToHidden()` — state → Hidden, anchors cleared, hidden via `_visibility` (mirror `ResetToVisible()` at :551). **Order rule (r1 F11): the hold must be armed BEFORE any `SetSuspended(true)`** — `SetSuspended` posts an async `ForceShow` when Hidden, and a hold armed after it loses the race.

- [ ] **Step 1: Write the failing tests** (the harness attaches via `sut.Attach(fake)`; sample loops copy the file's existing drive helpers, e.g. `GazeBackOnToolbar_ResetsHideCountdown` at :83)

```csharp
    [Fact]
    public void GuideHiddenHold_SuppressesTheAsyncForceShow_FromSuspend()
    {
        var sut = NewController();
        var fake = new FakeVisibility();
        sut.Attach(fake);

        DriveToHidden(sut);                       // the file's 5 s off-toolbar sample loop, extracted
        Check.That(sut.StateName).IsEqualTo("Hidden");

        sut.SetGuideHiddenHold(true);             // the hold FIRST (r1 F11's order)...
        sut.SetSuspended(true);                   // ...then the suspend (Task 9's SetGuideHold order)
        RunBackgroundIfNeeded();                  // the async ForceShow would land here
        Check.That(fake.ForceShowCalls).IsEqualTo(0);   // suppressed (r3 F2a)
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
        // A past-edge summon now detects immediately — the harness's summon sample loop records
        // ShowToolbar without any 5 s hide-delay wait (r4 F2).
    }
```

(`DriveToHidden`/`RunBackgroundIfNeeded`: extract the existing off-toolbar sample loop into a helper if the file has none — the loop body is `sut.Feed(CreateSample(x, y, valid: true), timestampUs)` advanced past `_hideDelayUs`, the same shape :83's test uses.)

- [ ] **Step 2: Implement**

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

- [ ] **Step 3: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideController"` → PASS.

- [ ] **Step 4: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/ToolbarAutoHideController.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): ToolbarAutoHideController guide-hidden hold + ResetToHidden"
```

---

### Task 9: Toolbar wiring — lifecycle holder (ordered), feed terms, `OnTargetActivated` gate

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- Test: extend `ToolbarLifecycleCoordinatorTests` (incl. the `ProbeGateWiring_IsPinnedAtTheSource` source-text pins at :361-376)

**Interfaces:**
- Consumes: Task 8's `SetGuideHiddenHold`/`ResetToHidden`.
- Produces: `ToolbarLifecycleCoordinator.SetGuideHold(bool held)` — **`_autoHide?.SetGuideHiddenHold(held)` FIRST, then `SetAutoHideSuspended("guide", held)`** (r1 F11's order); `ResetToolbarForGuideTask()` — hidden-hold off + suspend off + `ResetToHidden()`; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` gain `guideOpen` (all existing call sites + the :361/:366/:376 source pins updated in this task); `ToolbarViewModel.GuideOpenGate` (assigned in Task 12). **`ToolbarVisibilityChanged` already exists on the lifecycle (:52) and is already forwarded (:366) — reuse it, do not redeclare (r1 F12).**

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

- [ ] **Step 2: Implement**

`ToolbarLifecycleCoordinator.cs` — the static feeds gain the parameter and term:

```csharp
    internal static bool ShouldFeedKeyboardSample(bool paused, bool modalKeyboardVisible, bool keyboardIsVisible,
        bool isInGap, bool probeOpen, bool exerciseActive, bool guideOpen) =>
        !paused && modalKeyboardVisible && keyboardIsVisible && !isInGap && !probeOpen && !exerciseActive && !guideOpen;

    internal static bool ShouldFeedSettingsSample(bool exerciseActive, bool guideOpen) =>
        !exerciseActive && !guideOpen;
```

(update EVERY call site — 11 in the coordinator + the source-text pins in `ProbeGateWiring_IsPinnedAtTheSource`; the gaze-thread call sites pass `_modal.GuideOpen`), plus two methods:

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

`ToolbarViewModel.cs` — the gate property + the gate INSIDE `OnTargetActivated` AFTER the progress/highlight reset lines (:537-541) so a gated press still cleans the button's visual state and the cue does not play for a suppressed activation (r1 F30):

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

- [ ] **Step 3: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarLifecycleCoordinator"` and `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~Toolbar"` → PASS.

- [ ] **Step 4: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs src/EyeCtrl.ViewModels/ToolbarViewModel.cs tests/EyeCtrl.App.Tests/Services/Coordinators/ToolbarLifecycleCoordinatorTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): ordered toolbar hold + GuideOpen feed terms + OnTargetActivated gate"
```

---

### Task 10: Ring lifecycle — the coordinators' gate + restore

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs`
- Test: extend the coordinators' existing test files

**Interfaces:**
- Consumes: Task 1's `ModalInteractionState.GuideOpen`.
- Produces: `OverlayVmBindingCoordinator.RingMayReshow` (`!_modal.GuideOpen`) — **`OverlayMessageCoordinator` has NO `_modal`** (its ctor takes `(toolbar, vmBinding)`, `EventCoordinator.cs:212`), so the message coordinator consults the binding coordinator's flag. The FULL ring lifecycle (r1 F13): guide open → ring hidden; calibration's Collecting hide stays; the gated re-show returns the ring when allowed; on guide close the wiring restores when `CursorVM.IsVisible && !SettingsOpen && !ProbeOpen` (Task 12).

- [ ] **Step 1: Failing tests** (the existing coordinator test files' harnesses)

```csharp
    [Fact]
    public void RingMayReshow_IsFalse_WhileGuideOpen()
    {
        modal.GuideOpen = true;
        Check.That(vmBinding.RingMayReshow).IsFalse();
        modal.GuideOpen = false;
        Check.That(vmBinding.RingMayReshow).IsTrue();
    }

    [Fact]
    public void CalibrationIdle_Reshow_IsSuppressed_WhileGuideOpen()
    {
        modal.GuideOpen = true;
        coordinator.OnCalibrationStateChanged(new CalibrationStateChangedMessage(CalibrationState.Idle, 100));
        Check.That(recordingCursor.ShowCount).IsEqualTo(0);   // the gated path
    }
```

- [ ] **Step 2: Implement**

`OverlayVmBindingCoordinator.cs`:

```csharp
    /// <summary>Whether the ring may re-show right now — false while the first-run guide owns
    /// the screen (the guide hides the ring and re-shows it itself: step 5 only, spec §5).</summary>
    internal bool RingMayReshow => !_modal.GuideOpen;
```

and its `IsVisible` case gains the gate:

```csharp
            case nameof(CursorCircleViewModel.IsVisible):
                if (Context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen && !_modal.GuideOpen)
                    cursorCircle.Show();
                else
                    cursorCircle.Hide();
                break;
```

`OverlayMessageCoordinator.cs` — both calibration `cursorCircle.Show()` sites (:308/:318) consult the binding coordinator:

```csharp
                        if (Context.CursorVM.IsVisible && _vmBinding.RingMayReshow) cursorCircle.Show();
```

(the field name is the ctor-injected binding coordinator — match the actual field.)

- [ ] **Step 3: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayVmBinding|FullyQualifiedName~OverlayMessage"` → PASS.

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs tests/
git -C .worktrees/first-run-guide commit -m "feat(guide): ring re-show gated on GuideOpen via the binding coordinator's flag"
```

---

### Task 11: Proactive surfaces — BannerSlot hold, hosts, SetupNoticeWatch

**Files:**
- Modify: `src/EyeCtrl.App/Services/BannerSlot.cs`
- Modify: `src/EyeCtrl.App/Services/RestartNoticeHost.cs`
- Modify: `src/EyeCtrl.App/Services/LagMarkHost.cs`
- Modify: `src/EyeCtrl.App/Services/SetupNoticeWatch.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs` (new) + host test extensions

**Interfaces:**
- Produces: `BannerSlot.Hold()/Release()` (hide + re-queue live banner at the FRONT, pause dequeue; **`Preempt` bypasses the hold by design — caregiver alerts are the safety channel and take the slot even during the guide; pinned by test**, r1 F24); `RestartNoticeHost`/`LagMarkHost` gain OPTIONAL `Func<bool>? isGuideOpen = null` params (keeps the existing tests compiling, r1 F24) + `OnGuideOpenChanged`; `SetupNoticeWatch` optional gate + expiry-skip while the guide is open (the 90 s window must not run out behind the guide — r1 F24) + `OnGuideOpenChanged`.

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

- [ ] **Step 2: Implement `BannerSlot.Hold/Release`**

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

- [ ] **Step 3: The hosts + the watch**

`RestartNoticeHost` — ctor gains an OPTIONAL `Func<bool>? isGuideOpen = null` (wired `() => _eventCoordinator.IsGuideOpen` at App.xaml.cs:519's construction); the three show paths' guards gain `|| (_isGuideOpen?.Invoke() ?? false)`, with the blocked call stashed for replay:

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

`LagMarkHost` — optional `Func<bool>? isGuideOpen = null` (App.xaml.cs:605), `CanShow()` gains `&& (_isGuideOpen?.Invoke() ?? false) == false`, and:

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

`SetupNoticeWatch` — optional `Func<bool>? isGuideOpen = null` (App.xaml.cs:577); ONE gated helper replaces the three raw `CheckStartup()` call sites (Start :91, Poll :110, Poke :136 — each becomes `if (CheckStartupWhenGuideClosed())`, keeping its surrounding schedule/flow), and **the expiry check also skips while the guide is open** (the 90 s window must not run out behind the guide — r1 F24; `Finish()` would make the close-time poke a no-op):

```csharp
    /// <summary>r4 F3 + r1 F24: CheckStartup writes setup-notice-shown.json BEFORE _show (no burn
    /// behind the guide), and the expiry window must not run out while the guide holds the
    /// screen — both skip while GuideOpen; the close-time poke shows then.</summary>
    private bool GuideOpen => _isGuideOpen?.Invoke() ?? false;

    private bool CheckStartupWhenGuideClosed() => !GuideOpen && _service.CheckStartup();
```

(the expiry comparison gains `&& !GuideOpen`), plus:

```csharp
    /// <summary>Guide opened or closed: closing pokes once (r4 F3).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (!open) Poke();
    }
```

- [ ] **Step 4: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~BannerSlot|FullyQualifiedName~RestartNotice|FullyQualifiedName~SetupNotice|FullyQualifiedName~LagMark"` → PASS (existing tests compile — the new params are optional).

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/BannerSlot.cs src/EyeCtrl.App/Services/RestartNoticeHost.cs src/EyeCtrl.App/Services/LagMarkHost.cs src/EyeCtrl.App/Services/SetupNoticeWatch.cs tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): BannerSlot hold (preempt exempted) + defer hosts + gated SetupNoticeWatch"
```

---

### Task 12: Startup wiring — the per-run raise, claims lifecycle, gaze input, and the retired machinery

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (rename `SafetySetupRequestedMessage` → `FirstRunGuideRequestedMessage`)
- Modify: `src/EyeCtrl.App/Services/SafetySetupFlow.cs` (`SafetySetupStartup.MaybeRequest` sends the new message; `SafetySetupLaunch`/`SafetySetupHost` deleted)
- Modify: `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` (per-run factory replaces `ShowSafetySetup`)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (`SetSplashHandle` forwarder)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (splash forwarding, canInterrupt, banner hold, status-card hold, notice hosts' args, raise ordering)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (+ `SettingsPageContext.cs`, `GeneralPageSource.cs`) — retire the window-side host and swap the row
- Modify: `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs` (:78-170's `SafetySetupLaunch`/`SafetySetupHost` tests + the `RequestProbe` recipient at :656), `tests/EyeCtrl.App.Tests/Settings/TestSettingsPageContext.cs` (:46), `tests/EyeCtrl.App.Tests/Settings/SimpleModeCurationTests.cs` (:103/:108), `localization/settings-row-english.golden.json` (:1177)
- Test: extend `SafetySetupWiringTests` + `GeneralPageSourceTests`

**Interfaces:**
- Consumes: Tasks 1–11.
- Produces: the complete first-run raise — message renamed, per-run trio built in the coordinator, claims acquired at `Show()` inside `WhenExited`, gaze input wired, step-5 toggles, restore on close, `g_guide` row.

- [ ] **Step 1: Rename the message + raise seam**

`InteractionMessages.cs`:

```csharp
/// <summary>Raise the first-run guide (which absorbs the safety setup). Sent once at startup while
/// <c>General.SafetySetupDone</c> is false, and from the Settings → General re-run row.</summary>
public record FirstRunGuideRequestedMessage;
```

(delete `SafetySetupRequestedMessage`; `SafetySetupStartup.MaybeRequest` sends the new message — its doc comment updated.)

- [ ] **Step 2: The per-run factory in `SettingsUiCoordinator`** — replaces `ShowSafetySetup()` entirely:

```csharp
    private FirstRunGuideHost? _guideHost;
    private SplashThreadHandle? _splashHandle;
    private FirstRunGuideWindow? _guideWindow;
    private FirstRunGuideViewModel? _guideVm;
    private FirstRunGuideFlow? _guideFlow;

    /// <summary>Hands the splash handle in for the latched show. Called once from App.xaml.cs
    /// via EventCoordinator's forwarder (r1 F15) after Initialize.</summary>
    public void SetSplashHandle(SplashThreadHandle? handle) => _splashHandle = handle;
```

Registration + factory:

```csharp
        // First-run guide: raised at startup (the renamed gate) and from the Settings → General row.
        messenger.Register<SettingsUiCoordinator, FirstRunGuideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(StartFirstRunGuide));
```

```csharp
    private void StartFirstRunGuide()
    {
        var context = Context!;
        if (_guideHost is not null) return;                        // one-flow rule

        var host = new FirstRunGuideHost();
        if (!host.TryBegin(out var lease)) return;
        _guideHost = host;

        try
        {
            BuildAndRaiseGuide(context, lease);
        }
        catch
        {
            // r1 F14's fallback: a raise failure must not wedge the status-card hold or the
            // one-flow gate. Dispose the lease (releases the gate) and run the status-cards
            // fallback (Step 5 wires it), then surface the failure.
            lease.Dispose();
            _guideHost = null;
            StatusCardsFallback?.Invoke();
            throw;
        }
    }

    /// <summary>App assigns this at startup (Step 5): a raise failure starts the status-card
    /// presenter through the same one-shot the normal close uses (r3 F6's fallback release).</summary>
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

        _guideFlow = new FirstRunGuideFlow(settings, trackerPresent: tracker,
            toolbarTaskAvailable: toolbarTask,
            surface: new GuideSurface(_guideWindow, _guideVm, this),
            createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, _supervisionToggle,
                showModal, onEnded));

        // Gaze input lives or dies with IsVisible (r1 F16): Receive gates on it.
        _guideVm.IsVisible = true;

        // The chrome wiring (r1 F16): choices, chrome close, practice, theme, dwell-time.
        _guideVm.ChoiceActivated += id => context.Dispatcher.BeginInvoke(() => OnGuideChoice(id));
        _guideVm.CloseRequested += () => context.Dispatcher.BeginInvoke(_guideFlow.Close);
        _guideVm.PracticeTargetActivated += () => context.Dispatcher.BeginInvoke(_guideFlow.NotifyTaskSucceeded);
        _guideVm.ThemeChosen += name => context.Dispatcher.BeginInvoke(() =>
        {
            settings.Theme.Name = name;
            OnSettingsChanged(settings);                     // the Settings rows' live-apply path
            context.SettingsService.SaveDebounced(settings); // the DEBOUNCED save (r1 F22) — never sync Save
        });
        _guideVm.DwellTimeChanged += _ => context.SettingsService.SaveDebounced(settings);
        context.ToolbarVM.GuideOpenGate = () => _modal.GuideOpen;   // claim 7 wired (r1 F12)

        // Claims + window, at the ACTUAL Show() inside the latched continuation (r3 F6).
        void Show()
        {
            context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Guide);   // claim 1 (r1 F15)
            _modal.GuideOpen = true;                       // claim 2 (+ every _modal reader)
            _quickActions.SetSummonSuspended("guide", true);   // claim 5
            _toolbar.SetGuideHold(true);                   // claim 6 — hold arms BEFORE suspend (r1 F11)
            Log.Information("First-run guide: shown");
            _guideWindow!.ShowGuide();
            _guideVm.ArmLatchForCards();                   // every chrome card held until gaze leaves
            _guideFlow!.Start();
        }
        if (_splashHandle is { } splash) splash.WhenExited(Show);
        else Show();

        // Step toggles (claim 6(d)) — with the PauseIndicator raise in the chain (spec §5 z-order).
        _guideFlow.StepEntered += step => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen) return;
            if (step == FirstRunGuideStep.ToolbarTask)
            {
                _toolbar.ResetToolbarForGuideTask();       // hold released + controller Hidden
                context.OverlayManager.Toolbar.RaiseToTop();          // Guide < Toolbar
                context.OverlayManager.PauseIndicator.RaiseToTop();   // < PauseIndicator
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.RaiseToTop(); // < ring
            }
            else
            {
                _toolbar.SetGuideHold(true);               // non-task steps hold again
            }
        });

        // The summon observation: the LIFECYCLE's existing ToolbarVisibilityChanged (:52/:366) —
        // gated on the toolbar task, switching the card to the success text (spec §7.5), and
        // UNSUBSCRIBED in Restore (handlers must not pile up across re-runs, r1 F21).
        void OnToolbarVisibility(bool visible)
        {
            if (_guideFlow is null || !_modal.GuideOpen || !visible) return;
            context.Dispatcher.BeginInvoke(() =>
            {
                if (_guideFlow is null || !_guideFlow.IsOnStep(FirstRunGuideStep.ToolbarTask)) return;
                context.OverlayManager.Toolbar.RaiseToTop();
                context.OverlayManager.PauseIndicator.RaiseToTop();
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.RaiseToTop();
                _guideWindow?.ShowCardContent(
                    StringTable.Current.T("guide.toolbar.success"),
                    _guideVm?.Body ?? string.Empty, chromeClose: true);  // success text; Continue waits
            });
        }
        _toolbar.ToolbarVisibilityChanged += OnToolbarVisibility;

        void Restore(GuideExitReason reason)
        {
            _toolbar.ToolbarVisibilityChanged -= OnToolbarVisibility;   // no handler pile-up
            context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);  // claim 1
            context.Messenger.Unregister<CalibrationStateChangedMessage>(this);  // r1 F17: always
            _modal.GuideOpen = false;
            _quickActions.SetSummonSuspended("guide", false);
            _toolbar.SetGuideHold(false);
            _toolbar.ForceShow();                          // ForceShow + ResetToVisible — every mode (r4 F5)
            if (context.CursorVM.IsVisible && !context.IsSettingsOpen && !context.IsProbeOpen)
                context.OverlayManager.CursorCircle.Show();           // ring restore (spec §5)
            Log.Information("First-run guide: closed (step={Step}, completed={Completed})",
                _guideFlow?.LastStepId ?? "done", reason == GuideExitReason.Finished);
            (_guideVm as IDisposable)?.Dispose();          // messenger/registry teardown (r1 F23)
            _guideWindow?.Close();
            _guideWindow = null;
            _guideVm = null;
            _guideFlow = null;
            _guideHost = null;
            lease.Dispose();
        }
        _guideFlow.Closed += reason => context.Dispatcher.BeginInvoke(() => Restore(reason));
        _guideWindow.Closed += (_, _) => _guideFlow?.WindowClosed();   // external close = abandon
    }

    /// <summary>The choice ids the window's chrome targets fire, mapped to flow calls.</summary>
    private void OnGuideChoice(string id)
    {
        if (_guideFlow is null) return;
        switch (id)
        {
            case "guide_start": _guideFlow.Advance(); break;            // Welcome: Start
            case "guide_later": _guideFlow.Close(); break;              // Welcome: Later
            case "guide_dwell_skip": _guideFlow.Skip(); break;          // MeetDwell: Skip
            case "guide_dwelltime_continue": _guideFlow.Advance(); break;
            case "guide_calibrate_now": StartCalibration(); break;
            case "guide_calibrate_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_continue": _guideFlow.Advance(); break;
            case "guide_theme_continue": _guideFlow.Advance(); break;
            case "guide_done_finish": _guideFlow.Finish(); break;       // r1 F4's exit
            case "guide_close": _guideFlow.Close(); break;              // chrome Close
        }
    }

    internal void ForwardSafety(ModalChoice choice) => _guideFlow?.ForwardSafetyChoice(choice.OnChosen!);
```

(If `EventCoordinationContext` names its toolbar-VM or probe members differently, match the context's actual members — the coordinator reads `_modal` directly for the gate. `FirstRunGuideViewModel` gains `ArmLatchForCards()` = the latch HoldAll over the just-registered card set.)

- [ ] **Step 3: `GuideSurface` — the step→content mapping (the IGuideSurface implementation)** — a nested private class in `SettingsUiCoordinator`:

```csharp
    /// <summary>The flow's window surface: maps steps to localized content + choice rows, and
    /// carries the theme-chip registration. The safety chain's cards land here from the FLOW
    /// (one route) and re-register their choices through the Task 6 latch protocol.</summary>
    private sealed class GuideSurface(
        FirstRunGuideWindow window, FirstRunGuideViewModel vm,
        SettingsUiCoordinator owner) : IGuideSurface
    {
        public void ShowStep(FirstRunGuideStep step)
        {
            var id = step.ToString().ToLowerInvariant();       // guide.welcome.* / guide.done.* ...
            var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
            window.ShowCardContent(
                StringTable.Current.T($"guide.{id}.title"),
                step == FirstRunGuideStep.ToolbarTask
                    ? string.Format(StringTable.Current.T("guide.toolbar.body"),
                        StringTable.Current.T($"guide.toolbar.edge.{owner.CurrentDockedEdge()}"))
                    : StringTable.Current.T($"guide.{id}.body"),
                chromeClose);
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
                        () => vm.ThemeChosenFire(theme));
        }

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
            FirstRunGuideStep.Theme => [],                     // the chips are chrome targets, not choices
            FirstRunGuideStep.Done => [(T("guide.done.finish"), "guide_done_finish")],
            _ => [],
        };

        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            window.ShowCardContent(StringTable.Current.T("guide.safety.title"), message, chromeClose: true);
            vm.ClearChoices();
            foreach (var choice in choices)
                vm.RegisterChoice(choice.Id, () => owner.ForwardSafety(choice));
        }

        public void HideCard() => window.HideGuide();
        public void ShowCard() => window.ShowGuide();
        public void Close() => window.Close();

        private static string T(string key) => StringTable.Current.T(key);
    }
```

(The chip names must match `ThemeRegistry`'s canonical names — verify the four against `ThemeRegistry` and adjust. `CurrentDockedEdge()` is a small coordinator accessor over `Context.Settings.Toolbar.DockedEdge` returning the lower-case edge word. The practice target's centre push and the choice centres' layout reads: the window's `Card` gets `Loaded`/`SizeChanged` handlers (~15 lines of view code) that compute each registered target's on-screen centre in physical px and call `vm.SetPracticeCentre(...)` / `vm.UpdateChoiceCentre(...)` — repeated on DPI change per Q1.)

- [ ] **Step 4: Calibration — per-"Calibrate-now" subscription (r1 F17) + the stranding guard (r1 F27)**

```csharp
    private IDisposable? _calibrationCeiling;

    private void StartCalibration()
    {
        var context = Context!;
        if (_guideVm is null || _guideFlow is null) return;

        // Hide only once collection actually starts (r1 F27): a refused start (the VM not Idle)
        // must not leave the user under a hidden guide. Guard: hide on Collecting/Training; if
        // no Collecting arrives within 2 s, unregister and re-show.
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
                context.Messenger.Unregister<SettingsUiCoordinator, CalibrationStateChangedMessage>(this);
                _calibrationCeiling?.Dispose();
                context.Dispatcher.BeginInvoke(() =>
                {
                    _guideWindow?.ShowGuide();
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
            context.Messenger.Unregister<SettingsUiCoordinator, CalibrationStateChangedMessage>(this);
            context.Dispatcher.BeginInvoke(() => _guideWindow?.ShowGuide());   // re-show: not stranded
        }

        _guideVm.HidePracticeTarget();
        context.Messenger.Send(new CalibrationRequestedMessage());
    }
```

(`SafetySetupFlow.DispatcherDelay` is the existing one-shot dispatcher timer seam (:404-414) — reused instead of a new timer helper. The VM gains `internal void HidePracticeTarget() { _practiceDetector.RemoveTarget("guide_practice"); }`. `Restore` ALSO unregisters the calibration message unconditionally (r1 F17 — the Later-on-Welcome path never registers it, but every other exit must not leave it; the messenger's unregister is a no-op when absent).)

- [ ] **Step 5: App.xaml.cs wiring**

  1. after `_eventCoordinator.Initialize(...)`: `_eventCoordinator.SetSplashHandle(_splashHandle);` — with the forwarder on EventCoordinator following `SetSupervisionToggle` (`EventCoordinator.cs:124`):
  ```csharp
      /// <summary>Hands the splash handle to the settings-UI coordinator (the guide's latched
      /// show). The coordinator stores it (r1 F15).</summary>
      public void SetSplashHandle(SplashThreadHandle? handle) => _settingsUi?.SetSplashHandle(handle);
  ```
  2. `:704` becomes `canInterrupt: im.CurrentState == InteractionState.FreeLook && !_eventCoordinator.IsGuideOpen`.
  3. the banner hold/release + the status-card hold, REUSING the existing `bannerSlot` local (:518) and keeping the `!IsTestMode` guard (:423) — placed at the existing `StatusCardPresenter` start site (:424):
  ```csharp
            // The guide's banner hold (claim 4): live banner hides + re-queues; dequeue pauses.
            // Preempt still bypasses it (caregiver alerts — Task 11's pinned behaviour).
            _eventCoordinator.GuideOpenChanged += open =>
            {
                if (open) bannerSlot.Hold(); else bannerSlot.Release();
            };

            bool startupGuidePending = SafetySetupStartup.ShouldPrompt(
                settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode);
            if (startupGuidePending && !IsTestMode)
            {
                // The NoTracker/Paused card must not spend its state behind the guide (r2 F1):
                // the presenter starts on guide close. Fallback release (r3 F6): the
                // coordinator's StatusCardsFallback runs if the raise fails (r1 F14).
                void OnGuideClosedForStatusCards(bool open)
                {
                    if (open) return;
                    _eventCoordinator.GuideOpenChanged -= OnGuideClosedForStatusCards;
                    _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
                }
                _eventCoordinator.GuideOpenChanged += OnGuideClosedForStatusCards;
                _settingsUi.StatusCardsFallback = () => OnGuideClosedForStatusCards(false);
            }
            else
            {
                _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
            }
  ```
  (`_settingsUi` is the coordinator field App already holds for `SetSupervisionToggle`-style calls; the existing `TheApp_StartsTheCards_…` pin test (:735-736) updates to cover both branches — r1 F14.)
  4. the notice hosts gain their optional args + subscriptions at the existing construction sites: `RestartNoticeHost` (:519) `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `_eventCoordinator.GuideOpenChanged += _restartNoticeHost.OnGuideOpenChanged;`; `LagMarkHost` (:605) likewise; `SetupNoticeWatch` (:577) `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `_eventCoordinator.GuideOpenChanged += _setupNoticeWatch.OnGuideOpenChanged;`.
  5. `SafetySetupStartup.MaybeRequest`'s call site (:666) unchanged except the renamed message.

- [ ] **Step 6: Retire the old machinery + swap the row** — delete `SafetySetupLaunch`/`SafetySetupHost` from `SafetySetupFlow.cs`; in `SettingsWindow.xaml.cs` remove the `_safetySetup` field + construction (:521-524), `StartSafetySetup` (:1227), `CreateSafetySetupFlow` (:1233); in `SettingsPageContext.cs` remove `RunSafetySetup` (:97) and the constructor argument (:667); in `GeneralPageSource.cs` replace the `g_safety` ButtonSpec with:

```csharp
                new ButtonSpec("g_guide",
                    StringTable.Current.T("settings.general.guide.label"),
                    () => { _ctx.Close(); _ctx.Messenger.Send(new FirstRunGuideRequestedMessage()); },
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
```

plus `"settings.general.guide.label"` ("Run the introduction guide" / DE "Einführung erneut starten", fit `B260x2` — replacing `settings.general.safety.label` in BOTH strings files; `--refresh-de-src` runs again HERE, r1 F19). **Retirement consumers updated in the same task (r1 F19):** `SafetySetupFlowTests.cs:78-170` (the `SafetySetupLaunch`/`SafetySetupHost` tests — DELETE; the flow-level tests stay) + `RequestProbe : IRecipient<SafetySetupRequestedMessage>` (:656 — retarget to `FirstRunGuideRequestedMessage`); `TestSettingsPageContext.cs:46` (`RunSafetySetup = ...` removed); `SimpleModeCurationTests.cs:103/:108` (`"Button:g_safety"` → `"Button:g_guide"`); `localization/settings-row-english.golden.json:1177` (regenerate the golden row).

- [ ] **Step 7: Update the wiring tests** — `SafetySetupWiringTests` (new seams: `FirstRunGuideRequestedMessage` registration, per-run factory present, NO `SafetySetupLaunch.Run`/`SafetySetupHost`/`RunSafetySetup` strings anywhere in `SettingsUiCoordinator.cs`/`SettingsWindow.xaml.cs`) + `GeneralPageSourceTests` (`g_guide` replaces the `g_safety` label/parameter).

- [ ] **Step 8: Run everything + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → PASS; `node .gitnexus/run.cjs detect-changes --scope all` → not partial.

```bash
git -C .worktrees/first-run-guide add -A
git -C .worktrees/first-run-guide commit -m "feat(guide): per-run raise, claims lifecycle, gaze input, retire the safety-setup machinery, g_guide row"
```

---

### Task 13: Final gates

- [ ] **Step 1:** `dotnet build EyeCtrl.slnx` → 0 errors.
- [ ] **Step 2:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → 0 failed (under the repo's `wait-idle-tests.ps1` gate).
- [ ] **Step 3:** `dotnet test tests/EyeCtrl.ViewModels.Tests` → 0 failed; `dotnet test tests/EyeCtrl.Core.Tests` → 0 failed.
- [ ] **Step 4:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"` (non-launching STA subset) → 0 failed.
- [ ] **Step 5:** `python -I tools/strings_validate.py` → clean; `python -I tools/kb_log_metrics_test.py` → clean.
- [ ] **Step 6:** `node .gitnexus/run.cjs detect-changes --scope all` → not partial/truncated; report the risk line.

---

## Self-review notes (rev 2 — the plan-review r1 fixes folded in)

1. **One showModal route** — the flow's factory takes `(showModal, onEnded)` and passes `showModal` to the surface itself (r1 F3/F16); tests record via `FakeSurface.LastSafetyChoices`; production and tests share the path.
2. **Done exits** — `Advance()` on Done → `Finish()` → `Closed(Finished)` (r1 F4); the log line carries `step=` + `completed=`.
3. **Latch protocol (Q1 adopted)** — Register-at-origin + `Hold(id)`; centres pushed after holding via `UpdateChoiceCentre` (→ `UpdateTargetCenter` + `Positioned`); radius `LogicalToPhysicalX(ModalBtnRadius 70)`, margin `LatchReleaseMarginDip` (35 DIP); targets cleared before a choice's action runs; centres re-pushed on DPI/display change.
4. **Hold ordering** — `SetGuideHiddenHold` arms before `SetAutoHideSuspended` (r1 F11); Task 8's tests use the real `NewController`/`Attach`/`FakeVisibility` harness.
5. **Default interface methods** — `RaiseToTop()` bodies default-empty so the ~16 test fakes compile (r1 F9).
6. **Existing members reused, not redeclared** — `ToolbarLifecycleCoordinator.ToolbarVisibilityChanged` (:52/:366), `ForceShow()` (:246), `SafetySetupFlow.DispatcherDelay` (:404).
7. **Claim 1 applied** — `Acquire/ReleaseFixationSuppression(FixationSuppressor.Guide)` in Show/Restore (r1 F15); `EventCoordinator.SetSplashHandle` forwarder added.
8. **Gaze input wired** — `IsVisible = true` at Show; all four VM events subscribed; the full step→content mapping lives in `GuideSurface` (r1 F16); `SafetyChoices` renders through the surface's re-registration.
9. **Calibration subscription** — registered in the "Calibrate-now" handler, unconditionally unregistered in Restore; refusal guard re-shows after 2 s (r1 F17/F27).
10. **Telemetry + census** — `expected` map entry + census row + `DwellActivationMessage` on practice fires (r1 F6).
11. **German shape + positional args + fit classes** — `{text, src}` rows; `{0}` placeholders; localized edge keys; titles `modal460`, choices `modal170` (r1 F18/F25).
12. **Retirement inventory complete** — flow tests :78-170, `RequestProbe`, `TestSettingsPageContext`, `SimpleModeCurationTests`, golden.json (r1 F19).
13. **Summon handler** — gated on the ToolbarTask step, switches to the success text and waits for Continue, unsubscribed in Restore (r1 F21).
14. **Save path** — `OnSettingsChanged` + `SaveDebounced` (r1 F22); practice detector carries the forgiving-dwell grace; VM disposed in Restore (r1 F23).
15. **Status-card block** — reuses `bannerSlot`, keeps `!IsTestMode`, fallback via `StatusCardsFallback` + pin update (r1 F14).
16. **Spec coverage** — §4 (Tasks 4, 12), §5 seven claims (Tasks 1, 8, 9, 10, 11, 12), §6 (Tasks 5, 6, 12), §7 steps 1–8 (Tasks 4, 5, 6, 7, 12), §8 (Tasks 6, 12), §9 (Tasks 8–12 + tests), §12 test strategy (per-task tests + Task 13 gates), §11 file list (every row in a task). No known gaps.
