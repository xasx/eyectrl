# First-Run Introduction Guide — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first-run introductory guide exactly as specified in `docs/superpowers/specs/2026-11-16-first-run-guide-design.md` (rev 2.4, SHIP): an eight-step gaze-driven first-run experience that absorbs the safety setup and holds seven `GuideOpen` claims.

**Architecture:** A headless `FirstRunGuideFlow` state machine (the `SafetySetupFlow` pattern — callback-driven surface, headlessly testable) drives a per-run `FirstRunGuideWindow` through `IGuideSurface`. `SettingsUiCoordinator` builds the per-run trio behind the renamed `FirstRunGuideRequestedMessage`, acquires the guide's claims at the actual `Show()` inside the latched `SplashThreadHandle.WhenExited` continuation, and releases/restores them on close. The five safety questions run through the existing `SafetySetupFlow` via a new `StartQuestions()` entry.

**Tech Stack:** .NET 10 WPF (net10.0-windows), CommunityToolkit.Mvvm, NFluent 3.1.0 + xUnit v3, Serilog.

## Global Constraints

- **Worktree:** every command runs inside `.worktrees/first-run-guide` (`git -C .worktrees/first-run-guide …` or `cd` once per shell). Test commands use ONE project per invocation (MSB1008 on multi-project args).
- **Assertions:** NFluent 3.1.0 (`Check.That(...)`), xUnit v3 (`xunit.v3`), no mocking library — private nested fakes only. NFluent trap: `IsCloseTo` is absolute-delta; inclusive comparisons are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`.
- **No visible windows in App tests:** every STA test that shows the guide window calls `InvisibleTestWindow.Prepare(window)` first (the window is `AllowsTransparency=true` and must never write/animate `Opacity` after `Show`). `NoVisibleTestWindowGuardTests` scans sources and fails otherwise.
- **Naming/style:** file-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled, no `#region`.
- **Strings:** every `guide.*` key sets `where:"guide"`; `StringTableSchema.Surfaces` and `tools/strings_validate.py`'s `SURFACES` must stay in exact parity (add `guide` to BOTH, same position); alphabetical order is enforced only on `strings.de.manifest.json` (`guide` inserts between `"eye-care"` and `"language-recovery"`).
- **Privacy:** guide log lines carry step ids and state transitions only — never localized text or user content (KB-E1 content-free contract).
- **Spec §10 ledger:** L1–L9 decisions are settled; do not reopen them in implementation.
- **Log line:** the guide writes exactly one Information line on open and one on close (`First-run guide: shown` / `First-run guide: closed (step=<id>, completed=<bool>)` — ids only).

---

### Task 1: `GuideOpen` seams — suppressor member, modal flag, offers, coordinator exposure

**Files:**
- Modify: `src/EyeCtrl.Core/FixationSuppressor.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` (RecomputeSuspension)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs` (new)

**Interfaces:**
- Produces: `FixationSuppressor.Guide`; `ModalInteractionState.GuideOpen` (bool property, UI-thread write, gaze-thread read) + `GuideOpenChanged` event; `EventCoordinator.IsGuideOpen` + `EventCoordinator.GuideOpenChanged` (forwards the modal event). Later tasks gate on these.

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

`src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs` — add after the `ProbeOpen` region (same volatile/write-on-UI/read-on-gaze contract):

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
    /// try/catch like <see cref="ProbeOpenChanged"/>: one coordinator's recompute throwing must
    /// not stop the remaining gates from observing the flip. Subscribers read
    /// <see cref="GuideOpen"/>, not the argument (re-entrancy).</summary>
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

`src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs` — in `RecomputeSuspension`, directly after the `_modal.ProbeOpen` check:

```csharp
        if (_modal.GuideOpen)
            reasons.Add("guide");
