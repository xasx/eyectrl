# Dwell-Completion Audio Cue — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Play a soft confirmation tone when a dwell activates on any EyeCtrl surface except the typing family, gated by a new Dwell-tab toggle (default off).

**Architecture:** `DwellOverlayViewModelBase` (plus five direct-detector stragglers) publishes an empty `DwellActivationMessage` through the messenger **after** the surface's effect ran; a new ctor-subscribing `DwellCueService` singleton (factory-registered, eagerly resolved) plays `Assets/dwell-cue.wav` when `DwellSettings.CueEnabled` is true. Spec: `docs/superpowers/specs/2026-09-23-dwell-audio-cue-design.md` (rev 3, `c3f717e5`) — read it first; every mechanism here cites it.

**Tech Stack:** .NET 10 WPF (`net10.0-windows` app, `EyeCtrl.ViewModels` MVVM layer), CommunityToolkit.Mvvm `WeakReferenceMessenger`, `System.Media.SoundPlayer`, xUnit v3 + NFluent 3.1.0, Python 3 for the one-shot wav generator.

**Plan revision:** rev 2 — external plan review round 1 applied (6 blockers B1–B6, 7 should-fixes S1–S7, 8 gaps G1–G8; all verified against source, `Play()` non-virtual re-measured).

## Global Constraints