```

`src/EyeCtrl.App/Services/EventCoordinator.cs` — beside the existing `IsSettingsOpen`/`SettingsOpenChanged` forwarders (it has both):

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

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModalInteractionStateTests"` → PASS; then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ActivationOffer"` → PASS (existing suspension pins unaffected — "guide" is a new reason only while GuideOpen).

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.Core/FixationSuppressor.cs src/EyeCtrl.App/Services/Coordinators/ModalInteractionState.cs src/EyeCtrl.App/Services/Coordinators/ActivationOfferCoordinator.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/ModalInteractionStateTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): GuideOpen modal flag + FixationSuppressor.Guide + offers suspension reason"
```

---

### Task 2: `SafetySetupFlow.StartQuestions()` + flight-kind guard

**Files:**
- Modify: `src/EyeCtrl.App/Services/SupervisionToggleService.cs`
- Modify: `src/EyeCtrl.App/Services/SafetySetupFlow.cs` (StartQuestions + RequestCrashRecovery)
- Test: `tests/EyeCtrl.App.Tests/Services/SafetySetupFlowTests.cs` (extend)

**Interfaces:**
- Produces: `SafetySetupFlow.StartQuestions()` — chain from question 0, no intro card; `SupervisionToggleService.IsEnablingInFlight`. The guide's flow (Task 5) calls `StartQuestions()` and reports `onEnded`.

- [ ] **Step 1: Write the failing tests** (extend `SafetySetupFlowTests` — reuse its `Flow(...)`/`WorkingSupervision` helpers, `SafetySetupFlowTests.cs:599-647`; `_cards`/`_ended`/`_saves` capture lists already exist)

```csharp
    [Fact]
    public void StartQuestions_AskesQuestion0_WithoutTheIntroCard()
    {
        var flow = Flow();
        flow.StartQuestions();

        Check.That(_cards).HasSize(1);
        // The intro card's message is the settings.safety.intro.message key; question 0 is the
        // crash-recovery question — distinguishable by the Yes/No choice ids.
        Check.That(_cards[0].choices[0].Id).IsEqualTo("safety_yes");
    }

    [Fact]
    public void StartQuestions_Completes_AndSetsTheFlag()
    {
        var flow = Flow();
        flow.StartQuestions();
        for (var i = 0; i < SafetySetupFlow.QuestionCount; i++)
        {
            _cards[^1].choices.First(c => c.Id == "safety_yes").Action();
            if (i == SafetySetupFlow.QuestionCount - 2)
                _cards[^1].choices.First(c => c.Id == "safety_basic").Action(); // question 4 = mode
        }

        Check.That(_settings.General.SafetySetupDone).IsTrue();
        Check.That(_ended[^1]).IsTrue();
    }

    [Fact]
    public void CrashRecovery_Yes_WhileAlreadyEnabled_IsANoOpSuccess()
    {
        _settings.Supervision.Enabled = true;
        var (toggle, persisted) = WorkingSupervision(_settings);
        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        _cards[0].choices.First(c => c.Id == "safety_yes").Action();

        // No waiting card, no bring-up, chain moved on to question 1.
        Check.That(_cards.Count).IsEqualTo(2);
        Check.That(persisted).IsEmpty();
    }

    [Fact]
    public void CrashRecovery_Yes_WithAnUnrelatedFlightInFlight_DoesNotWait()
    {
        var (toggle, persisted) = WorkingSupervision(_settings);
        // Simulate an unrelated ENSURE flight: RequestEnsure() sets InFlight with kind=Ensuring.
        toggle.RequestEnsure();
        Check.That(toggle.InFlight).IsTrue();

        var flow = Flow(supervision: toggle);
        flow.StartQuestions();

        _cards[0].choices.First(c => c.Id == "safety_yes").Action();

        // The chain must NOT attach to the unrelated flight: question 1 is already up.
        Check.That(_cards.Count).IsEqualTo(2);
        Check.That(persisted).IsEmpty();
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SafetySetupFlowTests"`
Expected: FAIL — no `StartQuestions`, already-enabled "Yes" waits/behaves differently, unrelated flight attaches.

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
    /// same onEnded semantics.</summary>
    internal void StartQuestions()
    {
        _active = true;
        _pendingFailure = null;
        Advance(0);
    }
```

- [ ] **Step 4: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SafetySetupFlowTests"` → PASS (all existing pins included).

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
- Produces: `SplashThreadHandle.WhenExited(Action onMainThread)` — latched: fires immediately (on the main dispatcher) when the splash already exited; attaches otherwise; runs once per registration; the null-splash case is handled by the caller (Task 13's helper).

- [ ] **Step 1: Write the failing test**

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
    public void WhenExited_Attaches_BeforeExit_AndFiresOnce_OnTheCallingDispatcher()
    {
        var window = new FakeSplashWindow();
        var handle = new SplashThreadHandle(Dispatcher.CurrentDispatcher, window);
        var fired = new List<string>();
        var gate = new ManualResetEventSlim();

        handle.WhenExited(() => { fired.Add("a"); gate.Set(); });
        window.RaiseExit();
        Check.That(gate.Wait(2000)).IsTrue();

        handle.WhenExited(() => fired.Add("late"));   // already exited: fires immediately
        Check.That(fired).HasSize(2);
        Check.That(fired[0]).IsEqualTo("a");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandleWhenExitedTests"`
Expected: FAIL — no `WhenExited`.

- [ ] **Step 3: Implement** (in `SplashThreadHandle`, after `BeginExit()`)

```csharp
    // The exit latch: ExitFinished fires on the SPLASH thread; continuations must run on the
    // MAIN dispatcher. Latched under a lock so a registration racing the exit cannot be dropped.
    private readonly object _exitGate = new();
    private readonly List<Action> _exitContinuations = new();
    private bool _exited;

    /// <summary>
    /// Runs <paramref name="onMainThread"/> once, on the main dispatcher, after the splash's
    /// <see cref="ISplashWindow.ExitFinished"/>. Latched: a registration made after the exit
    /// (or after dispose) fires immediately. The splash-is-null case never reaches here — the
    /// caller runs its continuation directly when <c>splash is null</c>.
    /// </summary>
    internal void WhenExited(Action onMainThread)
    {
        ArgumentNullException.ThrowIfNull(onMainThread);
        bool fireNow;
        lock (_exitGate)
        {
            fireNow = _exited || _disposed;
            if (!fireNow) _exitContinuations.Add(onMainThread);
        }
        if (fireNow) RunOnMainThread(onMainThread);
    }

    private void OnWindowExitFinished()
    {
        List<Action> toRun;
        lock (_exitGate)
        {
            if (_exited) return;
            _exited = true;
            toRun = new List<Action>(_exitContinuations);
            _exitContinuations.Clear();
        }
        foreach (var continuation in toRun)
            RunOnMainThread(continuation);
    }

    private void RunOnMainThread(Action action)
    {
        var main = Application.Current?.Dispatcher;
        if (main is null)
        {
            action();   // no Application (headless test): run inline
            return;
        }
        _ = main.BeginInvoke(action);
    }
```

and wire the window event in the **constructor**:

```csharp
    internal SplashThreadHandle(Dispatcher splashDispatcher, ISplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
        window.ExitFinished += OnWindowExitFinished;   // fires on the splash thread
    }
```

- [ ] **Step 4: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadHandle"` → PASS (includes existing splash-thread tests).

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): SplashThreadHandle.WhenExited — latched main-thread continuation"
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
- Produces: keys `guide.welcome.*`, `guide.dwell.*`, `guide.dwlltime.*`, `guide.calibrate.*`, `guide.toolbar.*`, `guide.theme.*`, `guide.safety.*`, `guide.done.*`, `guide.chrome.*` — consumed by the flow/window from Task 5/7. Exact key list below.

- [ ] **Step 1: Add the surface to both lists (exact parity — same position in each)**

`StringTableSchema.cs` Surfaces — insert after `"eye-care",`:

```csharp
        "guide",
```

`tools/strings_validate.py` SURFACES — insert after `"eye-care",`:

```python
    "guide",
```

- [ ] **Step 2: Add the English keys** to `strings.json` (shape: `{"text": ..., "who": "user", "where": "guide", "fit": ..., "args": [...], "crisis": true}` — `crisis` is required by the schema for user-facing keys; every `where` must be `"guide"`):

```json
  "guide.welcome.title": { "text": "Welcome to EyeCtrl", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.welcome.body": { "text": "EyeCtrl lets you control this PC with your eyes. This short guide shows you the basics — about 2 minutes. Every practice task can be skipped.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.welcome.start": { "text": "Start", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.later": { "text": "Later", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.dwell.title": { "text": "Dwell — your click", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.dwell.body": { "text": "Rest your gaze on a target and hold still: the ring fills, and on completion the target activates. Try it on the circle below.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.dwell.skip": { "text": "Skip", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.dwelltime.title": { "text": "Dwell time", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.dwelltime.body": { "text": "This slider sets how long a desktop click takes — the cursor dwell that clicks. Try values on the practice target; the guide's own buttons keep the standard UI dwell.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.dwelltime.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.calibrate.title": { "text": "Calibration", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.calibrate.body": { "text": "Make sure the cursor follows your eyes. You can also calibrate later from Settings or the toolbar.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.calibrate.now": { "text": "Calibrate now", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.toolbar.title": { "text": "Your main menu", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.toolbar.body": { "text": "Look at the {edge} edge of the screen to summon the toolbar — the keyboard, settings and everything else open from there. Try it now.", "who": "user", "where": "guide", "fit": "prose", "args": ["edge"], "crisis": true },
  "guide.toolbar.success": { "text": "Summoned! The toolbar hides again on its own. Continue when ready.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.theme.title": { "text": "Pick a look", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.theme.body": { "text": "The theme takes effect after a restart — the card here previews your choice.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.theme.continue": { "text": "Continue", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.safety.title": { "text": "5 quick questions to keep you safe", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.safety.body": { "text": "These keep crash recovery, startup and the audible cue set up for you.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.done.title": { "text": "You're set.", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.done.body": { "text": "The docked edge summons the toolbar — your main menu. The gaze keyboard opens from it, and every setting lives in Settings.", "who": "user", "where": "guide", "fit": "prose", "args": [], "crisis": true },
  "guide.done.finish": { "text": "Finish", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.chrome.close": { "text": "Close guide", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true }
```

- [ ] **Step 3: Add the German keys** to `src/EyeCtrl.App/Localization/strings.de.json` — mirror every key with the German text (translator voice per `docs/release-notes/german-localization.md`; the `where`/`fit`/`args` fields match English exactly). Example rows:

```json
  "guide.welcome.title": { "text": "Willkommen bei EyeCtrl", "who": "user", "where": "guide", "fit": "modal170", "args": [], "crisis": true },
  "guide.later": { "text": "Später", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true },
  "guide.chrome.close": { "text": "Anleitung schließen", "who": "user", "where": "guide", "fit": "row", "args": [], "crisis": true }
```

(translate every key from Step 2 the same way)

- [ ] **Step 4: Refresh the German manifest** — add `"guide"` between `"eye-care"` and `"language-recovery"` in `src/EyeCtrl.App/Localization/strings.de.manifest.json`, then refresh source hashes:

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
- Consumes: `SafetySetupFlow.StartQuestions()` (Task 2).
- Produces: `FirstRunGuideStep` enum; `IGuideSurface` (`ShowStep`, `HideCard`, `ShowCard`, `Close`); `FirstRunGuideFlow` with `Start/Advance/Skip/Close/NotifyTaskSucceeded/NotifyCalibrationEnded/NotifySafetyEnded/WindowClosed` + `StepEntered/ToolbarTaskEntered/ToolbarTaskExited/Closed` events; `FirstRunGuideHost.EnsureCanStart()` one-flow gate. Task 7's window implements `IGuideSurface`; Task 13's coordinator subscribes the lifecycle events.

- [ ] **Step 1: Write the failing tests** (fakes per the repo's private-nested-fake convention)

```csharp
// tests/EyeCtrl.App.Tests/Services/FirstRunGuideFlowTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using NFluent;
using Xunit;

public class FirstRunGuideFlowTests
{
    private sealed class FakeSurface : IGuideSurface
    {
        public List<FirstRunGuideStep> Shown { get; } = new();
        public int HideCount; public int ShowCount; public int CloseCount;
        public void ShowStep(FirstRunGuideStep step) => Shown.Add(step);
        public void HideCard() => HideCount++;
        public void ShowCard() => ShowCount++;
        public void Close() => CloseCount++;
    }

    private static FirstRunGuideFlow Flow(
        FakeSurface surface, EyeCtrlSettings settings, bool tracker = true, bool dockedToolbar = true)
        => new(settings, trackerPresent: tracker, toolbarTaskAvailable: dockedToolbar, surface,
               createSafetyFlow: onEnded => new SafetySetupFlow(settings, supervision: null,
                   (m, c) => { /* the guide surface renders choices in the real wiring */ },
                   applyAndSave: () => { }, setSimpleSettingsUi: _ => { }, onEnded: onEnded));

    [Fact]
    public void Start_ShowsWelcome_Then_AdvancesThroughAllSteps()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        var steps = new List<FirstRunGuideStep>();
        flow.StepEntered += steps.Add;

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
        flow.NotifySafetyEnded(true);
        Check.That(steps[^1]).IsEqualTo(FirstRunGuideStep.Done);
    }

    [Fact]
    public void NoTracker_OmitsCalibrate_AndFloatingToolbar_OmitsToolbarTask()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = new FirstRunGuideFlow(settings, trackerPresent: false, toolbarTaskAvailable: false, s,
            onEnded => new SafetySetupFlow(settings, null, (m, c) => { }, () => { }, _ => { }, onEnded));

        flow.Start();
        flow.Advance();                                   // Welcome → MeetDwell
        flow.NotifyTaskSucceeded();                       // → DwellTime
        flow.Advance();                                   // → Theme (Calibrate AND ToolbarTask omitted)
        Check.That(s.Shown).ContainsOnlyElementsThatMatch(st => st is FirstRunGuideStep.Theme or FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime or FirstRunGuideStep.Welcome);
    }

    [Fact]
    public void Skip_WorksOnTasksAndCalibrate_Only()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Skip();                                      // MeetDwell skipped
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Skip();                                      // INVALID on DwellTime (Continue-only)
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.DwellTime);
        flow.Advance();                                   // → Calibrate
        flow.Skip();                                      // Calibrate skipped → ToolbarTask
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.ToolbarTask);
    }

    [Fact]
    public void Later_OnWelcome_AndChromeClose_CloseWithoutTheFlag()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        Guid? exit = null;
        flow.Closed += reason => exit = reason;
        flow.Start();
        flow.Close();
        Check.That(s.CloseCount).IsEqualTo(1);
        Check.That(exit).IsNotNull();
        Check.That(settings.General.SafetySetupDone).IsFalse();
    }

    [Fact]
    public void SafetyBlock_Completes_ThroughTheChain_AndLatches()
    {
        var s = new FakeSurface();
        var settings = new EyeCtrlSettings();
        var flow = Flow(s, settings);
        flow.Start();
        flow.Advance(); flow.NotifyTaskSucceeded(); flow.Advance();
        flow.NotifyCalibrationEnded(); flow.NotifyTaskSucceeded(); flow.Advance();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Safety);
        // The guide surface drives the chain's choices; here we answer all five "Yes".
        flow.AnswerSafetyChoice("safety_yes");            // Q0..Q3
        flow.AnswerSafetyChoice("safety_yes");
        flow.AnswerSafetyChoice("safety_yes");
        flow.AnswerSafetyChoice("safety_yes");
        flow.AnswerSafetyChoice("safety_basic");          // Q4 mode
        Check.That(settings.General.SafetySetupDone).IsTrue();
        Check.That(s.Shown[^1]).IsEqualTo(FirstRunGuideStep.Done);
    }

    [Fact]
    public void Host_RefusesASecondFlow_WhileOneIsOpen()
    {
        var host = new FirstRunGuideHost();
        Check.That(host.TryBegin(out _)).IsTrue();
        Check.That(host.TryBegin(out _)).IsFalse();
        host.End();
        Check.That(host.TryBegin(out _)).IsTrue();
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

/// <summary>The guide's eight steps, in order (spec §6). Calibrate and ToolbarTask are omitted
/// when their preconditions fail (§7 steps 4/5).</summary>
internal enum FirstRunGuideStep
{
    Welcome, MeetDwell, DwellTime, Calibrate, ToolbarTask, Theme, Safety, Done,
}

/// <summary>Why the guide closed. Abandoned = Later/chrome Close/window closed — the flag stays
/// false unless the safety block completed; Finished = the Done card's Finish.</summary>
internal enum GuideExitReason { Finished, Abandoned }

/// <summary>The flow's window surface (spec §6): the flow knows nothing WPF; the window's
/// code-behind implements it and updates <c>FirstRunGuideViewModel</c>.</summary>
internal interface IGuideSurface
{
    void ShowStep(FirstRunGuideStep step);
    /// <summary>The safety chain's showModal lands here: the surface renders the card and the
    /// wiring captures the choices so dwells forward their Action through the flow.</summary>
    void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices);
    void HideCard();
    void ShowCard();
    void Close();
}

/// <summary>
/// The first-run guide's headless state machine (the SafetySetupFlow pattern). Owns the step
/// list, the skip policy, the task-success transitions, and the absorbed safety chain; the
/// per-run wiring (Task 13) subscribes the lifecycle events to acquire/release the guide's
/// seven claims and drive the window.
/// </summary>
internal sealed class FirstRunGuideFlow
{
    private readonly EyeCtrlSettings _settings;
    private readonly bool _trackerPresent;
    private readonly bool _toolbarTaskAvailable;
    private readonly IGuideSurface _surface;
    private readonly Func<Action<bool>, SafetySetupFlow> _createSafetyFlow;
    private SafetySetupFlow? _safetyFlow;
    private bool _closed;
    private FirstRunGuideStep _step;

    /// <summary>Raised after each step change, BEFORE the surface shows it — the wiring toggles
    /// the toolbar hold here (§5 claim 6: held on non-task steps, lifted on the task).</summary>
    public event Action<FirstRunGuideStep>? StepEntered;
    public event Action? ToolbarTaskEntered;
    public event Action? ToolbarTaskExited;
    /// <summary>Raised once when the guide ends. Abandoned keeps applied answers and leaves the
    /// flag false unless the safety block completed; the chain's latching is its own.</summary>
    public event Action<GuideExitReason>? Closed;

    public FirstRunGuideFlow(EyeCtrlSettings settings, bool trackerPresent, bool toolbarTaskAvailable,
        IGuideSurface surface, Func<Action<bool>, SafetySetupFlow> createSafetyFlow)
    {
        _settings = settings;
        _trackerPresent = trackerPresent;
        _toolbarTaskAvailable = toolbarTaskAvailable;
        _surface = surface;
        _createSafetyFlow = createSafetyFlow;
    }

    public void Start()
    {
        Enter(FirstRunGuideStep.Welcome);
    }

    public void Advance()
    {
        switch (_step)
        {
            case FirstRunGuideStep.Welcome: Enter(Next(FirstRunGuideStep.MeetDwell)); break;
            case FirstRunGuideStep.MeetDwell: break;                      // task: success only
            case FirstRunGuideStep.DwellTime: Enter(Next(FirstRunGuideStep.Calibrate)); break;
            case FirstRunGuideStep.Calibrate: break;                      // skip / calibration end
            case FirstRunGuideStep.ToolbarTask: break;                    // task: success only
            case FirstRunGuideStep.Theme: Enter(Next(FirstRunGuideStep.Safety)); break;
            case FirstRunGuideStep.Safety: break;                         // chain-driven
            case FirstRunGuideStep.Done: break;
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
    /// Idempotent — the window's Closed event re-enters after surface.Close(), and must not
    /// raise Closed twice. Abandon keeps applied answers; the flag stays false unless the
    /// safety block already completed.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _safetyFlow?.Abandon();
        _surface.Close();
        Closed?.Invoke(GuideExitReason.Abandoned);
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

    /// <summary>The guide surface forwards a fired chain choice's Action (Yes/No/Basic/All).
    /// No-op outside the safety block.</summary>
    public void ForwardSafetyChoice(Action choice)
    {
        if (_step != FirstRunGuideStep.Safety) return;
        choice();
    }

    public void WindowClosed() => Close();

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
            // The chain's showModal lands on the surface (ShowSafetyCard) — the wiring captures
            // the choices there so dwells forward their Action through ForwardSafetyChoice.
            _safetyFlow = _createSafetyFlow(completed => NotifySafetyEnded(completed));
            _safetyFlow.StartQuestions();
        }

        _surface.ShowStep(step);
    }
}
```

The task-5 test's safety block uses the same forwarding (the `FakeSurface` records `LastSafetyChoices` in its `ShowSafetyCard`, and the test invokes the matching `ModalChoice`'s action exactly the way the existing `SafetySetupFlowTests` invoke choices):

```csharp
    private static void AnswerSafety(FakeSurface s, FirstRunGuideFlow flow, string id) =>
        s.LastSafetyChoices!.First(c => c.Id == id).Action();
```

(with `using System.Linq;`), replacing the five `flow.AnswerSafetyChoice(...)` calls with `AnswerSafety(s, flow, "safety_yes"); … "safety_basic");`.

- [ ] **Step 4: Implement `FirstRunGuideHost.cs`**

```csharp
namespace EyeCtrl.App.Services;

using System;

/// <summary>
/// The guide's one-flow gate (the SafetySetupHost pattern, minus the Settings-window coupling):
/// TryBegin fails while a guide is open; End releases. Per-run construction means the gate is
/// the only cross-run state.
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
git -C .worktrees/first-run-guide commit -m "feat(guide): headless FirstRunGuideFlow + host — steps, skip policy, safety absorption"
```

---

### Task 6: `FirstRunGuideViewModel` — binding surface + gaze contract

**Files:**
- Create: `src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs` (new)

**Interfaces:**
- Consumes: `DwellOverlayViewModelBase` (single detector — chrome dwell), `LeaveBeforeArmLatch`.
- Produces: `FirstRunGuideViewModel` with `IsVisible`, `Title`, `Body`, `Choices` (observable), `ShowSafetyCard(message, choices)`, `ChromeCloseVisible`, practice-target detector access, `DwellActiveWhilePaused => true`, `OnGuideTargetActivated` routing, `CloseRequested` event. Task 7's window binds it; Task 13's wiring feeds it.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using CommunityToolkit.Mvvm.Messaging;
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
        vm.ShowDwellPracticeStep();

        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("practice");

        long t = 0;
        for (var i = 0; i < 12; i++)   // 300 ms at 30 ms samples — the ACTIVATION knob, not 900
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t));
            t += 30_000;
        }
        Check.That(activated).HasSize(1);
    }

    [Fact]
    public void Gaze_GatesOnIsInGap_NotOnIsPaused()
    {
        var vm = Vm();
        vm.ShowDwellPracticeStep();
        var activated = new List<string>();
        vm.PracticeTargetActivated += () => activated.Add("p");

        // paused sample, not in gap: accumulates (DwellActiveWhilePaused = true)
        long t = 0;
        for (var i = 0; i < 30; i++)
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(280, 220), t) { IsPaused = true });
            t += 30_000;
        }
        Check.That(activated).HasSize(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests"`
Expected: FAIL — no `FirstRunGuideViewModel`.

- [ ] **Step 3: Implement**

```csharp
namespace EyeCtrl.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Binding surface for the first-run guide's current step (spec §5/§6). The chrome detector
/// (base class) dwells at ToolbarButtonDwellMs; the practice target carries its OWN detector at
/// ActivationButtonDwellMs (r1 F7's two-knob split — the base owns a single detector). Gaze
/// routes through SampleMayDwell (gates !IsInGap, NOT IsPaused) — DwellActiveWhilePaused is
/// true, so the guide stays usable under user-pause (§9).
/// </summary>
public sealed partial class FirstRunGuideViewModel : DwellOverlayViewModelBase
{
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _practiceDetector;
    private readonly LeaveBeforeArmLatch _latch;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _chromeCloseVisible;
    [ObservableProperty] private bool _practiceVisible;
    [ObservableProperty] private bool _sliderVisible;
    [ObservableProperty] private bool _themeChipsVisible;

    /// <summary>The practice target fired (its own ActivationButtonDwellMs detector).</summary>
    public event Action? PracticeTargetActivated;
    /// <summary>The chrome Close (or Later) fired.</summary>
    public event Action? CloseRequested;
    /// <summary>A card choice fired (id from the strings-driven choice row).</summary>
    public event Action<string>? ChoiceActivated;
    /// <summary>The dwell-time slider changed (physical-Px position → 200–2000 ms).</summary>
    public event Action<int>? DwellTimeChanged;
    /// <summary>A theme chip fired (theme name).</summary>
    public event Action<string>? ThemeChosen;

    internal bool DwellActiveWhilePausedForTest => DwellActiveWhilePaused;

    public FirstRunGuideViewModel(IMessenger messenger, int dwellCooldownMs,
        EyeCtrlSettings settings, IStruggleTelemetrySink? sink,
        IDwellDetectorRegistry? dwellRegistry)
        : base(messenger, dwellCooldownMs, context: DwellContext.Other, sink: sink,
               dwellRegistry: dwellRegistry, pauseGraceSettings: settings.Dwell)
    {
        _settings = settings;
        _practiceDetector = new DwellDetector(dwellCooldownMs: 0, DwellContext.Other, sink,
            pauseGraceMs: 0) { OtherTargetReleasesHold = true };
        _practiceDetector.TargetActivated += _ => PracticeTargetActivated?.Invoke();
        _latch = new LeaveBeforeArmLatch(DwellDetector, releaseMarginPhysical: () => 70);
    }

    /// <summary>The two knob values the step-3 slider writes through (200–2000).</summary>
    public int DwellTimeMs => _settings.Dwell.ActivationButtonDwellMs;

    public void SetDwellTime(int ms)
    {
        _settings.Dwell.ActivationButtonDwellMs = Math.Clamp(ms, 200, 2000);
        DwellTimeChanged?.Invoke(_settings.Dwell.ActivationButtonDwellMs);
    }

    /// <summary>Shows a card: title/body from the string keys, chrome Close armed via the latch.</summary>
    public void ShowCard(string title, string body, bool chromeClose)
    {
        Title = title;
        Body = body;
        ChromeCloseVisible = chromeClose;
    }

    /// <summary>The safety chain's card (spec §7 step 7): the body becomes the message and the
    /// choices are stored — the window renders each choice's Label as a chrome dwell target, and
    /// a fired choice forwards its Action through the flow's ForwardSafetyChoice.</summary>
    public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
    {
        Body = message;
        SafetyChoices = choices;
    }

    /// <summary>The safety chain's current choices (null outside the safety block).</summary>
    public IReadOnlyList<ModalChoice>? SafetyChoices { get; private set; }

    public void ShowDwellPracticeStep()
    {
        PracticeVisible = true;
        // Big tolerant target, centre of the card's practice area (physical px set by the view).
        _practiceDetector.AddTarget(new DwellTarget("guide_practice",
            new PhysicalPoint(280, 220), radius: 60, dwellTimeMs: _settings.Dwell.ActivationButtonDwellMs));
    }

    public void HidePracticeTarget() => _practiceDetector.ClearTargets();

    /// <summary>The practice target follows the live ActivationButtonDwellMs (step 3 slider).</summary>
    public void RefreshPracticeDwellMs() { /* view re-reads DwellTimeMs when the slider moves */ }

    /// <summary>Per-sample feed: the base detector (chrome) AND the practice detector, both
    /// through the shared gate — a gap-held sample accumulates nowhere.</summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);
        if (IsVisible && SampleMayDwell(message))
        {
            _latch.Update(message.Position);
            DwellDetector.Update(message.Position, message.TimestampMicroseconds);
            if (PracticeVisible)
                _practiceDetector.Update(message.Position, message.TimestampMicroseconds);
        }
    }

    /// <summary>Chrome target activated: the window's code-behind routes ids to the flow.</summary>
    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == "guide_close") { CloseRequested?.Invoke(); return; }
        ChoiceActivated?.Invoke(targetId);
    }

    protected override void OnTargetProgress(string targetId, double progress) { /* bound in XAML */ }

    /// <summary>The latch holds every newly-shown choice until gaze left it (§5 disarm latch).</summary>
    public void ArmLatch() => _latch.HoldAll();
    public void LatchPositioned(string id) => _latch.Positioned(id);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideViewModelTests"` → PASS. Also `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiring"` → PASS (the guide VM satisfies the sink/ctor conventions; `EveryDwellOverlaySubclass_TakesTheSinkOnItsSingleConstructor` — the ctor above takes the sink on its single ctor).

- [ ] **Step 5: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.ViewModels/FirstRunGuideViewModel.cs tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideViewModelTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideViewModel — dual dwell knobs, latch, paused-dwell opt-in"
```

---

### Task 7: `FirstRunGuideWindow` — XAML, code-behind, overlay inventory

**Files:**
- Create: `src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml` + `.xaml.cs`
- Modify: `docs/design/overlay-unification-spec.md` (§3.1 row + D17 map)
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`RaiseToTop()` on `IToolbarOverlay` + `ICursorCircleOverlay`)
- Test: `tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs` (new, STA)

**Interfaces:**
- Consumes: `FirstRunGuideViewModel` (Task 6).
- Produces: `FirstRunGuideWindow : OverlayWindow` — `IsClickThrough = true`, `AllowsTransparency`, no post-show Opacity writes; `IGuideSurface` implementation lives in Task 13's wiring (the window exposes `ShowStepContent(...)`, `HideCard()`, `ShowCard()`, plus `ToolbarAndRingRaised` handling). `IToolbarOverlay.RaiseToTop()`/`ICursorCircleOverlay.RaiseToTop()` exist for step 5.

- [ ] **Step 1: The window XAML** (`FirstRunGuideWindow.xaml`) — fullscreen, click-through backdrop, centered card:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.FirstRunGuideWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
                        IsClickThrough="True"
                        WindowStyle="None" AllowsTransparency="True"
                        ShowInTaskbar="False" WindowStartupLocation="CenterScreen"
                        Topmost="True" ShowActivated="False">
    <Grid>
        <!-- Fullscreen backdrop: transparent (click-through), the card is the only interactive part -->
        <Border x:Name="Card" Width="560" Height="440"
                Background="{DynamicResource BrushSurfaceDark}"
                CornerRadius="{DynamicResource LargeCornerRadiusKey}"
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
                <StackPanel x:Name="ChoiceRow" Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Right"/>
            </Grid>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Code-behind** (`FirstRunGuideWindow.xaml.cs`):

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window (spec §5): fullscreen click-through backdrop with a centered
/// interactive card. AllowsTransparency + no post-Show Opacity writes (InvisibleTestWindow.Prepare
/// compatible, r1 F10). The XAML binds FirstRunGuideViewModel; step content is code-built because
/// each step hosts different live controls (practice target, slider, theme chips).
/// </content>
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    public FirstRunGuideWindow(FirstRunGuideViewModel vm)
    {
        InitializeComponent();
        IsClickThrough = true;
        DataContext = vm;
    }

    /// <summary>Step 5: the summoned toolbar (and ring) must sit above this window.</summary>
    public void RaiseToolbarAndRingAbove()
    {
        Toolbar?.RaiseToTop();
        if (CursorVM is { IsVisible: true })
            CursorCircle?.RaiseToTop();
    }
}
```

(The `Toolbar`/`CursorCircle` accesses go through the injected overlay accessor the wiring passes in — Task 13 wires the actual raise; the method above is the window-side hook the wiring calls.)

- [ ] **Step 3: Overlay inventory row** — `docs/design/overlay-unification-spec.md` §3.1 gains (after the Splash row, same column shape):

```
| Guide | `FirstRunGuideWindow` | `#F0181818`-card on transparent | XAML | collision-zone |
```

and `OverlayInventoryDriftTests.D17CollisionZoneClasses` gains `"FirstRunGuideWindow"` → keep both directions green.

- [ ] **Step 4: `IOverlayWindows.cs`** — add to `ICursorCircleOverlay` and `IToolbarOverlay`:

```csharp
    /// <summary>Raises this window above any later-created fullscreen topmost overlay (the guide).
    /// Uncontested — the same RaiseIntoBand machinery the pause strip uses.</summary>
    void RaiseToTop();
```

(`OverlayWindow` already implements it — `OverlayWindow.cs:377`.)

- [ ] **Step 5: STA tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

public class FirstRunGuideWindowTests
{
    [StaFact]
    public void Window_IsClickThrough_AllowsTransparency_AndBindsTheVm()
    {
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null);
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);

        Check.That(window.IsClickThrough).IsTrue();
        Check.That(window.AllowsTransparency).IsTrue();
        Check.That(window.DataContext).IsSameReferenceAs(vm);
        window.Close();
    }
}
```

- [ ] **Step 6: Run to verify pass + repo gates**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~FirstRunGuideWindowTests|FullyQualifiedName~OverlayInventoryDrift"` → PASS.

- [ ] **Step 7: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Overlays/FirstRunGuideWindow.xaml* docs/design/overlay-unification-spec.md tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowTests.cs src/EyeCtrl.App/Services/IOverlayWindows.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): FirstRunGuideWindow + overlay inventory row + RaiseToTop interface members"
```

---

### Task 8: `ToolbarAutoHideController` — guide-hidden hold + `ResetToHidden`

**Files:**
- Modify: `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` (extend — the file's existing harness/fakes are reused)

**Interfaces:**
- Produces: `ToolbarAutoHideController.SetGuideHiddenHold(bool)` — while true, `ForceShow` and `ReassertVisibility`'s `SetActive(true)` re-arm are suppressed; `ResetToHidden()` — state → Hidden, anchors cleared, hidden via `_visibility` (mirror `ResetToVisible()` at :551).

- [ ] **Step 1: Write the failing tests** (in the controller's existing test file, reusing its harness — the file drives the controller with recorded `_visibility` fakes)

```csharp
    [Fact]
    public void GuideHiddenHold_SuppressesForceShow_AndReassertVisibility()
    {
        var controller = CreateController();          // the file's harness
        controller.SetGuideHiddenHold(true);
        controller.SetSuspended(true);                // would normally ForceShow when Hidden
        Check.That(controller.StateName).IsEqualTo("Visible");

        controller.ResetToHidden();
        controller.SetGuideHiddenHold(false);
        controller.ReassertVisibility();
        // ... per the harness's recorded-call fake: no ForceShow, no SetActive(true) while held
    }

    [Fact]
    public void ResetToHidden_PutsTheControllerInHidden_WithClearedAnchors()
    {
        var controller = CreateController();
        controller.ResetToVisible();
        Check.That(controller.StateName).IsEqualTo("Visible");

        controller.ResetToHidden();
        Check.That(controller.StateName).IsEqualTo("Hidden");
        // A same-moment edge dwell now summons without waiting hideDelayMs (Feed's Hidden case).
    }