- Work ONLY in the existing worktree `.worktrees/dwell-audio-cue` (branch `feat/dwell-audio-cue`, base `c9225e2c`). **Never push, never file GitHub issues, never do anything externally visible** — the owner decides (S6).
- **Never launch the app and never `Show()` any overlay window** (owner consent gate; gaze-only user — a shown window seizes the only input surface). Tests construct windows off-screen/probe-style only, never `Show`/`ShowAsync`.
- Assertions: **NFluent** (`Check.That(...)`) — global using exists; inclusive compares are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`; `IsCloseTo` is ABSOLUTE delta.
- No mocking library — private nested/sealed fake classes only. **`System.Media.SoundPlayer.Play` is NOT virtual** (measured on .NET 10.0.12: `Play`, `PlaySync`, `Load`, `Stop` all `IsVirtual=False`) — never write `override void Play()` on a `SoundPlayer` subclass; fake through interfaces/delegates instead (B1).
- File-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- Test namespaces mirror source namespaces. Each task's tests run with the exact `dotnet test` command given.
- Don't run two test suites at once on this box (machine-global named pipes / AppFixture seizure).
- **Gate baseline (G8):** roughly 13 `AppLaunchTests` fail headless for environmental reasons on this box; `dotnet test` output is German — read the summary counts, not grep hits. Judge tasks by "no NEW failures vs baseline", and record the counts in the task handoff.
- Every publish site obeys the two spec invariants: **boundary rule** (publish where the detector event arrives — never inside dispatch methods `SimulateActivated`/mouse fallbacks share) and **post-effect rule D8** (publish after the effect ran; the settings dispatch is handled-gated).
- GitNexus gates: impact was pre-run on `DwellOverlayViewModelBase` — **HIGH (24 impacted)**, disposition: additive wrapper + protected virtual property, no signature changes; the 22 d=1 entries are exactly the 11 base VMs + their ctors, the ScrollModeController reach is indirect via the process graph. `detect_changes` runs in Task 10 with the compare scope (S7).

---

### Task 1: `DwellActivationMessage` + base-class wrapper

*(Unchanged from plan rev 1 — review round 1 verified all facts have teeth: "Task 1's timing activates exactly once: the gaze enters at 2 ms and fires at 28 ms, and re-arming with cooldown 0 would need 53 ms, beyond the 40 ms of samples. Its order, opt-out and dispose facts all have teeth.")*

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (append record)
- Modify: `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs` (wrapper, virtual, OnDispose)
- Modify: `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs`, `src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs` (opt-out overrides)
- Test: `tests/EyeCtrl.ViewModels.Tests/DwellActivationMessagingTests.cs` (new)

**Interfaces (produced, later tasks rely on these):**
- `EyeCtrl.ViewModels.Messages.DwellActivationMessage` — empty record.
- `DwellOverlayViewModelBase.AnnounceDwellActivation` — `protected virtual bool`, default `true`.
- Base publishes `Messenger.Send(new DwellActivationMessage())` AFTER `OnTargetActivated(targetId)` returns, only when the property is true.

- [ ] **Step 1: Write the failing tests** — create `tests/EyeCtrl.ViewModels.Tests/DwellActivationMessagingTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public class DwellActivationMessagingTests
{
    /// <summary>Records every DwellActivationMessage and what the VM's effect flag said at send time.</summary>
    private sealed class CueProbe(Func<bool> effectFlagAtSend) : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public List<bool> EffectRanAtSend { get; } = [];
        public void Receive(DwellActivationMessage message)
        {
            Count++;
            EffectRanAtSend.Add(effectFlagAtSend());
        }
    }

    private class AnnouncingProbe(IMessenger messenger, Action effect) : DwellOverlayViewModelBase(messenger, dwellCooldownMs: 0)
    {
        public bool EffectRan { get; private set; }
        public void ArmAndLook()
        {
            DwellDetector.AddTarget(new DwellTarget("t1", new PhysicalPoint(50, 50), 30, 25));
            IsVisible = true;
            for (var i = 1; i <= 20; i++)
                Receive(new GazeCursorMovedMessage(new PhysicalPoint(50, 50), i * 2_000));
        }
        protected override void OnTargetActivated(string targetId) { EffectRan = true; effect(); }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    private sealed class SilentProbe(IMessenger messenger) : AnnouncingProbe(messenger, () => { })
    {
        protected override bool AnnounceDwellActivation => false;
    }

    [Fact]
    public void Activation_PublishesTheMessage_AfterTheEffectRan()
    {
        var messenger = new WeakReferenceMessenger();
        var effectRan = false;
        var vm = new AnnouncingProbe(messenger, () => effectRan = true);
        var probe = new CueProbe(() => effectRan);
        messenger.Register(probe);

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.EffectRanAtSend.Count).IsEqualTo(1);
        Check.That(probe.EffectRanAtSend[0]).IsTrue();
    }

    [Fact]
    public void OptedOutSurface_PublishesNothing()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new SilentProbe(messenger);
        var probe = new CueProbe(() => true);
        messenger.Register(probe);

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void DisposedViewModel_PublishesNothing()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new AnnouncingProbe(messenger, () => { });
        var probe = new CueProbe(() => true);
        messenger.Register(probe);
        ((IDisposable)vm).Dispose();

        vm.ArmAndLook();

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void TheAnnounceOptOutSet_IsExactlyTheTwoNoEffectSurfaces()
    {
        // DeclaredOnly: an override declares its own getter; the inherited default does not.
        // Forces any future opt-out to touch this pin consciously (spec Testing).
        var optOuts = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Where(t => t.GetMethod("get_AnnounceDwellActivation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly) is not null)
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Check.That(string.Join(",", optOuts))
            .IsEqualTo("ManualSelectionOverlayViewModel,ScrollOverlayViewModel");
    }
}
```

- [ ] **Step 2: Run to verify red** — `cd .worktrees/dwell-audio-cue && dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~DwellActivationMessagingTests"`
  Expected: compile errors (`DwellActivationMessage` not defined). If it compiles, facts 1/2/3 FAIL.

- [ ] **Step 3: Implement.** In `InteractionMessages.cs` append:

```csharp
/// <summary>
/// Published after a dwell activation's effect has run on an EyeCtrl surface (the overlay VM base
/// class, the settings window, the emoji category bar, the eye-care banner, notes, and manual
/// selection — spec 2026-09-23 §Mechanism). Consumed by the App's DwellCueService for the
/// dwell-completion sound. Post-effect and detector-event-boundary are load-bearing invariants;
/// empty payload by design (ledger D5).
/// </summary>
public record DwellActivationMessage;
```

In `DwellOverlayViewModelBase`: change the ctor subscription `DwellDetector.TargetActivated += OnTargetActivated;` to `DwellDetector.TargetActivated += OnDetectorTargetActivated;`, and in `OnDispose` the matching `-=` line likewise. Add:

```csharp
    /// <summary>
    /// Whether an activation on this surface publishes <see cref="Messages.DwellActivationMessage"/>
    /// (the dwell-completion cue's trigger). Default true — a new effectful surface is covered with
    /// no action. Override with false ONLY where OnTargetActivated is a no-op (no effect to
    /// confirm); a reflection pin in EyeCtrl.ViewModels.Tests forces new opt-outs to be conscious.
    /// </summary>
    protected virtual bool AnnounceDwellActivation => true;

    // D8 (spec): the effect runs FIRST — Messenger.Send is synchronous, so a throwing subscriber
    // must never be able to cost the user their activation — then the cue publishes.
    private void OnDetectorTargetActivated(string targetId)
    {
        OnTargetActivated(targetId);
        if (AnnounceDwellActivation)
            Messenger.Send(new DwellActivationMessage());
    }
```

In `ScrollOverlayViewModel` and `ManualSelectionOverlayViewModel` add (with a one-line comment naming the empty handler as the reason):

```csharp
    protected override bool AnnounceDwellActivation => false;
```

- [ ] **Step 4: Run to verify green** — same command as Step 2. Expected: 4/4 PASS.

- [ ] **Step 5: Full ViewModels suite** — `dotnet test tests/EyeCtrl.ViewModels.Tests`
  Expected: all pass (the 11 existing VM suites must be unaffected — the wrapper preserves order for them).

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(cue): DwellActivationMessage + post-effect publish in DwellOverlayViewModelBase"`

---

### Task 2: settings dispatch handled-gate + publish

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs:89-98` (four lambdas), `:187-250` (dispatch returns `bool`)
- Test: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowDwellActivationTests.cs` (new)

**Interfaces (consumes/produces):** consumes `DwellActivationMessage` (Task 1). The private dispatch becomes `bool` — no external signature changes; `SimulateActivated` behavior unchanged (it bypasses the lambdas, stays silent).

**Geometry note (B2):** `Register` places every target at `(0,0)` (`SettingsWindowViewModel.cs:138`); the real position arrives only via `UpdatePosition`, which every existing test calls. Every fact below MUST call `UpdatePosition` after `Register` or nothing activates.

- [ ] **Step 1: Write the failing tests** — create `tests/EyeCtrl.ViewModels.Tests/SettingsWindowDwellActivationTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsWindowDwellActivationTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    private sealed class CueProbe : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public bool FlagAtSend { get; private set; }
        public Func<bool>? FlagAtSendAccessor { get; set; }
        public void Receive(DwellActivationMessage message)
        {
            Count++;
            if (FlagAtSendAccessor is not null) FlagAtSend = FlagAtSendAccessor();
        }
    }

    private (SettingsWindowViewModel Vm, CueProbe Probe) Create()
    {
        var settingsVM = new SettingsViewModel(_settings);
        var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore(), new FakeInstalledAppProvider());
        var vm = new SettingsWindowViewModel(settingsVM, quickActionsEditor, _messenger, _settings.Dwell);
        var probe = new CueProbe();
        _messenger.Register(probe);
        return (vm, probe);
    }

    private static void LookAt(SettingsWindowViewModel vm) 
    {
        for (var i = 1; i <= 20; i++)
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(50, 50), i * 2_000));
    }

    [Fact]
    public void ControlActivation_RunsTheRegisteredAction_ThenPublishes()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        probe.FlagAtSendAccessor = () => actionRan;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.FlagAtSend).IsTrue();
    }

    [Fact]
    public void StaleControlId_NoRegisteredAction_PublishesNothing()
    {
        var (vm, probe) = Create();
        vm.Register("c1", DwellCategory.Control, 30, 25, () => { });
        // Simulate the stale shape (spec: view teardown removed the action, target left over):
        // the action dictionaries are private, so remove via reflection — the sanctioned probe
        // pattern (KeyboardPredictionExclusionTests reads private detector fields the same way).
        var actions = typeof(SettingsWindowViewModel)
            .GetField("_controlActions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm) as System.Collections.IDictionary;
        actions!.Remove("c1");
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void StaleControlId_TwinWithAction_Publishes()
    {
        // Positive twin of the fact above: identical geometry and drive, action PRESENT.
        // Without this twin, a geometry mistake in the stale fact would pass silently (B2).
        var (vm, probe) = Create();
        vm.Register("c1", DwellCategory.Control, 30, 25, () => { });
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
    }

    [Fact]
    public void StaleModalId_NotAChoiceAndNoAction_PublishesNothing()
    {
        var (vm, probe) = Create();
        // A modal with one real choice; the ghost target id matches neither a choice nor an action.
        vm.ShowModal("Proceed?", [new ModalChoice("go", "Go", () => { })]);
        vm.Register("ghost", DwellCategory.Modal, 30, 25, () => { });
        var actions = typeof(SettingsWindowViewModel)
            .GetField("_modalActions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm) as System.Collections.IDictionary;
        actions!.Remove("ghost");
        vm.UpdatePosition("ghost", DwellCategory.Modal, new PhysicalPoint(50, 50));

        LookAt(vm); // modal visible ⇒ Receive feeds _modalDwell only

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void StaleModalId_TwinRealChoice_Publishes()
    {
        var (vm, probe) = Create();
        vm.ShowModal("Proceed?", [new ModalChoice("go", "Go", () => { })]);
        vm.Register("go", DwellCategory.Modal, 30, 25, () => { });
        vm.UpdatePosition("go", DwellCategory.Modal, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
    }

    [Fact]
    public void SimulateActivated_MouseFallbackPath_PublishesNothing()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        vm.SimulateActivated("c1", DwellCategory.Control);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(0);
    }
}
```

(`FakeQuickActionsStore`/`FakeInstalledAppProvider` already exist — `SettingsWindowViewModelTests.cs:20-22` uses them; `ModalChoice` is the SWVM's public modal-choice record — check its exact constructor shape at `SettingsWindowViewModel.cs:324`'s `ShowModal` and adapt the initializers. The reflection field names `_controlActions`/`_modalActions` appear verbatim in the dispatch arms.)

- [ ] **Step 2: Run to verify red** — `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsWindowDwellActivationTests"`. Expected RED set: `ControlActivation_...` and both `..._Twin...` facts and `StaleModalId_TwinRealChoice` FAIL (nothing publishes yet). The two stale facts PASS pre-change (nothing publishes anywhere yet) — they are mutation pins whose teeth come from their twins; that is deliberate, not a mistake.

- [ ] **Step 3: Implement.** In `SettingsWindowViewModel`, change the dispatch to return `bool` (rename to keep call sites clear):

```csharp
    private bool HandleTargetActivated(string id, DwellCategory category)
```

Arm-by-arm (`return` values; keep every existing statement inside each arm, only add the returns):
- `case DwellCategory.Tab:` — `close` arm: existing `Messenger.Send(new CloseSettingsMessage()); return true;` — pager arm: `if (_tabActions.TryGetValue(...)) { pagerAction.Invoke(); return true; } return false;` — fall-through tab arm: after `ShowTab(id); ActiveTabId = id;` and the optional `_tabActions` invoke: `return true;`
- `case DwellCategory.Pagination:` — `if (id is "pag_prev" or "pag_next") { ...existing...; if (_pagActions.TryGetValue(id, out var pagAction)) pagAction.Invoke(); return true; } if (_pagActions.TryGetValue(id, out var extra)) { extra.Invoke(); return true; } return false;`
- `case DwellCategory.Modal:` — existing body; after `modalAction?.Invoke();` → `return choice is not null || modalAction is not null;` (stale id — neither a choice nor a registered action — is the documented no-op, spec §Pause).
- `case DwellCategory.Control:` — `if (_controlActions.TryGetValue(id, out var controlAction)) { controlAction.Invoke(); return true; } return false;`

Then the four subscription lambdas (lines 89-98) become, one per category:

```csharp
        _tabDwell.TargetActivated += id =>
        {
            if (HandleTargetActivated(id, DwellCategory.Tab))
                Messenger.Send(new DwellActivationMessage());
        };
```

(same shape for `_controlDwell`/Control, `_pagDwell`/Pagination, `_modalDwell`/Modal). `SimulateActivated` keeps calling `HandleTargetActivated` — silent by the boundary rule.

- [ ] **Step 4: Run to verify green** — Step 2 command; expected 6/6 PASS. Then the whole suite: `dotnet test tests/EyeCtrl.ViewModels.Tests`.

- [ ] **Step 5: Commit** — `git commit -am "feat(cue): settings dwell dispatch handled-gated, publishes on handled activations"`

---

### Task 3: `CueEnabled` setting + Dwell row + generalized drift guard + order pin

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs` (property)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs` (row + reset)
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsPageResetDriftGuard.cs` (extracted shared guard)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/KeyboardPageSourceResetDriftTests.cs` (refactor onto the shared guard)
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/DwellPageSourceResetDriftTests.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowDwellActivationTests.cs` (add the publish-order fact — needs `CueEnabled`, so it lands HERE, B3)

**Interfaces (produced):** `DwellSettings.CueEnabled` (bool, default false) — Task 5's service gates on it.

- [ ] **Step 1: Failing tests first.** Create `DwellPageSourceResetDriftTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Dwell tab's "Reset Dwell Settings" button — same machinery as the
/// Keyboard guard (issue #157 class), extracted into SettingsPageResetDriftGuard so any page
/// source with a *_reset button can use it (spec §Settings; General pending #164, other eight
/// deferred — see the one-time census note in this task).
/// </summary>
public class DwellPageSourceResetDriftTests
{
    [Fact]
    public void ResetDwell_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new DwellPageSource(ctx).Build(),
            resetButtonId: "d_reset",
            pageName: "Dwell");
    }
}
```

And append to `SettingsWindowDwellActivationTests` (ViewModels.Tests — the property exists after this task's Step 4):

```csharp
    [Fact]
    public void PublishLands_AfterTheActivationActionsSet()
    {
        // Pins the ORDER property that makes the real d_cue_sound toggle self-demonstrating:
        // the activation's Set runs before the publish, so dwelling the toggle ON cues (the
        // gate reads true by then) and dwelling it OFF is silent. (Renamed from the rev-1
        // "self-demonstrating toggle" title: this fact drives a Control action that flips
        // CueEnabled — the real row's own path is the drift guard's territory, G3.)
        var (vm, probe) = Create();
        probe.FlagAtSendAccessor = () => _settings.Dwell.CueEnabled;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => _settings.Dwell.CueEnabled = true);
        vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(50, 50));

        LookAt(vm);

        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.FlagAtSend).IsTrue();
    }