```

- [ ] **Step 2: Implement**

```csharp
    // The guide-hidden hold (spec §5 claim 6): the first-run guide hides the toolbar on steps
    // 1–4/6–8. While held, the two shipped paths that would resurrect a hidden toolbar — the
    // async ForceShow inside SetSuspended, and ReassertVisibility's else-branch SetActive(true)
    // (calibration transitions) — are suppressed, and release restores unconditionally (r4 F5).
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
            _visibility?.HideToolbar(_paused);
        else if (!_guideHiddenHold)
            _visibility.SetActive(true);
```

- [ ] **Step 3: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHideController"` → PASS (existing pins: settings suspend-recover, calibration reassert).

- [ ] **Step 4: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/ToolbarAutoHideController.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): ToolbarAutoHideController guide-hidden hold + ResetToHidden"
```

---

### Task 9: Toolbar wiring — lifecycle holder, feed terms, `OnTargetActivated` gate

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- Test: extend the existing `ToolbarLifecycleCoordinatorTests` + a ToolbarViewModel gate test

**Interfaces:**
- Consumes: Task 8's `SetGuideHiddenHold`/`ResetToHidden` (the lifecycle forwards: `ResetAutoHideToHidden()`).
- Produces: `ToolbarLifecycleCoordinator.SetGuideHold(bool held)` — `SetAutoHideSuspended("guide", held)` + `SetGuideHiddenHold(held)` (+ on release: `SetActive(true)` + `ResetToVisible`-restore is the CLOSE path, not this); `ResetToolbarForGuideTask()` — release the hold + `ResetToHidden()`; `ShouldFeedKeyboardSample`/`ShouldFeedSettingsSample` gain `guideOpen`; `ToolbarViewModel.GuideOpenGate` property consulted in `OnTargetActivated`.

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

(update both call sites to pass `_modal.GuideOpen`), plus two methods:

```csharp
    /// <summary>The guide's toolbar hold: named holder + the controller's hidden hold (§5 claim 6).
    /// Release (guide close) is the coordinator's restore path — ForceShow + ResetToVisible.</summary>
    public void SetGuideHold(bool held)
    {
        SetAutoHideSuspended("guide", held);
        _autoHide?.SetGuideHiddenHold(held);
    }

    /// <summary>Step-5 entry: release the hold and reset the controller to Hidden so the summon
    /// detection is live immediately (r4 F2).</summary>
    public void ResetToolbarForGuideTask()
    {
        SetAutoHideSuspended("guide", false);
        _autoHide?.SetGuideHiddenHold(false);
        _autoHide?.ResetToHidden();
    }
```

`ToolbarViewModel.cs` — a gate property + the first line of `OnTargetActivated`:

```csharp
    /// <summary>While the first-run guide owns the screen, every non-Pause activation is inert
    /// (§5 claim 7): the summoned toolbar is a display, not a control surface. Pause stays live
    /// (the deliberate emergency path). Null (tests, non-guide wiring) = not gated.</summary>
    internal Func<bool>? GuideOpenGate { get; set; }
```

```csharp
    protected override void OnTargetActivated(string targetId)
    {
        if (targetId != "Pause" && GuideOpenGate?.Invoke() == true)
            return;
```

`ToolbarLifecycleCoordinator.cs` — the adapter's visibility event is forwarded for the guide's step-5 wiring:

```csharp
    /// <summary>Forwarded from the visibility adapter's <c>VisibilityChanged</c> (UI thread).
    /// The guide's wiring uses the true transitions to raise the summoned toolbar above itself
    /// and to mark the summon task done (spec §7 step 5). Raised for every transition, guide or
    /// not — subscribers filter.</summary>
    public event Action<bool>? ToolbarVisibilityChanged;
```

(subscribe in `SetupToolbarAutoHide`, where `_visibility` is built: `_visibility.VisibilityChanged += v => ToolbarVisibilityChanged?.Invoke(v);`)

- [ ] **Step 3: Run to verify pass + regression**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarLifecycleCoordinator"` and `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~Toolbar"` → PASS.

- [ ] **Step 4: Commit**

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs src/EyeCtrl.ViewModels/ToolbarViewModel.cs tests/EyeCtrl.App.Tests/Services/Coordinators/ToolbarLifecycleCoordinatorTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): toolbar hold/ResetToHidden wiring + GuideOpen feed terms + button gate"
```

---

### Task 10: Ring re-show predicate — both coordinators

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs`
- Test: extend the coordinators' existing test files (their harnesses construct with `_modal`)