```

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellPageSourceResetDriftTests"` → compile error (`SettingsPageResetDriftGuard` missing); `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsWindowDwellActivationTests"` → compile error (`CueEnabled` missing).

- [ ] **Step 3: Extract the shared guard.** Create `SettingsPageResetDriftGuard.cs` by MOVING the body of `KeyboardPageSourceResetDriftTests.ResetKeyboard_OnConfirmedModal_RestoresEverySettingsRowOnThePage` into:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// The #157 drift-guard machinery, parameterized over a page source (spec §Settings). Kept as
/// one file so the perturb/read/kind-exemption logic has exactly one home. The spec-kind
/// reflection gate stays in the Keyboard test file (page-agnostic as written); the kind sets
/// are internal here so that file keeps compiling against them (B6).
/// </summary>
internal static class SettingsPageResetDriftGuard
{
    internal static void ResetRestoresEverySettingsRow(
        Func<SettingsPageContext, IReadOnlyList<PageSpec>> build,
        string resetButtonId, string pageName)
    {
        // (body moved verbatim from the Keyboard test; the two constructions of
        // KeyboardPageSource become the build parameter — DefaultViewByRowId gains the same
        // parameter; TestSettingsPageContext.Build(showModal: (_, act) => act()) stays;
        // every Check.WithCustomMessage gains the pageName interpolation)
    }

    // SettingsKinds / NotASettingsKind / IsSettingsRow / Read / Perturb move here VERBATIM,
    // with `private` → `internal` on the two sets (the Keyboard file's spec-kind fact uses them).
}
```

(`TestSettingsPageContext` is a `static class` — it cannot appear in the delegate signature, hence `SettingsPageContext`, B6/CS0718.) Refactor `KeyboardPageSourceResetDriftTests` to call the guard with `ctx => new KeyboardPageSource(ctx).Build()`, `"k_reset"`, `"Keyboard"`. Run the Keyboard tests: must stay green (pure extraction).

- [ ] **Step 4: Add the setting + row + reset.** In `DwellSettings.cs` (after `DwellCooldownMs`):

```csharp
    /// <summary>Whether a soft confirmation tone plays when a dwell completes on a non-typing
    /// surface (toolbar, menus, settings, etc.). Keys keep their own click sound. Default off
    /// (spec 2026-09-23, ledger D3).</summary>
    [ObservableProperty] private bool _cueEnabled = false;