**Interfaces:**
- Consumes: Task 1's `ModalInteractionState.GuideOpen`.
- Produces: a shared predicate `RingMayShow() => !modal.GuideOpen || guideAllowsRing` — concretely: both `cursorCircle.Show()` call sites check `!_modal.GuideOpen` (or `Context.CursorVM.IsVisible` re-show paths defer while the guide is up except during its toolbar task; the guide's wiring re-shows the ring itself on task entry and restores on close — the coordinators only need to NOT re-show over the guide).

- [ ] **Step 1: Failing tests** (in the existing coordinator test files)

```csharp
    [Fact]
    public void RingReshow_IsSuppressed_WhileGuideOpen()
    {
        // harness: modal.GuideOpen = true; raise CursorVM.IsVisible flip; the recorded
        // cursorCircle fake must NOT receive Show().
        modal.GuideOpen = true;
        cursorVm.IsVisible = true;
        Check.That(recordingCursor.ShowCount).IsEqualTo(0);
    }
```

- [ ] **Step 2: Implement** — in `OverlayVmBindingCoordinator`'s `IsVisible` case:

```csharp
            case nameof(CursorCircleViewModel.IsVisible):
                if (Context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen && !_modal.GuideOpen)
                    cursorCircle.Show();
                else
                    cursorCircle.Hide();
                break;
```

in `OverlayMessageCoordinator`'s calibration `Active/Failed` and `Idle/Cancelled` branches, replace `if (Context.CursorVM.IsVisible) cursorCircle.Show();` with:

```csharp
                        if (Context.CursorVM.IsVisible && !_modal.GuideOpen) cursorCircle.Show();
```

(the guide's own wiring restores the ring on close — Task 13.)

- [ ] **Step 3: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~OverlayVmBinding|FullyQualifiedName~OverlayMessage"` → PASS.

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs tests/
git -C .worktrees/first-run-guide commit -m "feat(guide): ring re-show gated on GuideOpen in both coordinators"
```

---

### Task 11: Proactive surfaces — BannerSlot hold, StatusCardPresenter, hosts, SetupNoticeWatch

**Files:**
- Modify: `src/EyeCtrl.App/Services/BannerSlot.cs`
- Modify: `src/EyeCtrl.App/Services/StatusCardPresenter.cs` (no code change — App.xaml.cs wiring only; keep the file untouched)
- Modify: `src/EyeCtrl.App/Services/RestartNoticeHost.cs`
- Modify: `src/EyeCtrl.App/Services/LagMarkHost.cs`
- Modify: `src/EyeCtrl.App/Services/SetupNoticeWatch.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs` (new) + host test extensions

**Interfaces:**
- Produces: `BannerSlot.Hold()/Release()` (hide + re-queue live banner at the FRONT, pause dequeue; release resumes without wedging/dropping); `RestartNoticeHost` GuideOpen-aware defer (blocked shows stashed, replayed on guide close); `LagMarkHost.OnGuideOpenChanged(bool)`; `SetupNoticeWatch` `!IsGuideOpen` gate (checks pause, one poll on close).

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
    // because the hold clears it. UI thread only, like the rest of the slot.
    private bool _held;

    /// <summary>Hold for a fullscreen surface (the first-run guide): the live banner hides and
    /// re-queues at the front; queued banners wait. Idempotent.</summary>
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

    /// <summary>Release: dequeue resumes at once with the front of the queue (nothing dropped).</summary>
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

- [ ] **Step 3: RestartNoticeHost + LagMarkHost + SetupNoticeWatch**

`RestartNoticeHost` — ctor gains `Func<bool> isGuideOpen` (wired `() => _eventCoordinator.IsGuideOpen` at App.xaml.cs:519's construction); the three show paths' guards gain `|| _isGuideOpen()`, with the blocked call stashed for replay:

```csharp
    private readonly Func<bool> _isGuideOpen;
    private Action? _pendingOnGuideClosed;

    /// <summary>Stashes a blocked show for replay on guide close (defer, never drop — r3 F4).</summary>
    private bool BlockedByGuide()
    {
        if (!_isGuideOpen()) return false;
        return true;   // caller stashes its own continuation
    }
```

Concretely each show path becomes: `if (_isSettingsOpen() || _isProbeOpen()) return; if (_isGuideOpen()) { _pendingOnGuideClosed = () => ShowInProgress(); return; } ShowInProgress();` — the host gains:

```csharp
    /// <summary>Guide opened or closed. Opening takes a notice down (it covers the card);
    /// closing replays at most ONE stashed show (the newest wins — an older notice is stale).</summary>
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

`LagMarkHost` — ctor gains `Func<bool> isGuideOpen` (App.xaml.cs:605), `CanShow()` gains `&& !_isGuideOpen()`, and:

```csharp
    /// <summary>Guide opened or closed — the button hides and returns like Settings does (r3 F4).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (open || _isGuideOpen())
            _postToUi(HideButtonAndZone);
        else if (CanShow())
            _postToUi(ShowButtonAndZone);
    }
```

`SetupNoticeWatch` — ctor gains `Func<bool>? isGuideOpen = null` (App.xaml.cs:577), and the three `CheckStartup()` call sites (Start :91, Poll :110, Poke :136) become:

```csharp
        if (_isGuideOpen?.Invoke() != true && _service.CheckStartup())
```

wait — careful: the guide-open case must SKIP the check entirely (not just skip showing) so `WriteShownTime` never runs behind the guide; the watch keeps polling:

```csharp
        // r4 F3: CheckStartup writes setup-notice-shown.json BEFORE _show, so while the guide is
        // open the check is skipped entirely (the watch keeps polling); on guide close one poll
        // runs and the notice shows then.
        if (_isGuideOpen?.Invoke() == true)
            return false;   // call sites keep their schedule/flow
```

and the watch gains:

```csharp
    /// <summary>Guide opened or closed: closing pokes once (r4 F3).</summary>
    public void OnGuideOpenChanged(bool open)
    {
        if (!open) Poke();
    }
```

- [ ] **Step 4: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~BannerSlot|FullyQualifiedName~RestartNotice|FullyQualifiedName~SetupNotice|FullyQualifiedName~LagMark"` → PASS.

```bash
git -C .worktrees/first-run-guide add -A src/EyeCtrl.App/Services/BannerSlot.cs src/EyeCtrl.App/Services/RestartNoticeHost.cs src/EyeCtrl.App/Services/LagMarkHost.cs src/EyeCtrl.App/Services/SetupNoticeWatch.cs tests/EyeCtrl.App.Tests/Services/BannerSlotHoldTests.cs
git -C .worktrees/first-run-guide commit -m "feat(guide): BannerSlot hold + defer hosts + SetupNoticeWatch GuideOpen gate"
```

---

### Task 12: Startup wiring — the per-run raise, claims lifecycle, and the retired machinery

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (rename `SafetySetupRequestedMessage` → `FirstRunGuideRequestedMessage`)
- Modify: `src/EyeCtrl.App/Services/SafetySetupFlow.cs` (delete `SafetySetupLaunch`/`SafetySetupStartup` sends renamed; `SafetySetupStartup.MaybeRequest` sends the new message)
- Modify: `src/EyeCtrl.App/Services/Coordinators/SettingsUiCoordinator.cs` (per-run factory replaces `ShowSafetySetup`)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (splash forwarding, canInterrupt, status-card hold, guide raise ordering, retire SafetySetupHost consumers)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (+ `SettingsPageContext.cs`, `GeneralPageSource.cs`) — retire the window-side host and swap the row
- Test: extend `SafetySetupWiringTests` + `GeneralPageSourceTests`

**Interfaces:**
- Consumes: Tasks 1, 2, 3, 5, 6, 7, 8, 9, 10, 11.
- Produces: the complete first-run raise: message renamed, coordinator builds the per-run trio, claims acquired at `Show()` inside `WhenExited`, step-5 toggles, restore on close, `g_guide` row.

- [ ] **Step 1: Rename the message + raise seam**

`InteractionMessages.cs`:

```csharp
/// <summary>Raise the first-run guide (which absorbs the safety setup). Sent once at startup while
/// <c>General.SafetySetupDone</c> is false, and from the Settings → General re-run row.</summary>
public record FirstRunGuideRequestedMessage;
```

(delete `SafetySetupRequestedMessage`; update `SafetySetupStartup.MaybeRequest` to send `FirstRunGuideRequestedMessage` and its doc comment.)

- [ ] **Step 2: The per-run factory in `SettingsUiCoordinator`** — replaces `ShowSafetySetup()` entirely. Fields + entry:

```csharp
    private FirstRunGuideHost? _guideHost;
    private SplashThreadHandle? _splashHandle;
    private FirstRunGuideWindow? _guideWindow;
    private FirstRunGuideViewModel? _guideVm;
    private FirstRunGuideFlow? _guideFlow;

    /// <summary>Hands the splash handle in for the latched show. Called once from App.xaml.cs
    /// right after <c>_eventCoordinator.Initialize(...)</c> (the handle exists by then —
    /// <c>_splashHandle = splash</c> runs at App.xaml.cs:175, Initialize later).</summary>
    public void SetSplashHandle(SplashThreadHandle? handle) => _splashHandle = handle;
```

The message registration replaces the `SafetySetupRequestedMessage` one:

```csharp
        // First-run guide: raised at startup (the renamed gate) and from the Settings → General row.
        messenger.Register<SettingsUiCoordinator, FirstRunGuideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(StartFirstRunGuide));
```

The factory:

```csharp
    private void StartFirstRunGuide()
    {
        var context = Context!;
        if (_guideHost is not null) return;                        // one-flow rule (r1 F4)

        var host = new FirstRunGuideHost();
        if (!host.TryBegin(out var lease)) return;
        _guideHost = host;

        var settings = context.Settings;
        bool tracker = App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool toolbarTask = tracker && settings.Toolbar.IsDocked;   // spec §7 step 5

        _guideVm = new FirstRunGuideViewModel(context.Messenger,
            dwellCooldownMs: (int)settings.Dwell.ToolbarButtonDwellMs,
            settings, sink: null, dwellRegistry: null);
        _guideWindow = new FirstRunGuideWindow(_guideVm);

        _guideFlow = new FirstRunGuideFlow(settings, trackerPresent: tracker,
            toolbarTaskAvailable: toolbarTask,
            surface: new GuideSurface(_guideWindow, _guideVm, () => _guideFlow),
            createSafetyFlow: onEnded => new SafetySetupFlow(settings, _supervisionToggle,
                showModal: (message, choices) => _guideVm!.ShowSafetyCard(message, choices),
                applyAndSave: () => context.SettingsService.Save(settings),
                setSimpleSettingsUi: v => settings.General.SimpleSettingsUi = v,
                onEnded: completed => _guideFlow?.NotifySafetyEnded(completed)));
```

The claims, acquired at the **actual `Show()`** inside the latched continuation (r3 F6) — null splash ⇒ immediate:

```csharp
        void Show()
        {
            _modal.GuideOpen = true;                       // claim 2 (+ every _modal reader)
            _quickActions.SetSummonSuspended("guide", true);   // claim 5
            _toolbar.SetGuideHold(true);                   // claim 6 (steps 1–4/6–8 hold)
            Log.Information("First-run guide: shown");
            _guideWindow!.Show();
            _guideVm!.ArmLatch();                          // first card disarmed until gaze leaves
            _guideFlow!.Start();
        }
        if (_splashHandle is { } splash) splash.WhenExited(Show);
        else Show();
```

Step toggles and the toolbar-shown observation (the adapter's `VisibilityChanged` forwarded by the lifecycle):

```csharp
        _guideFlow.StepEntered += step => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen) return;
            if (step == FirstRunGuideStep.ToolbarTask)
            {
                _toolbar.ResetToolbarForGuideTask();       // claim 6(d): hold released + Hidden
                _guideWindow?.RaiseToolbarAndRingAbove();  // Guide < Toolbar < PauseIndicator < ring
            }
            else
            {
                _toolbar.SetGuideHold(true);               // non-task steps hold again
            }
        });

        _toolbar.ToolbarVisibilityChanged += visible => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen || !visible) return;
            _guideWindow?.RaiseToolbarAndRingAbove();      // ring raised AFTER the toolbar
            _guideFlow.NotifyTaskSucceeded();              // the summon IS the task's success
        });
```

Restore on close — **unconditional, every exit path** (r4 F5):

```csharp
        void Restore(GuideExitReason reason)
        {
            _modal.GuideOpen = false;
            _quickActions.SetSummonSuspended("guide", false);
            _toolbar.SetGuideHold(false);
            _toolbar.ForceShow();                          // ForceShow + ResetToVisible — every mode
            Log.Information("First-run guide: closed (completed={Completed})",
                reason == GuideExitReason.Finished);
            _guideWindow?.Close();
            _guideWindow = null;
            _guideVm = null;
            _guideFlow = null;
            _guideHost = null;
            lease.Dispose();
        }
        _guideFlow.Closed += reason => context.Dispatcher.BeginInvoke(() => Restore(reason));
        _guideWindow.Closed += (_, _) => _guideFlow?.WindowClosed();   // external close = abandon
```

The `GuideSurface` (nested private class, same file):

```csharp
    /// <summary>The flow's window surface: maps steps to the window/VM. Safety cards route the
    /// chain's choices back through the flow.</summary>
    private sealed class GuideSurface(FirstRunGuideWindow window, FirstRunGuideViewModel vm,
        Func<FirstRunGuideFlow?> flow) : IGuideSurface
    {
        public void ShowStep(FirstRunGuideStep step) => window.ShowStep(step, vm);
        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            vm.ArmLatch();                                 // every new card disarmed until gaze leaves
        }
        public void HideCard() => window.HideCard();
        public void ShowCard() => window.ShowCard();
        public void Close() => window.Close();
    }
```

- [ ] **Step 3: The window's step rendering** — `FirstRunGuideWindow.ShowStep(step, vm)` (added to Task 7's code-behind) switches per step: Welcome/Calibrate/ToolbarTask/Done/Theme render title+body and choice buttons bound to `vm.ChoiceActivated` (Welcome: Start→`flow.Advance()`/Later→`flow.Close()`; Calibrate: "Calibrate now"→`Context.Messenger.Send(new CalibrationRequestedMessage())` + `HideCard()` + the wiring's `CalibrationStateChangedMessage` subscription advances on `Idle/Cancelled`; Theme: four chips → `vm.ThemeChosen` → `settings.Theme.Name = name` + apply-save → Continue→`flow.Advance()`; DwellTime: `DwellSlider` on `vm.DwellTimeMs`/`SetDwellTime`; MeetDwell/ToolbarTask: practice content/edge instruction + Skip). The choice ids are the `guide.*` keys from Task 4; `FirstRunGuideFlow.StepEntered`'s handler holds/releases the toolbar; `ChromeCloseVisible` shows the chrome Close → `flow.Close()`.

- [ ] **Step 4: Calibration advance + the DwellTime persistence** — the wiring subscribes once per run:

```csharp
        void OnCalibrationStateChanged(CalibrationStateChangedMessage m)
        {
            if (m.State is CalibrationState.Idle or CalibrationState.Cancelled)
            {
                context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
                context.Dispatcher.BeginInvoke(() =>
                {
                    _guideWindow?.ShowCard();
                    _guideFlow?.NotifyCalibrationEnded();
                });
            }
        }
        context.Messenger.Register<SettingsUiCoordinator, CalibrationStateChangedMessage>(this, (_, m) => OnCalibrationStateChanged(m));
```

The DwellTime step's live apply uses the same debounced-save path the Settings rows use (`context.SettingsService.Save(settings)` in the VM's `DwellTimeChanged` handler wired at factory time: `vm.DwellTimeChanged += _ => context.SettingsService.Save(settings);`).

- [ ] **Step 4b: Retire the old machinery + swap the row** — delete `SafetySetupLaunch` and `SafetySetupHost` from `SafetySetupFlow.cs` (the file keeps `SafetySetupStartup` with the renamed message and `SafetySetupFlow` itself); in `SettingsWindow.xaml.cs` remove the `_safetySetup` field + `SafetySetupHost` construction (:521-524), `StartSafetySetup` (:1227), and `CreateSafetySetupFlow` (:1233); in `SettingsPageContext.cs` remove `RunSafetySetup` (:97) and the `SettingsWindow` constructor argument that feeds it (:667); in `GeneralPageSource.cs` replace the `g_safety` ButtonSpec with:

```csharp
                new ButtonSpec("g_guide",
                    StringTable.Current.T("settings.general.guide.label"),
                    () => { _ctx.Close(); _ctx.Messenger.Send(new FirstRunGuideRequestedMessage()); },
                    ButtonKind.Reset, Width: 300, Height: 100) { Simple = true },
```

plus the row's string keys (surface `"settings"`, so they do NOT live in the guide surface): `"settings.general.guide.label"` = "Run the introduction guide" / DE "Einführung erneut starten" — replacing `"settings.general.safety.label"` ("Run Safety Setup Again") in both `strings.json` and `strings.de.json` (the old key is deleted; `tools/strings_validate.py` fails on orphaned keys otherwise).

- [ ] **Step 5: App.xaml.cs wiring** — four changes:
  1. after `_eventCoordinator.Initialize(...)`: `_eventCoordinator.SetSplashHandle(_splashHandle);`
  2. `:704` becomes `canInterrupt: im.CurrentState == InteractionState.FreeLook && !_eventCoordinator.IsGuideOpen`
  3. the banner hold/release + status-card hold (replaces the bare `Start()` at :424):
```csharp
            var bannerSlot = _serviceProvider.GetRequiredService<BannerSlot>();
            _eventCoordinator.GuideOpenChanged += open =>
            {
                if (open) bannerSlot.Hold(); else bannerSlot.Release();
            };

            bool startupGuidePending = SafetySetupStartup.ShouldPrompt(
                settings.General.SafetySetupDone, IsPausedLaunch, IsTestMode);
            if (startupGuidePending)
            {
                // The NoTracker/Paused card must not spend its state behind the guide (r2 F1):
                // the presenter starts on guide close — or on guide-startup failure (fallback release).
                void StartStatusCards()
                {
                    _eventCoordinator.GuideOpenChanged -= onGuideClosed;
                    _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
                }
                void onGuideClosed(bool open) { if (!open) StartStatusCards(); }
                _eventCoordinator.GuideOpenChanged += onGuideClosed;
                try
                {
                    // The raise itself runs via the coordinator's BeginInvoke; if the factory
                    // throws before Show(), the gate never opens and the cards must not wedge.
                    // SafetySetupStartup.MaybeRequest below is the only raiser; wrap nothing here —
                    // the coordinator's own try/catch (Task 5's lease pattern) releases via
                    // Restore(). A guide that never opens would wedge the hold, so the raise path
                    // guarantees Closed-or-lease-dispose on every failure.
                }
                finally { /* no-op: the fallback release lives in the coordinator's Restore */ }
            }
            else
            {
                _serviceProvider.GetRequiredService<StatusCardPresenter>().Start();
            }
```
  4. `SafetySetupStartup.MaybeRequest`'s call site and comment (App.xaml.cs:666) unchanged except the message it sends is now `FirstRunGuideRequestedMessage` (Task 12 Step 1 renamed it).
  5. the guide claims also cover `SetupNoticeWatch`/hosts: `_eventCoordinator.GuideOpenChanged` subscribers added at the existing construction sites (`RestartNoticeHost` :519 gains `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `GuideOpenChanged` → `OnGuideOpenChanged`; `LagMarkHost` :605 likewise; `SetupNoticeWatch` :577 gains `isGuideOpen: () => _eventCoordinator.IsGuideOpen` + `GuideOpenChanged` → `OnGuideOpenChanged`).
  6. `ToolbarLifecycleCoordinator` forwards the adapter's `VisibilityChanged` as `ToolbarShownDuringGuide` (Task 9's Step 2 adds the event; the lifecycle raises it only while `_modal.GuideOpen` — simpler: raise always, the coordinator filters).

- [ ] **Step 6: Update the wiring tests**

- [ ] **Step 6: Update the wiring tests** — `SafetySetupWiringTests` (new seams: `FirstRunGuideRequestedMessage` registration, per-run factory present, no `SafetySetupLaunch.Run`/`SafetySetupHost` strings) + `GeneralPageSourceTests` (`g_guide` replaces the `g_safety` label/parameter).

- [ ] **Step 7: Run everything + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → PASS (full non-UI gate).

```bash
git -C .worktrees/first-run-guide add -A
git -C .worktrees/first-run-guide commit -m "feat(guide): per-run raise, claims lifecycle, retire the safety-setup machinery, g_guide row"
```

---

### Task 13: Final gates

- [ ] **Step 1:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → 0 failed.
- [ ] **Step 2:** `dotnet test tests/EyeCtrl.ViewModels.Tests` → 0 failed. `dotnet test tests/EyeCtrl.Core.Tests` → 0 failed.
- [ ] **Step 3:** `dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"` (non-launching STA subset runs headless-gated) → 0 failed.
- [ ] **Step 4:** `python -I tools/strings_validate.py` → clean; `python -I tools/kb_log_metrics_test.py` → clean (nothing changed, sanity).
- [ ] **Step 5:** `dotnet build EyeCtrl.slnx` → 0 errors.

---

## Self-review notes (fixes applied during planning)

1. **Safety-choice forwarding** — the chain's real `ModalChoice.Action` is forwarded: the surface captures the chain's choices in `FirstRunGuideViewModel.ShowSafetyCard` (Task 6), the flow exposes `ForwardSafetyChoice(Action)` (Task 5), and the tests invoke the recorded choices exactly like the existing `SafetySetupFlowTests` do. An early draft invented an `AnswerForTests` seam — it is gone.
2. **Flow close idempotence** — `Close()` guards on `_closed` (Task 5): the window's `Closed` event re-enters after `surface.Close()`, and `Closed` must raise exactly once.
3. **Restore vs release are distinct** — `SetGuideHold(false)` only lifts suppressions; the guide's `Restore` additionally calls `ToolbarLifecycleCoordinator.ForceShow()` (existing method: adapter `ForceShow` + controller `ResetToVisible`) so every mode gets its toolbar back, per r4 F5.
4. **Overlay inventory** — the new window lands in `docs/design/overlay-unification-spec.md` §3.1 + the D17 map (Task 7 Step 3), or `OverlayInventoryDriftTests` fails the build.
5. **VM telemetry conventions** — the guide VM takes the sink on its single constructor and assigns a `DwellContext` (Task 6); `EveryDwellOverlaySubclass_*` pins stay green (Task 6 Step 4's run).
6. **Step-5 success observation** — the adapter's `VisibilityChanged` is forwarded as `ToolbarLifecycleCoordinator.ToolbarVisibilityChanged` (Task 9); the guide wiring filters `visible == true` and calls `NotifyTaskSucceeded()` — no new polling seam.
7. **Spec coverage** — §4 (gate/flag/re-run: Tasks 4, 12), §5 (seven claims: Tasks 1, 8, 9, 10, 11, 12), §6 (flow/surface/lifecycle: Tasks 5, 6, 12), §7 steps 1–8 (Tasks 4, 5, 6, 7, 12), §8 (data flow: Tasks 6, 12), §9 edge cases (Tasks 8–12 + tests), §12 test strategy (per-task tests + Task 13 gates), §11 file list (every row appears in a task). No known gaps.