```

In `DwellPageSource`, add before the `d_reset` ButtonSpec (second PageSpec group):

```csharp
                new ToggleSpec("d_cue_sound", "Dwell Completion Sound",
                    () => d.CueEnabled, v => { d.CueEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Plays a soft tone when a dwell activates on any EyeCtrl surface — buttons, menus, settings. Keys keep their own click sound." },
```

and inside `d_reset`'s confirmed-modal action, after `d.DwellCooldownMs = def.DwellCooldownMs;`:

```csharp
                        d.CueEnabled = def.CueEnabled;
```

- [ ] **Step 5: Run green — and note what green means here (G5).** The Dwell guard passes CLEANLY at this point: `d_reset` already covers all nine current rows and `DwellSettings` has no cross-field guards — that is the expected result, not a vacuous pass (the non-empty-rows check guards vacuity). The guard's teeth for THIS feature are proven in Step 6. Also re-run the ViewModels filter from Step 2 → 7/7 PASS.

- [ ] **Step 6: Prove teeth.** Delete the `d.CueEnabled = def.CueEnabled;` line, re-run the Dwell guard → FAIL naming `d_cue_sound` (spec §Settings), restore the line, re-run → PASS.

- [ ] **Step 7: One-time 11-page census (spec §Settings, review 2 #4; S6: results go to the owner, no issue filing).** Add a SCRATCH fact (not committed) enumerating all page sources with a `*_reset` button — `am_`, `ad_`, `av_`, `c_`, `d_`, `dg_`, `ec_`, `g_`, `k_`, `lay_`, `t_` — constructing each source from `TestSettingsPageContext.Build()` and running the shared guard over its rows. Run it, RECORD the failures (expect at least `g_` per #164) as one line per failing page in the task handoff, DELETE the scratch fact, and hand the list to the OWNER to file issues. Do not wire the eight deferred pages into the suite.

- [ ] **Step 8: Suites + commit.** `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` and `dotnet test tests/EyeCtrl.Core.Tests`. Then:
  `git add -A && git commit -m "feat(cue): DwellSettings.CueEnabled + d_cue_sound row + reset-guard machinery generalized (Keyboard+Dwell)"`

---

### Task 4: `WavHeader` parser + generated asset + generator script

**Files:**
- Create: `src/EyeCtrl.App/WavHeader.cs`
- Create: `tools/gen_dwell_cue.py`
- Create (generated): `src/EyeCtrl.App/Assets/dwell-cue.wav`
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj` (after the `click.wav` Content block, line ~55)
- Test: `tests/EyeCtrl.App.Tests/DwellCueAssetTests.cs` (new)

**Interfaces (produced, Task 5 consumes):** `internal static class WavHeader` with `internal static Pcm16Header? TryRead(string path)`; `internal sealed record Pcm16Header(int SampleRateHz, int ChannelCount, int BitsPerSample, long DataByteCount, long DataOffset)` — the offset lets the peak scan work regardless of chunk order after `data` (G2).

- [ ] **Step 1: Failing asset test.** Create `tests/EyeCtrl.App.Tests/DwellCueAssetTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.IO;
using EyeCtrl.App;

/// <summary>
/// Pins the committed cue asset's format and level (spec §Asset). The RIFF parse carries the
/// check — SoundPlayer.Load() is NOT asserted: measured 2026-09-24, Load() succeeds on garbage
/// bytes for a file-path player (the header is only validated in Play()).
/// </summary>
public class DwellCueAssetTests
{
    private static string AssetPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dwell-cue.wav");

    [Fact]
    public void TheCommittedCue_IsQuietShortPcm16_44k1()
    {
        var header = WavHeader.TryRead(AssetPath());

        Check.WithCustomMessage("dwell-cue.wav is missing from the test output — check the csproj Content include.")
            .That(header).IsNotNull();
        Check.That(header!.SampleRateHz).IsEqualTo(44_100);
        Check.That(header.ChannelCount).IsEqualTo(1);
        Check.That(header.BitsPerSample).IsEqualTo(16);

        var seconds = (double)header.DataByteCount / (header.SampleRateHz * header.ChannelCount * 2);
        Check.That(seconds).IsGreaterThan(0.03);
        Check.That(seconds).IsLessThan(0.08);

        // Peak ≤ −20 dBFS (amplitude 0.1): amplitude IS the volume — SoundPlayer has none (D4).
        // Seek by the RETURNED offset (G2): 'data' need not be the last chunk for other tools.
        var peak = 0;
        using (var fs = File.OpenRead(AssetPath()))
        {
            fs.Seek(header.DataOffset, SeekOrigin.Begin);
            var remaining = header.DataByteCount;
            var buf = new byte[4_096];
            int read;
            while (remaining > 0 && (read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i + 1 < read; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buf, i)));
                remaining -= read;
            }
        }
        Check.That(peak).IsLessOrEqualThan((int)(0.1 * 32_767) + 1);
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellCueAssetTests"`. Expected: compile error (`WavHeader` missing).

- [ ] **Step 3: Implement `WavHeader.cs`:**

```csharp
namespace EyeCtrl.App;

using System.Buffers.Binary;
using System.IO;

/// <summary>
/// Parses just enough RIFF/WAVE to decide whether System.Media.SoundPlayer can play the file.
/// SoundPlayer.Load() does NOT validate a file-path wav — measured 2026-09-24: garbage bytes
/// load "successfully" and the header is first checked inside Play(), which throws on the
/// calling thread. The cue service validates here instead (spec §Mechanism step 4).
/// WAVE_FORMAT_EXTENSIBLE (0xFFFE) is deliberately rejected: SoundPlayer's own Play() path
/// only accepts classic PCM on .NET — a file this parser rejects would throw there.
/// </summary>
internal sealed record Pcm16Header(int SampleRateHz, int ChannelCount, int BitsPerSample, long DataByteCount, long DataOffset);

internal static class WavHeader
{
    /// <summary>A PCM (format tag 1) 16-bit RIFF/WAVE file's header, or null for anything else.</summary>
    internal static Pcm16Header? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> riff = stackalloc byte[12];
            if (fs.Read(riff) != 12
                || !riff[0..4].SequenceEqual("RIFF"u8)
                || !riff[8..12].SequenceEqual("WAVE"u8))
                return null;

            Span<byte> chunkHead = stackalloc byte[8];
            while (fs.Read(chunkHead) == 8)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHead[4..8]);
                if (chunkHead[0..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16) return null;
                    Span<byte> fmt = stackalloc byte[16];
                    if (fs.Read(fmt) != 16) return null;
                    var tag = BinaryPrimitives.ReadInt16LittleEndian(fmt[0..2]);
                    if (tag != 1) return null; // PCM only (see class doc for EXTENSIBLE)
                    var channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                    var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                    var bits = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);
                    if (bits != 16) return null;
                    // WAVEFORMATEX carries extra bytes (cbSize etc.) — skip the REST of the
                    // chunk before hunting 'data', or the hunt starts mid-chunk (G1).
                    fs.Seek((size - 16) + (size & 1), SeekOrigin.Current);
                    var data = FindDataChunk(fs, out var dataOffset);
                    if (data is null) return null;
                    return new Pcm16Header(rate, channels, bits, data.Value, dataOffset);
                }
                fs.Seek(size + (size & 1), SeekOrigin.Current); // chunks are word-aligned
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static long? FindDataChunk(FileStream fs, out long dataOffset)
    {
        Span<byte> chunkHead = stackalloc byte[8];
        while (fs.Read(chunkHead) == 8)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHead[4..8]);
            if (chunkHead[0..4].SequenceEqual("data"u8))
            {
                dataOffset = fs.Position;
                return size;
            }
            fs.Seek(size + (size & 1), SeekOrigin.Current);
        }
        dataOffset = 0;
        return null;
    }
}
```

- [ ] **Step 4: Create `tools/gen_dwell_cue.py` and run it** (2.5 ms linear attack per G7 — a sine starting at full envelope measures as a tick, not a tone; decide tone shape HERE because the asset test pins the bytes):

```python
#!/usr/bin/env python3
"""Generates src/EyeCtrl.App/Assets/dwell-cue.wav — the dwell-completion cue (spec 2026-09-23
§Asset, ledger D6). Parameters are PINNED here and asserted by DwellCueAssetTests; regenerate
only deliberately, together with that test.
16-bit PCM, 44.1 kHz, mono, 50 ms, 880 Hz sine, 2.5 ms linear attack, ~12 ms exponential decay,
peak 0.1 (-20 dBFS). The attack is what makes it read as a soft tone rather than a tick (plan G7).
"""
import math
import struct
import wave

SR = 44_100
DUR = 0.050
FREQ = 880.0
ATTACK = 0.0025
TAU = 0.012
PEAK = 0.10  # -20 dBFS

def main() -> None:
    n = int(SR * DUR)
    frames = bytearray()
    for i in range(n):
        t = i / SR
        attack = min(1.0, t / ATTACK) if ATTACK > 0 else 1.0
        env = attack * math.exp(-t / TAU)
        sample = PEAK * env * math.sin(2 * math.pi * FREQ * t)
        frames += struct.pack("<h", int(round(sample * 32_767)))
    with wave.open("src/EyeCtrl.App/Assets/dwell-cue.wav", "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(bytes(frames))
    print(f"wrote src/EyeCtrl.App/Assets/dwell-cue.wav ({len(frames)} data bytes)")

if __name__ == "__main__":
    main()
```

Run from the worktree root: `python tools/gen_dwell_cue.py` (creates the wav).

- [ ] **Step 5: csproj Content block** (after the `click.wav` block at `EyeCtrl.App.csproj:51-55`):

```xml
    <Content Include="Assets\dwell-cue.wav">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
      <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
    </Content>
```

- [ ] **Step 6: Run green** — Step 2 command → PASS (a rebuild copies the asset to test output — verified: `click.wav` reaches the App.Tests bin the same way).

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat(cue): WavHeader PCM16 parser + generated dwell-cue.wav asset + pinned-format test"`

---

### Task 5: `DwellCueService`

**Files:**
- Create: `src/EyeCtrl.App/DwellCueService.cs`
- Test: `tests/EyeCtrl.App.Tests/DwellCueServiceTests.cs` (new)

**Interfaces:** consumes `DwellActivationMessage`, `DwellSettings.CueEnabled`, `WavHeader.TryRead`. Produces `DwellCueService(IMessenger, DwellSettings, string wavPath, Func<string, ICuePlayer?>? playerFactory = null)` implementing `IRecipient<DwellActivationMessage>` and `IDisposable` (S3: container disposal unregisters from the messenger — the teardown-isolation the spec requires), plus `internal long HandledCount { get; private set; }` (instance, bumped BEFORE the enabled gate — B4c) and the `internal interface ICuePlayer { void Play(); }` seam (B1: `SoundPlayer.Play` is not virtual — measured).

- [ ] **Step 1: Failing tests.** Create `tests/EyeCtrl.App.Tests/DwellCueServiceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.ViewModels.Messages;
using EyeCtrl.Core.Settings;

public class DwellCueServiceTests
{
    /// <summary>Canonical 44-byte PCM16 wav + a few zero samples — valid for WavHeader.</summary>
    private static string WriteValidWav(string dir)
    {
        var path = Path.Combine(dir, $"ok_{Guid.NewGuid():N}.wav");
        using var fs = File.Create(path);
        fs.Write("RIFF"u8);
        fs.Write(new byte[] { 0x24, 0, 0, 0 }); // 36 + payload
        fs.Write("WAVE"u8);
        fs.Write("fmt "u8); fs.Write(new byte[] { 16, 0, 0, 0 });
        fs.Write(new byte[] { 1, 0 });          // PCM
        fs.Write(new byte[] { 1, 0 });          // mono
        fs.Write(new byte[] { 0x44, 0xAC, 0, 0 }); // 44100
        fs.Write(new byte[] { 0x88, 0x58, 0x01, 0 }); // byte rate
        fs.Write(new byte[] { 2, 0 });          // block align
        fs.Write(new byte[] { 16, 0 });         // bits
        fs.Write("data"u8); fs.Write(new byte[] { 8, 0, 0, 0 });
        fs.Write(new byte[8]);                  // 4 zero samples
        return path;
    }

    /// <summary>ICuePlayer fake — SoundPlayer.Play is NOT virtual, so the seam is the interface (B1).</summary>
    private sealed class RecordingPlayer : ICuePlayer
    {
        public int Plays { get; private set; }
        public bool ThrowOnPlay { get; set; }
        public void Play()
        {
            Plays++;
            if (ThrowOnPlay) throw new InvalidOperationException("simulated device failure");
        }
    }

    [Fact]
    public void Enabled_CompletesActivation_Plays()
    {
        var settings = new DwellSettings { CueEnabled = true };
        var player = new RecordingPlayer();
        using var svc = new DwellCueService(new WeakReferenceMessenger(), settings,
            WriteValidWav(Path.GetTempPath()), _ => player);

        svc.Receive(new DwellActivationMessage());

        Check.That(player.Plays).IsEqualTo(1);
    }

    [Fact]
    public void Disabled_PlaysNothing_ButStillCountsHandled()
    {
        var settings = new DwellSettings { CueEnabled = false };
        var player = new RecordingPlayer();
        using var svc = new DwellCueService(new WeakReferenceMessenger(), settings,
            WriteValidWav(Path.GetTempPath()), _ => player);

        svc.Receive(new DwellActivationMessage());

        Check.That(player.Plays).IsEqualTo(0);
        Check.That(svc.HandledCount).IsEqualTo(1); // counted BEFORE the gate (B4c)
    }

    [Fact]
    public void GarbageWav_NeverConstructsAPlayer()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(bad, new byte[200]); // measured: SoundPlayer.Load() accepts this
        var factoryCalled = false;

        using var svc = new DwellCueService(new WeakReferenceMessenger(),
            new DwellSettings { CueEnabled = true }, bad,
            _ => { factoryCalled = true; return new RecordingPlayer(); });

        svc.Receive(new DwellActivationMessage());

        Check.That(factoryCalled).IsFalse();
    }

    [Fact]
    public void PlayFailure_Latches_PlaysAtMostOnce()
    {
        var settings = new DwellSettings { CueEnabled = true };
        var player = new RecordingPlayer { ThrowOnPlay = true };
        using var svc = new DwellCueService(new WeakReferenceMessenger(), settings,
            WriteValidWav(Path.GetTempPath()), _ => player);

        Check.ThatCode(() => svc.Receive(new DwellActivationMessage())).DoesNotThrow();
        Check.ThatCode(() => svc.Receive(new DwellActivationMessage())).DoesNotThrow();

        Check.That(player.Plays).IsEqualTo(1); // latched: second Receive never reached Play()
    }

    [Fact]
    public void Dispose_UnregistersFromTheMessenger()
    {
        var messenger = new WeakReferenceMessenger();
        var seen = 0;
        var probe = new ActionProbe(() => seen++);
        messenger.Register(probe);
        var settings = new DwellSettings { CueEnabled = true };
        var player = new RecordingPlayer();
        var svc = new DwellCueService(messenger, settings, WriteValidWav(Path.GetTempPath()), _ => player);

        svc.Dispose();
        messenger.Send(new DwellActivationMessage());

        Check.That(player.Plays).IsEqualTo(0); // unregistered — nothing reaches Receive
        Check.That(seen).IsEqualTo(1);         // the unrelated probe still gets it

        messenger.UnregisterAll(probe);
    }

    private sealed record ActionProbe(Action OnReceive) : IRecipient<DwellActivationMessage>
    {
        public void Receive(DwellActivationMessage message) => OnReceive();
    }
}
```

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellCueServiceTests"`. Expected: compile error (types missing).

- [ ] **Step 3: Implement `src/EyeCtrl.App/DwellCueService.cs`:**

```csharp
namespace EyeCtrl.App;

using System.IO;
using System.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>Play-through seam for the cue player. SoundPlayer.Play is not virtual (measured
/// .NET 10.0.12), so tests substitute this interface instead of subclassing SoundPlayer (B1).</summary>
internal interface ICuePlayer
{
    void Play();
}

/// <summary>SoundPlayer-backed ICuePlayer — the production implementation.</summary>
internal sealed class SoundPlayerCuePlayer(string wavPath) : ICuePlayer
{
    private readonly SoundPlayer _player = new(wavPath);
    public void Play() => _player.Play();
}

/// <summary>
/// Plays the dwell-completion cue (spec 2026-09-23). Subscribes DwellActivationMessage in its
/// ctor UNCONDITIONALLY — subscription must not depend on the asset being valid (a null player
/// is already silent) — so it MUST be eagerly resolved at startup (App.xaml.cs via
/// AppBootstrapper.ResolveEagerServices): DI singletons are lazy and nothing else depends on it.
/// The wav header is validated by WavHeader, not SoundPlayer.Load() (which does not validate
/// file-path wavs — measured); a Play() failure latches the player to null so a
/// deleted-after-startup file logs once, not once per dwell. IDisposable: the container disposes
/// singletons, and disposal unregisters from the messenger (spec review 2 #6 teardown isolation).
/// </summary>
public sealed class DwellCueService : IRecipient<DwellActivationMessage>, IDisposable
{
    private readonly DwellSettings _settings;
    private readonly IMessenger _messenger;
    private ICuePlayer? _player;

    /// <summary>Instance-level wiring observation (B4c): static counters race with parallel
    /// test classes; this is read from the resolved instance, bumped BEFORE the enabled gate
    /// so a disabled service still proves it is wired.</summary>
    internal long HandledCount { get; private set; }

    public DwellCueService(IMessenger messenger, DwellSettings settings, string wavPath,
        Func<string, ICuePlayer?>? playerFactory = null)
    {
        _messenger = messenger;
        _settings = settings;
        _messenger.Register(this); // unconditional (B4d): wiring must not depend on the asset
        if (WavHeader.TryRead(wavPath) is null) return;
        var factory = playerFactory ?? (p => File.Exists(p) ? new SoundPlayerCuePlayer(p) : null);
        _player = factory(wavPath);
    }

    public void Receive(DwellActivationMessage message)
    {
        HandledCount++;
        if (!_settings.CueEnabled || _player is null) return;
        try
        {
            _player.Play();
        }
        catch (Exception ex)
        {
            _player = null; // latch (spec §Error handling)
            Log.Warning(ex, "Dwell cue sound failed; cue disabled for this session.");
        }
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
```

- [ ] **Step 4: Run green** — Step 2 command → 5/5 PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(cue): DwellCueService — ICuePlayer seam, header-validated, Play-failure latch, disposable subscription"`

---

### Task 6: DI wiring (factory registration + eager resolve + wiring test)

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (factory registration near DwellTimeRouter ~line 295; NEW `ResolveEagerServices`)
- Modify: `src/EyeCtrl.App/App.xaml.cs:191-199` (extract the discard-resolves into the new method's call)
- Test: `tests/EyeCtrl.App.Tests/Services/DwellCueWiringTests.cs` (new)

**Interfaces:** consumes Task 5's ctor. Produces: the service live in the app's container, eagerly resolved through a path the test exercises (B4a/b).

- [ ] **Step 1: Failing wiring test** — create `tests/EyeCtrl.App.Tests/Services/DwellCueWiringTests.cs`, mirroring `DwellRoutingRegistrationWiringTests` (same collection discipline — read its remarks first: `[Collection("DpiHelper Tests (App)")]`, temp app-data, provider disposal; copy its `BuildProvider` call shape verbatim — `AppBootstrapper.BuildServiceProvider(...)` parameter list as that file calls it):

```csharp
namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The lazy-singleton trap (spec §Mechanism step 4, plan B4): nothing depends on
/// DwellCueService, so a bare registration never constructs it, it never subscribes, and the
/// feature silently does nothing. This test runs the REAL eager-resolution path
/// (AppBootstrapper.ResolveEagerServices — the same method App.xaml.cs calls at startup) over
/// the real container, sends DwellActivationMessage on the container's own messenger, and
/// observes the resolved INSTANCE's HandledCount move. The App.xaml.cs call site itself is
/// pinned by the source-scan fact below. Fresh settings keep CueEnabled=false, so no real
/// audio path is touched; the temp app-data folder mirrors DwellRoutingRegistrationWiringTests
/// (calibration rewrite hazard).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DwellCueWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("dwell-cue");

    [Fact]
    public void RealContainer_EagerResolvePath_ServiceSubscribes()
    {
        using var provider = AppBootstrapper.BuildServiceProvider(
            /* copy the exact arguments from DwellRoutingRegistrationWiringTests.BuildProvider,
               with this class's AppData path */);
        AppBootstrapper.ResolveEagerServices(provider);

        var cue = provider.GetRequiredService<DwellCueService>();
        var before = cue.HandledCount;

        provider.GetRequiredService<IMessenger>().Send(new DwellActivationMessage());

        Check.That(cue.HandledCount - before).IsStrictlyGreaterThan(0);
        // Fresh settings ⇒ CueEnabled=false ⇒ HandledCount moved but nothing played: wiring
        // proven without any audio device dependency.
    }

    [Fact]
    public void AppXamlCs_CallsTheEagerResolvePath()
    {
        // B4b: deleting the startup call would otherwise keep every other fact green.
        var appXamlCs = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src/EyeCtrl.App/App.xaml.cs"));
        Check.WithCustomMessage(
            "App.xaml.cs must call AppBootstrapper.ResolveEagerServices(provider) at startup — "
            + "DI singletons are lazy; without this line DwellCueService never subscribes.")
            .That(appXamlCs.Contains("ResolveEagerServices(")).IsTrue();
    }
}
```

(`SourceScanner.RepoRoot()` is the shared walk-up helper already used by `CornerRadiusClampTests` / `EmojiGridLoggingSourceTests` — use it, don't copy it, G6.)

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellCueWiringTests"`. Expected: compile error (`ResolveEagerServices` missing).

- [ ] **Step 3: Implement.** In `AppBootstrapper`, add the factory next to the DwellTimeRouter registration block:

```csharp
        // Dwell-completion audio cue (spec 2026-09-23): ctor-subscribes DwellActivationMessage on
        // the messenger, so — like DwellTimeRouter — it must be eagerly resolved at startup
        // (ResolveEagerServices); DI singletons are lazy and nothing else depends on it.
        services.AddSingleton(sp => new DwellCueService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<EyeCtrlSettings>().Dwell,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "dwell-cue.wav")));
```

and the extraction (behavior-preserving move of the EXISTING discard-resolves — `ManualSelectionDwellService` at `App.xaml.cs:192`, `DwellTimeRouter` at `:197`, `GeometryRouter` at `:199` — plus the new one):

```csharp
    /// <summary>
    /// Resolves the ctor-subscribing/lazy-trapped singletons that nothing else depends on.
    /// Extracted from App.xaml.cs's discard-resolves so tests can run the SAME startup path
    /// (plan B4) — and so the startup line is one call site to pin.
    /// </summary>
    public static void ResolveEagerServices(IServiceProvider sp)
    {
        // ManualSelectionDwellService subscribes to messenger messages in its ctor (...)
        _ = sp.GetRequiredService<ManualSelectionDwellService>();
        // DwellTimeRouter subscribes the settings provider and dwell registry in its ctor and
        // applies current values immediately, so it must exist before any surface registers.
        _ = sp.GetRequiredService<DwellTimeRouter>();
        _ = sp.GetRequiredService<GeometryRouter>();
        // DwellCueService subscribes DwellActivationMessage in its ctor (see its class doc).
        _ = sp.GetRequiredService<DwellCueService>();
    }
```

(carry over the existing per-line comments verbatim; in `App.xaml.cs` replace the four discard-resolve lines with one `AppBootstrapper.ResolveEagerServices(_serviceProvider);` keeping the surrounding comment context).

- [ ] **Step 4: Run green — then prove the source-scan's teeth (B4b mutation).** Step 2 command → 2/2 PASS. Then temporarily delete the `ResolveEagerServices(` call from `App.xaml.cs` → source-scan fact FAILs → restore → PASS.

- [ ] **Step 5: Full App non-UI suite** — `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (baseline per Global Constraints).

- [ ] **Step 6: Commit** — `git commit -am "feat(cue): factory registration + ResolveEagerServices extraction + real-container wiring test"`

---

### Task 7: straggler publishes — emoji category bar + eye-care banner

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` (optional trailing `IMessenger?`, lambda publish)
- Modify: `src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs` (same)
- Test: extend `tests/EyeCtrl.App.Tests/Overlays/EmojiGridManagerTests.cs`; create `tests/EyeCtrl.App.Tests/Overlays/EyeCareBannerCueTests.cs`

**Interfaces:** consumes `DwellActivationMessage`. Both classes keep every existing ctor parameter; the new parameter is optional-trailing, so no caller changes (the production `EyeCareBannerWindow` factory in `AppBootstrapper.cs:451` passes none and lands on `WeakReferenceMessenger.Default` — the registered app instance, spec D9).

**Test-drive mechanism (S4/S5, named up front):** neither surface has a public "add a dwell target without showing UI" API. Drive BOTH by reflecting the private detector field (`EmojiGridManager._categoryDwellDetector` at `:91`; `EyeCareBannerWindow._dwell` at `:30`), calling `AddTarget(new DwellTarget(id, new PhysicalPoint(50, 50), 30, 25))`, then feeding samples — for the emoji manager via the same public gaze entry the existing tests use (read them; if none, reflect the detector and call `Update` directly), for the banner via its PUBLIC `OnGaze(PhysicalPoint gaze, long timestampUs)` (`:94`). **Never call `ShowAsync`/`Show` on the banner** (targets only exist inside `ShowAsync`, but constructing the window + reflecting the detector needs no Show — S4, never-launch constraint).

- [ ] **Step 1: Failing emoji tests.** In `EmojiGridManagerTests`, extend `NewManager()` to accept `IMessenger? messenger = null` passed through to the constructor, and add TWO facts (S5 — one per branch):

```csharp
    private sealed record CueProbe(Action OnReceive) : IRecipient<EyeCtrl.ViewModels.Messages.DwellActivationMessage>
    {
        public int Count { get; private set; }
        public void Receive(EyeCtrl.ViewModels.Messages.DwellActivationMessage message) { Count++; OnReceive(); }
    }

    private static DwellDetector CategoryDetectorOf(EmojiGridManager manager) =>
        (DwellDetector)typeof(EmojiGridManager)
            .GetField("_categoryDwellDetector", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!;

    private static void DriveCategoryDwell(EmojiGridManager manager, string targetId)
    {
        var detector = CategoryDetectorOf(manager);
        detector.AddTarget(new DwellTarget(targetId, new PhysicalPoint(50, 50), 30, 25));
        for (var i = 1; i <= 20; i++)
            detector.Update(new PhysicalPoint(50, 50), i * 2_000);
    }

    [Fact]
    public void CategorySwitch_PublishesDwellActivation()
    {
        var messenger = new WeakReferenceMessenger();
        var (manager, _) = NewManager(messenger);
        var probe = new CueProbe(() => { });
        messenger.Register(probe);

        DriveCategoryDwell(manager, "cat_smileys"); // category-switch branch

        Check.That(probe.Count).IsEqualTo(1);
    }

    [Fact]
    public void SkinToneSelector_PublishesDwellActivation()
    {
        var messenger = new WeakReferenceMessenger();
        var (manager, _) = NewManager(messenger);
        var probe = new CueProbe(() => { });
        messenger.Register(probe);

        DriveCategoryDwell(manager, "cat__skinTone"); // early-return branch (spec review 2 #3)

        Check.That(probe.Count).IsEqualTo(1);
    }
```

(Adjust the target-id prefix to whatever `OnCategoryDwelled` strips — it reads `targetId[4..]`, so the ids above carry the real 4-char prefix; confirm in `EmojiGridManager.cs:450-465` when implementing.)

- [ ] **Step 2: Run red.** `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~EmojiGridManagerTests"`. Expected: both new facts FAIL (no publish, and the ctor has no messenger parameter).

- [ ] **Step 3: Implement emoji.** Ctor gains trailing `IMessenger? messenger = null`; field `private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;` (assigned in ctor body); subscription line 97 becomes:

```csharp
        _categoryDwellDetector.TargetActivated += OnCategoryDwellActivated;
```

with a private method (hoisted so a future teardown can `-=` it — the lambda-unsubscribe trap):

```csharp
    // Post-effect publish (spec D8): the handler runs first — including its _skinTone
    // early-return — then the cue fires, so no branch of the handler is left silent.
    private void OnCategoryDwellActivated(string targetId)
    {
        OnCategoryDwelled(targetId);
        _messenger.Send(new DwellActivationMessage());
    }
```

- [ ] **Step 4: Failing EyeCare test.** Create `EyeCareBannerCueTests.cs` — `[StaFact]` + `WpfApplicationGate.EnsureApplicationWithTokens()` (XAML tokens) + `[Collection("DpiHelper Tests (App)")]` (gate callers never run concurrently — DwellRoutingRegistrationWiringTests' ruling). Construct `new EyeCareBannerWindow(dwellMs: 25, messenger: probeMessenger)` (the new optional parameter, named), reflect `_dwell`, `AddTarget`, drive via PUBLIC `OnGaze(new PhysicalPoint(50, 50), i * 2_000)` for 20 samples, assert the probe fired exactly once. **No `ShowAsync`, no `Show`** (S4).

- [ ] **Step 5: Run red, implement, run green.** Same hoisted-method publish on `_dwell.TargetActivated` in `EyeCareBannerWindow` (ctor gains trailing `IMessenger? messenger = null` → `_messenger` field; the handler `Resolve(...)` runs first, then the send). Both emoji facts still green. Run the whole `Category=UI`-free App filter for the collection baseline.

- [ ] **Step 6: Commit** — `git commit -am "feat(cue): emoji category bar + eye-care banner publish DwellActivationMessage (optional messenger, D9)"`

---

### Task 8: straggler publishes — notes + manual selection

**Files:**
- Modify: `src/EyeCtrl.App/Services/NoteManager.cs:45` (hoisted-method publish — already has `IMessenger`)
- Modify: `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs:378` (publish inside continuation)
- Test: extend `tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs` and `tests/EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests.cs`

- [ ] **Step 1: Failing note test.** In `NoteManagerTests` (read its existing construction/drive first — it injects an `IMessenger`), add: a probe on the injected messenger, drive an activation on a registered note target (mirror the existing dwell-drive helper), assert the probe fired exactly once. A malformed id (`"note1:zzz"`) still publishes — the handler's `parts.Length != 2` early return is a no-op and the no-op-cue trade-off is ACCEPTED (spec §Pause); **record that as a comment, not an assertion** (G4 — a future handled-gate must not be blocked by a pinned trade-off).

- [ ] **Step 2: Implement note.** Line 45's subscription becomes a hoisted method (lambdas cannot be `-=`'d in `Dispose`, `NoteManager.cs:292`):

```csharp
        _dwellDetector.TargetActivated += OnDetectorTargetActivated;
```

```csharp
    // Post-effect publish (spec D8): the note action runs first, then the cue fires.
    private void OnDetectorTargetActivated(string targetId)
    {
        OnTargetActivated(targetId);
        _messenger.Send(new DwellActivationMessage());
    }
```

and `Dispose`'s `-=` line changes to the same method name.

- [ ] **Step 3: Failing ManualSelection test.** In `ManualSelectionDwellServiceTests` (read its construction/drive first — it injects `IMessenger` + a `Dispatcher`; mirror the existing dwell-completion test), add two facts: (a) a completed dwell publishes `DwellActivationMessage` (probe counts 1); (b) the torn-down-session case — end the session after the detector fires but before the dispatched continuation runs (the existing tests already exercise this bail-out; mirror how they force it) — publishes NOTHING.

- [ ] **Step 4: Implement ManualSelection.** Inside the `DispatchSafe` continuation, AFTER the `_dwellDetector == null` bail-out and after the existing `_messenger.Send(new ManualSelectionDwellCompletedMessage(...))`:

```csharp
            _messenger.Send(new DwellActivationMessage());
```

- [ ] **Step 5: Both suites green + commit** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~NoteManagerTests|FullyQualifiedName~ManualSelectionDwellServiceTests"`, then the full non-UI filter, then
  `git commit -am "feat(cue): notes + manual-selection publish DwellActivationMessage (post-bail-out)"`

---

### Task 9: prediction-bar click (typing family) + key-click robustness

**Files:**
- Modify: `src/EyeCtrl.App/KeyboardSoundService.cs` (`PlayKeyClick` → `virtual`; try/catch + latch — S1)
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:745-757` (`OnPredictionDwelled` → `internal`, click AFTER `UpdatePredictions()`)
- Test: `tests/EyeCtrl.App.Tests/Keyboard/KeyboardPredictionClickTests.cs` (new)

**Scope note (S1, owner-vetoable):** the `KeyboardSoundService` try/catch+latch is a deliberate scope addition — the spec's Error-handling section said "consider applying (a) to KeyboardSoundService in the same change", and the plan review confirmed the key path carries the exact measured corrupt-wav failure (a throw that skips the key effect). Three lines. If the owner vetoes, keep only the call-site reordering.

- [ ] **Step 1: Failing test.** Create the test — `[Collection("WpfApplication")]`, constructing the window via the off-screen `WS_POPUP` HwndSource probe copied from `KeyboardPredictionExclusionTests` (read its window-construction helper first and mirror it verbatim):

```csharp
    private sealed class RecordingSoundService : KeyboardSoundService
    {
        public int Clicks;
        // Base ctor points at a nonexistent wav → null player → the override is the only sound path.
        public RecordingSoundService() : base(new KeyboardSettings(), "no-such-file.wav") { }
        public override void PlayKeyClick() => Clicks++;
    }

    [Fact]
    public void PredictionAccept_PlaysTheKeyClick_AfterTheGuards()
    {
        var window = NewOffscreenWindow();          // mirrored helper
        var recorder = new RecordingSoundService();
        window.SetSoundService(recorder);
        ArrangeOneRenderedPrediction(window, "hello"); // mirrored: InMemoryDictProvider + type + UpdatePredictions

        window.OnPredictionDwelled("pred_0");
        Check.That(recorder.Clicks).IsEqualTo(1);

        window.OnPredictionDwelled("pred_9");        // stale index → no-op must NOT click
        Check.That(recorder.Clicks).IsEqualTo(1);
    }
```

(`KeyboardSoundService.PlayKeyClick` is OUR class — making it `virtual` is fine; the non-virtual one is `SoundPlayer.Play`, B1.)

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardPredictionClickTests"`. Expected: FAIL (no click call; `OnPredictionDwelled` private → possibly compile error, which is the red).

- [ ] **Step 3: Implement.** `KeyboardSoundService`:

```csharp
public class KeyboardSoundService(KeyboardSettings settings, string wavPath)
{
    private SoundPlayer? _player = File.Exists(wavPath) ? new SoundPlayer(wavPath) : null;

    public virtual void PlayKeyClick()
    {
        if (!settings.ClickSoundEnabled || _player is null) return;
        try
        {
            _player.Play();
        }
        catch (Exception ex)
        {
            _player = null; // latch: SoundPlayer validates the wav only in Play() — a corrupt or
                            // deleted-after-startup click.wav must cost one log line, not every key
            Log.Warning(ex, "Key click sound failed; click sound disabled for this session.");
        }
    }
}
```

(needs `using Serilog;`; `SettingsSnapshots`/behavior unchanged). `KeyboardWindow.OnPredictionDwelled` becomes `internal`, click LAST in the guard block (S1: a throw must not skip `UpdatePredictions`, and the no-op guard still must not click):

```csharp
    internal void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index)
            && index >= 0 && index < _renderedPredictions.Count)
        {
            _viewModel.HandlePredictionSelected(_renderedPredictions[index].Word);
            UpdatePredictions();
            _soundService?.PlayKeyClick(); // typing family (D1); AFTER UpdatePredictions so a
                                           // sound failure can never leave the bar stale (S1)
        }
    }
```

- [ ] **Step 4: Green + commit** — Step 2 command → PASS; full non-UI filter (baseline); then
  `git commit -am "feat(cue): prediction accepts join the typing click family (D1); key-click sound failure contained"`.

---

### Task 10: census allowlist + full gates

**Files:**
- Create: `tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs`
- Modify: only if gates surface drift.

- [ ] **Step 1: Write the census test** — dual-keyed (spec §Testing review 2 #5), source-scan via the SHARED `SourceScanner.RepoRoot()` (G6). Fragments carry the project folder (B5); the staleness half asserts the detector construction is still THERE, not just that the file exists. `KeyboardViewModel` appears ONLY in the `DwellContext` half — it has no `DwellDetector` (grep-confirmed) and hand-rolls its tracker:

```csharp
namespace EyeCtrl.App.Tests;

/// <summary>
/// Every dwell surface must be CLASSIFIED for the cue (spec §Testing): cue family / typing
/// family / excluded. Keyed on BOTH mechanisms — every `new DwellDetector(` construction site
/// AND every `DwellContext` enum member (surfaces that hand-roll their dwell, like
/// KeyboardViewModel's key tracker, still declare a context; OverlayTelemetryWiringTests
/// already pins ASSIGNMENT — this census pins the cue CLASSIFICATION). A new surface that is
/// silent AND unclassified fails here by name; the staleness half fails when an allowlist
/// entry stops matching the code (ToolbarButtonDriftTests pattern).
/// </summary>
public class DwellSurfaceCensusTests
{
    // project-rooted path fragment → classification + reason. Mirrors the spec's surface table.
    private static readonly (string Path, string Classification, string Reason)[] DetectorSites =
    [
        ("EyeCtrl.App/Overlays/EmojiGridManager.cs", "cue", "category bar = navigation (D1)"),
        ("EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs", "typing", "prediction bar — click family (D1)"),
        ("EyeCtrl.App/Services/ExternalDwellService.cs", "excluded", "external apps — owner declined"),
        ("EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs", "cue", "start/snooze exercise actions"),
        ("EyeCtrl.App/Services/NoteManager.cs", "cue", "note button actions"),
        ("EyeCtrl.App/Services/ManualSelectionDwellService.cs", "cue", "manual-selection dwell clicks"),
        ("EyeCtrl.ViewModels/SettingsWindowViewModel.cs", "cue", "tabs/controls/pagination/modal"),
        ("EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs", "cue", "base wrapper — 9 effectful VMs + 2 opt-outs"),
    ];

    // DwellContext members that are not a user-facing surface of their own.
    private static readonly string[] ContextMembersNotSurfaces = ["Other"];

    private static readonly Dictionary<string, string> ContextClassification = new(StringComparer.Ordinal)
    {
        ["Keyboard"] = "typing — keys click (hand-rolled tracker, KeyboardViewModel.cs:111)",
        ["KeyboardPrediction"] = "typing — click family (D1)",
        ["ActionMenu"] = "cue — base wrapper",
        ["ManualSelection"] = "cue — MSDS continuation",
        ["Toolbar"] = "cue — base wrapper",
        ["EdgeButtonBar"] = "cue — base wrapper",
        ["QuickActions"] = "cue — base wrapper",
        ["TaskSwitcher"] = "cue — base wrapper",
        ["Emoji"] = "cue — category bar",
        ["Notes"] = "cue — NoteManager",
        ["Scroll"] = "excluded — empty handler, opted out",
        ["DragTrail"] = "cue — base wrapper",
        ["ActivationButton"] = "cue — base wrapper",
        ["Backdrop"] = "cue — base wrapper",
        ["ExternalDwell"] = "excluded — owner declined",
        ["EyeCare"] = "cue — banner",
    };

    [Fact]
    public void EveryDwellDetectorConstructionSite_IsClassified()
    {
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("new DwellDetector("))
            .Select(f => f[(src.Length + 1)..].Replace('\\', '/'))
            .ToList();

        var classified = DetectorSites.Select(s => s.Path).ToList();
        var unclassified = found.Where(f => !classified.Contains(f, StringComparer.Ordinal)).ToList();

        Check.WithCustomMessage(
            "These files construct a DwellDetector but are not in DwellSurfaceCensusTests' "
            + "allowlist — classify the surface (cue/typing/excluded) or wire its publish: "
            + string.Join(", ", unclassified))
            .That(unclassified).IsEmpty();
    }

    [Fact]
    public void EveryDwellContextMember_IsClassified()
    {
        var members = Enum.GetNames<EyeCtrl.Core.Adaptation.DwellContext>()
            .Where(m => !ContextMembersNotSurfaces.Contains(m))
            .OrderBy(m => m, StringComparer.Ordinal).ToList();

        var missing = members.Where(m => !ContextClassification.ContainsKey(m)).ToList();
        Check.WithCustomMessage(
            "New DwellContext member(s) without a cue classification: " + string.Join(", ", missing)
            + " — classify in this census (spec §Testing) and wire the surface's publish or opt-out.")
            .That(missing).IsEmpty();
    }

    [Fact]
    public void TheDetectorAllowlist_HasNoStaleEntries()
    {
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        foreach (var (path, _, reason) in DetectorSites)
        {
            var full = Path.Combine(src, path.Replace('/', Path.DirectorySeparatorChar));
            Check.WithCustomMessage($"Census entry '{path}' ({reason}) no longer exists on disk.")
                .That(File.Exists(full)).IsTrue();
            Check.WithCustomMessage(
                $"Census entry '{path}' ({reason}) no longer constructs a DwellDetector — "
                + "update the classification or remove the entry.")
                .That(File.ReadAllText(full).Contains("new DwellDetector(")).IsTrue();
        }
    }
}
```

(Reconcile the `DwellContext` member list against `Core/Adaptation/DwellContext.cs` when implementing — the enum read showed exactly these members plus `Other`.)

- [ ] **Step 2: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSurfaceCensusTests"`. Expected: PASS if every site is wired by Tasks 1-9 (this is the point: run it AFTER Task 9). If it names an unclassified file, classify or wire it — that is the census doing its job.

- [ ] **Step 3: Tolerance spot-check (spec §Settings).** Temporarily add a fact writing `{ "dwell": { "cueEnabled": "bogus" } }` to a temp settings file, `SettingsService.Load` it, assert `Dwell.CueEnabled` is `false` and the OTHER dwell settings survived (no section wipe). Then DELETE the fact (one-time verification, recorded here, not a permanent test — or keep it if it reads cleanly beside the existing tolerance tests).

- [ ] **Step 4: Full gates (S7).**
  - `dotnet test tests/EyeCtrl.Core.Tests`
  - `dotnet test tests/EyeCtrl.ViewModels.Tests`
  - `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` — compare against the recorded baseline (G8), read summary counts.
  - `detect_changes`: by now Tasks 1-9 are committed, so the working-tree scope sees almost nothing. Run the COMPARE scope against the branch base from the main checkout:
    `node .gitnexus/run.cjs detect-changes --scope compare --base-ref c9225e2c --repo .` (with the worktree parameter per the repo's GitNexus notes; the index lives in the main checkout and has never seen this branch's intermediate states). Treat symbol/risk attribution as ADVISORY (line shifts on a branch the index never indexed), record the verdict in the task notes, and NEVER record a `0`/clean as a pass without `partial: false` and `truncated: false`.

- [ ] **Step 5: Commit** — `git commit -am "test(cue): dual-keyed dwell-surface census + tolerance spot-check + full gates"`

---

## Completion checklist (maps to spec sections)

- Behavior table: Tasks 1, 2, 7, 8, 9 (surfaces), 5 (gate).
- Mechanism invariants: Task 1 (boundary + post-effect + wrapper detach), Task 2 (handled-gate, stale-modal + stale-control with positive twins), Task 8 (post-bail-out).
- Service + wiring: Tasks 4, 5, 6 (header validation, latch, eager resolve via ResolveEagerServices + source-scanned App.xaml.cs call, wiring test with bus isolation and instance HandledCount).
- Settings: Task 3 (property, row, description via `Description` initializer, reset, generalized guard + teeth, one-time 11-page census → results to owner, NOT in KeyboardStackSnapshot — no change needed there; publish-order pin for the toggle).
- Error handling: Task 5 (all five service tests incl. dispose-unregister) + Task 9 (key-click latch, scope-add flagged) — caregiver-chime interplay stays a LIVE-verification item at the owner-gated launch (spec §Error handling; do not attempt headless).
- Testing strategy: every bullet has a task; mutation proofs: Task 3 Step 6 (delete-the-line), Task 6 Step 4 (delete-the-startup-call), per-site deletes happen naturally as the tests are written RED first.
