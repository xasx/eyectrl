# Dwell-Completion Audio Cue — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Play a soft confirmation tone when a dwell activates on any EyeCtrl surface except the typing family, gated by a new Dwell-tab toggle (default off).

**Architecture:** `DwellOverlayViewModelBase` (plus five direct-detector stragglers) publishes an empty `DwellActivationMessage` through the messenger **after** the surface's effect ran; a new ctor-subscribing `DwellCueService` singleton (factory-registered, eagerly resolved) plays `Assets/dwell-cue.wav` when `DwellSettings.CueEnabled` is true. Spec: `docs/superpowers/specs/2026-09-23-dwell-audio-cue-design.md` (rev 3, `c3f717e5`) — read it first; every mechanism here cites it.

**Tech Stack:** .NET 10 WPF (`net10.0-windows` app, `EyeCtrl.ViewModels` MVVM layer), CommunityToolkit.Mvvm `WeakReferenceMessenger`, `System.Media.SoundPlayer`, xUnit v3 + NFluent 3.1.0, Python 3 for the one-shot wav generator.

## Global Constraints

- Work ONLY in the existing worktree `.worktrees/dwell-audio-cue` (branch `feat/dwell-audio-cue`, base `c9225e2c`). **Never push** — the owner decides. cwd resets between shells: `cd` first or use absolute paths.
- Assertions: **NFluent** (`Check.That(...)`) — global using exists; inclusive compares are `IsGreaterOrEqualThan`/`IsLessOrEqualThan`; `IsCloseTo` is ABSOLUTE delta.
- No mocking library — private nested/sealed fake classes only.
- File-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- Test namespaces mirror source namespaces. Each task's tests run with the exact `dotnet test` command given.
- Don't run two test suites at once on this box (machine-global named pipes / AppFixture seizure).
- Every publish site obeys the two spec invariants: **boundary rule** (publish where the detector event arrives — never inside dispatch methods `SimulateActivated`/mouse fallbacks share) and **post-effect rule D8** (publish after the effect ran; the settings dispatch is handled-gated).
- GitNexus gates: impact was pre-run on `DwellOverlayViewModelBase` — **HIGH (24 impacted)**, disposition: additive wrapper + protected virtual property, no signature changes; the 22 d=1 entries are exactly the 11 base VMs + their ctors, the ScrollModeController reach is indirect via the process graph. Re-run `detect_changes` before the final commit (Task 10).
- **Never launch the app** (owner consent gate); no UI tests that launch the real app.

---

### Task 1: `DwellActivationMessage` + base-class wrapper

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
  Expected: compile errors (`DwellActivationMessage` not defined, no `AnnounceDwellActivation`). If it compiles, all four facts FAIL.

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

In `ScrollOverlayViewModel.OnTargetActivated`'s class and `ManualSelectionOverlayViewModel` add (with a one-line comment naming the empty handler as the reason):

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

- [ ] **Step 1: Write the failing tests** — create `tests/EyeCtrl.ViewModels.Tests/SettingsWindowDwellActivationTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
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
        public bool CueEnabledAtSend { get; private set; }
        public Func<bool>? ExtraFlagAtSend { get; set; }
        public void Receive(DwellActivationMessage message)
        {
            Count++;
            if (ExtraFlagAtSend is not null) CueEnabledAtSend = ExtraFlagAtSend();
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

    private static void LookAt(SettingsWindowViewModel vm, double x, double y, int samples = 20)
    {
        for (var i = 1; i <= samples; i++)
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(x, y), i * 2_000));
    }

    [Fact]
    public void ControlActivation_RunsTheRegisteredAction_ThenPublishes()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        probe.ExtraFlagAtSend = () => actionRan;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);

        LookAt(vm, 50, 50);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.CueEnabledAtSend).IsTrue();
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

        LookAt(vm, 50, 50);

        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void SimulateActivated_MouseFallbackPath_PublishesNothing()
    {
        var (vm, probe) = Create();
        var actionRan = false;
        vm.Register("c1", DwellCategory.Control, 30, 25, () => actionRan = true);

        vm.SimulateActivated("c1", DwellCategory.Control);

        Check.That(actionRan).IsTrue();
        Check.That(probe.Count).IsEqualTo(0);
    }

    [Fact]
    public void DwellingTheCueToggleItself_PublishesAfterTheSetLands()
    {
        var (vm, probe) = Create();
        probe.ExtraFlagAtSend = () => _settings.Dwell.CueEnabled;
        // The d_cue_sound row's activation shape: a Control action that flips CueEnabled.
        vm.Register("c1", DwellCategory.Control, 30, 25, () => _settings.Dwell.CueEnabled = true);

        LookAt(vm, 50, 50);

        Check.That(probe.Count).IsEqualTo(1);
        Check.That(probe.CueEnabledAtSend).IsTrue();
    }
}
```

Note: `FakeQuickActionsStore`/`FakeInstalledAppProvider` already exist in the test project (`SettingsWindowViewModelTests.cs:20-21` uses them). The reflection field name `_controlActions` — verify against the source when implementing; the dictionary names appear in the dispatch arms.

- [ ] **Step 2: Run to verify red** — `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsWindowDwellActivationTests"`. Expected: facts 1, 2, 4 FAIL (no publish yet), fact 3 PASSes already (nothing publishes anywhere) — that fact is the regression pin, note it as such.

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

- [ ] **Step 4: Run to verify green** — Step 2 command; expected 4/4 PASS. Then the whole suite: `dotnet test tests/EyeCtrl.ViewModels.Tests`.

- [ ] **Step 5: Commit** — `git commit -am "feat(cue): settings dwell dispatch handled-gated, publishes on handled activations"`

---

### Task 3: `CueEnabled` setting + Dwell row + generalized drift guard

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs` (property)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/DwellPageSource.cs` (row + reset)
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsPageResetDriftGuard.cs` (extracted shared guard)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/KeyboardPageSourceResetDriftTests.cs` (refactor onto the shared guard; assertions and doc comments unchanged in meaning)
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/DwellPageSourceResetDriftTests.cs`

**Interfaces (produced):** `DwellSettings.CueEnabled` (bool, default false) — Task 5's service gates on it.

- [ ] **Step 1: Failing tests first.** Create `DwellPageSourceResetDriftTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Dwell tab's "Reset Dwell Settings" button — same machinery as the
/// Keyboard guard (issue #157 class), extracted into SettingsPageResetDriftGuard so any page
/// source with a *_reset button can use it (spec §Settings; General pending #164, other eight
/// deferred — see the one-time census note in the plan).
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

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellPageSourceResetDriftTests"`. Expected: compile error (`SettingsPageResetDriftGuard` missing).

- [ ] **Step 3: Extract the shared guard.** Create `SettingsPageResetDriftGuard.cs` by MOVING the body of `KeyboardPageSourceResetDriftTests.ResetKeyboard_OnConfirmedModal_RestoresEverySettingsRowOnThePage` into:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// The #157 drift-guard machinery, parameterized over a page source (spec §Settings). Kept as
/// one file so the perturb/read/kind-exemption logic has exactly one home. The spec-kind
/// reflection gate stays in the Keyboard test file (it is page-agnostic already).
/// </summary>
internal static class SettingsPageResetDriftGuard
{
    internal static void ResetRestoresEverySettingsRow(
        Func<TestSettingsPageContext, IReadOnlyList<PageSpec>> build,
        string resetButtonId, string pageName)
    {
        var expected = DefaultViewByRowId(build);

        var ctx = TestSettingsPageContext.Build(showModal: (_, act) => act());
        var rows = build(ctx).SelectMany(p => p.Rows).ToList();

        Check.WithCustomMessage(
            $"No settings rows were enumerated from {pageName} — the guard would pass vacuously.")
            .That(rows.Where(IsSettingsRow).ToList()).Not.IsEmpty();

        foreach (var row in rows.Where(IsSettingsRow))
        {
            Perturb(row);
            Check.WithCustomMessage(
                $"Perturbing '{row.Id}' did not change the value its own Get reads — a dead or "
                + "mis-wired setter, so this row's reset is not actually under test.")
                .That(Read(row)).Not.IsEqualTo(expected[row.Id]);
        }

        rows.OfType<ButtonSpec>().Single(b => b.Id == resetButtonId).OnActivate();

        var drifted = build(ctx).SelectMany(p => p.Rows)
            .Where(IsSettingsRow)
            .Select(r => (Id: r.Id, Got: Read(r)))
            .Where(x => !expected.TryGetValue(x.Id, out var def) || !Equals(x.Got, def))
            .Select(x => expected.TryGetValue(x.Id, out var def)
                ? $"{x.Id}: still {x.Got}, default {def}"
                : $"{x.Id}: present after reset but absent from a fresh default page build")
            .ToList();

        Check.WithCustomMessage(
            $"{resetButtonId} must restore every settings row on the {pageName} page to its "
            + "default; these rows kept their perturbed value (add the setting to the reset "
            + "handler): " + string.Join("; ", drifted))
            .That(drifted).IsEmpty();
    }

    // SettingsKinds / NotASettingsKind / IsSettingsRow / Read / Perturb / DefaultViewByRowId
    // move here VERBATIM from KeyboardPageSourceResetDriftTests (DefaultViewByRowId gains the
    // build parameter; its KeyboardPageSource construction moves to the callers).
}
```

Refactor `KeyboardPageSourceResetDriftTests` to call the guard with `ctx => new KeyboardPageSource(ctx).Build()`, `"k_reset"`, `"Keyboard"` — keeping its spec-kind reflection fact where it is. Run the Keyboard tests: must stay green (pure extraction).

- [ ] **Step 4: Run the Dwell guard red.** Step 2 command now compiles and FAILS naming perturbed rows — because `DwellSettings.CueEnabled` and the row don't exist yet the guard perturbs EXISTING rows... it fails only if the reset already misses a row. Read the failure: with no new setting yet, it may PASS vacuously-ish (existing rows all reset correctly — which itself proves the guard generalizes). Proceed to Step 5 which makes it meaningful.

- [ ] **Step 5: Add the setting + row + reset.** In `DwellSettings.cs` (after `DwellCooldownMs`):

```csharp
    /// <summary>Whether a soft confirmation tone plays when a dwell completes on a non-typing
    /// surface (toolbar, menus, settings, etc.). Keys keep their own click sound. Default off
    /// (spec 2026-09-23, ledger D3).</summary>
    [ObservableProperty] private bool _cueEnabled = false;
```

In `DwellPageSource`, add before the `d_reset` ButtonSpec (second PageSpec group), and add the reset line inside `d_reset`'s confirmed-modal action after `d.DwellCooldownMs = def.DwellCooldownMs;`:

```csharp
                new ToggleSpec("d_cue_sound", "Dwell Completion Sound",
                    () => d.CueEnabled, v => { d.CueEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Plays a soft tone when a dwell activates on any EyeCtrl surface — buttons, menus, settings. Keys keep their own click sound." },
```

```csharp
                        d.CueEnabled = def.CueEnabled;
```

- [ ] **Step 6: Run green, then prove teeth.** Step 2 command → PASS. Then delete the `d.CueEnabled = def.CueEnabled;` line, re-run → FAIL naming `d_cue_sound` (teeth proof, spec §Settings), restore the line, re-run → PASS.

- [ ] **Step 7: One-time 11-page census (spec §Settings, review 2 #4).** Add a SCRATCH fact (not committed) enumerating all page sources with a `*_reset` button — `am_`, `ad_`, `av_`, `c_`, `d_`, `dg_`, `ec_`, `g_`, `k_`, `lay_`, `t_` — by constructing each source from `TestSettingsPageContext.Build()` and running the shared guard over its rows. Run it, RECORD the failures (expect at least `g_` per #164), file one GitHub issue per failing page (title: "`X_reset` misses <rows> — drift-guard census finding"), then DELETE the scratch fact. Do not wire the eight deferred pages into the suite.

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

**Interfaces (produced, Task 5 consumes):** `internal static class WavHeader` with `internal static Pcm16Header? TryRead(string path)`; `internal sealed record Pcm16Header(int SampleRateHz, int ChannelCount, int BitsPerSample, long DataByteCount)`.

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
        var peak = 0;
        using (var fs = File.OpenRead(AssetPath()))
        {
            fs.Seek(-header.DataByteCount, SeekOrigin.End);
            var buf = new byte[4_096];
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                for (var i = 0; i + 1 < read; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buf, i)));
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
/// </summary>
internal sealed record Pcm16Header(int SampleRateHz, int ChannelCount, int BitsPerSample, long DataByteCount);

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
                    if (tag != 1) return null; // PCM only — anything else (float32, ADPCM) is rejected by Play()
                    var channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                    var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                    var bits = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);
                    if (bits != 16) return null;
                    var data = FindDataChunk(fs);
                    if (data is null) return null;
                    return new Pcm16Header(rate, channels, bits, data.Value);
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

    private static long? FindDataChunk(FileStream fs)
    {
        Span<byte> chunkHead = stackalloc byte[8];
        while (fs.Read(chunkHead) == 8)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHead[4..8]);
            if (chunkHead[0..4].SequenceEqual("data"u8)) return size;
            fs.Seek(size + (size & 1), SeekOrigin.Current);
        }
        return null;
    }
}
```

- [ ] **Step 4: Create `tools/gen_dwell_cue.py` and run it:**

```python
#!/usr/bin/env python3
"""Generates src/EyeCtrl.App/Assets/dwell-cue.wav — the dwell-completion cue (spec 2026-09-23
§Asset, ledger D6). Parameters are PINNED here and asserted by DwellCueAssetTests; regenerate
only deliberately, together with that test.
16-bit PCM, 44.1 kHz, mono, 50 ms, 880 Hz sine with ~12 ms exponential decay, peak 0.1 (-20 dBFS).
"""
import math
import struct
import wave

SR = 44_100
DUR = 0.050
FREQ = 880.0
TAU = 0.012
PEAK = 0.10  # -20 dBFS

def main() -> None:
    n = int(SR * DUR)
    frames = bytearray()
    for i in range(n):
        t = i / SR
        env = math.exp(-t / TAU)
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

- [ ] **Step 6: Run green** — Step 2 command → PASS (a rebuild copies the asset to test output).

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat(cue): WavHeader PCM16 parser + generated dwell-cue.wav asset + pinned-format test"`

---

### Task 5: `DwellCueService`

**Files:**
- Create: `src/EyeCtrl.App/DwellCueService.cs`
- Test: `tests/EyeCtrl.App.Tests/DwellCueServiceTests.cs` (new)

**Interfaces:** consumes `DwellActivationMessage`, `DwellSettings.CueEnabled`, `WavHeader.TryRead`. Produces `DwellCueService(IMessenger, DwellSettings, string wavPath, Func<string, SoundPlayer?>? playerFactory = null)` + `public void Receive(DwellActivationMessage)` — Task 6 registers it.

- [ ] **Step 1: Failing tests.** Create `tests/EyeCtrl.App.Tests/DwellCueServiceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.IO;
using System.Media;
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

    private sealed class RecordingPlayer : SoundPlayer
    {
        public int Plays { get; private set; }
        public bool ThrowOnPlay { get; set; }
        public override void Play()
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
    public void Disabled_PlaysNothing()
    {
        var settings = new DwellSettings { CueEnabled = false };
        var player = new RecordingPlayer();
        using var svc = new DwellCueService(new WeakReferenceMessenger(), settings,
            WriteValidWav(Path.GetTempPath()), _ => player);

        svc.Receive(new DwellActivationMessage());

        Check.That(player.Plays).IsEqualTo(0);
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
}
```

Note: whether `DwellCueService` is `IDisposable` follows the messenger subscription style you implement in Step 3 — if it registers with `messenger.Register<DwellActivationMessage>(this, ...)`, implement `IDisposable` unregistering (the `using` above then compiles); if it stays probe-only here and subscribes in Task 6's registration, drop `using` in these tests. Pick one and keep Task 6 consistent.

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellCueServiceTests"`. Expected: compile error (type missing).

- [ ] **Step 3: Implement `src/EyeCtrl.App/DwellCueService.cs`:**

```csharp
namespace EyeCtrl.App;

using System.IO;
using System.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Plays the dwell-completion cue (spec 2026-09-23). Subscribes DwellActivationMessage in its
/// ctor, so it MUST be eagerly resolved at startup (App.xaml.cs) — DI singletons are lazy and
/// nothing else depends on it. The wav header is validated by WavHeader, not SoundPlayer.Load()
/// (which does not validate file-path wavs — measured); a Play() failure latches the player to
/// null so a deleted-after-startup file logs once, not once per dwell.
/// </summary>
public sealed class DwellCueService : IRecipient<DwellActivationMessage>
{
    private readonly DwellSettings _settings;
    private readonly IMessenger _messenger;
    private SoundPlayer? _player;

    public DwellCueService(IMessenger messenger, DwellSettings settings, string wavPath,
        Func<string, SoundPlayer?>? playerFactory = null)
    {
        _messenger = messenger;
        _settings = settings;
        // Header first: a corrupt file must become a null player even though the factory
        // (production or test) would happily hand back a SoundPlayer.
        if (WavHeader.TryRead(wavPath) is null) return;
        var factory = playerFactory ?? (p => File.Exists(p) ? new SoundPlayer(p) : null);
        _player = factory(wavPath);
        _messenger.Register(this);
    }

    public void Receive(DwellActivationMessage message)
    {
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
}
```

(`IRecipient<T>` + `messenger.Register(this)` is the toolkit's own subscription pair — no manual unregistration needed for a process-lifetime singleton; adjust the test file's `using` accordingly: drop it, the service is not IDisposable.) Remove the `using var svc =` usings in Step 1's tests to plain `var svc =` before running.

- [ ] **Step 4: Run green** — Step 2 command → 4/4 PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(cue): DwellCueService — header-validated player, enabled-gate, Play-failure latch"`

---

### Task 6: DI wiring (factory registration + eager resolve + wiring test)

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (near the DwellTimeRouter registration, ~line 295)
- Modify: `src/EyeCtrl.App/App.xaml.cs:197-199` (discard-resolve next to DwellTimeRouter)
- Test: `tests/EyeCtrl.App.Tests/Services/DwellCueWiringTests.cs` (new)

**Interfaces:** consumes Task 5's ctor. Produces: the service live in the app's container.

- [ ] **Step 1: Failing wiring test** — create `tests/EyeCtrl.App.Tests/Services/DwellCueWiringTests.cs`, mirroring `DwellRoutingRegistrationWiringTests` (same collection discipline — read its remarks first: `[Collection("DpiHelper Tests (App)")]`, temp app-data, provider disposal):

```csharp
namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The lazy-singleton trap (spec §Mechanism step 4): nothing depends on DwellCueService, so a
/// bare registration never constructs it, it never subscribes, and the feature silently does
/// nothing. Resolves the REAL provider, sends DwellActivationMessage, and observes the handler
/// ran. Fresh settings keep CueEnabled=false, so no real audio path is touched; the temp
/// app-data folder mirrors DwellRoutingRegistrationWiringTests (calibration rewrite hazard).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DwellCueWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("dwell-cue");

    [Fact]
    public void RealContainer_ServiceIsRegistered_AndReceivesTheMessage()
    {
        using var provider = AppBootstrapper.BuildServiceProvider(
            Path.Combine(AppData, "settings.json"), AppData);
        var messenger = provider.GetRequiredService<IMessenger>();
        var before = DwellCueService.HandledCountForTesting;

        messenger.Send(new DwellActivationMessage());

        Check.That(DwellCueService.HandledCountForTesting - before).IsStrictlyGreaterThan(0);
    }
}
```

(Match `AppBootstrapper.BuildServiceProvider`'s actual parameter list as `DwellRoutingRegistrationWiringTests.BuildProvider` calls it — copy that call shape verbatim.)

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellCueWiringTests"`. Expected: compile error or FAIL — `DwellCueService` unresolved in the container / `HandledCountForTesting` missing.

- [ ] **Step 3: Implement.** In `DwellCueService`, add the wiring-observation seam (one line in `Receive`, `internal static long HandledCountForTesting;` — `InternalsVisibleTo EyeCtrl.App.Tests` exists). In `AppBootstrapper` next to the DwellTimeRouter registration block:

```csharp
        // Dwell-completion audio cue (spec 2026-09-23): ctor-subscribes DwellActivationMessage on
        // the messenger, so — like DwellTimeRouter — it must be eagerly resolved at startup
        // (App.xaml.cs); DI singletons are lazy and nothing else depends on it.
        services.AddSingleton(sp => new DwellCueService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<EyeCtrlSettings>().Dwell,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "dwell-cue.wav")));
```

In `App.xaml.cs` immediately after `_ = _serviceProvider.GetRequiredService<DwellTimeRouter>();` (line 197):

```csharp
            // Dwell-completion cue: subscribes in its ctor (see DwellCueService) — must exist
            // before any surface can activate, same reasoning as DwellTimeRouter above.
            _ = _serviceProvider.GetRequiredService<DwellCueService>();
```

- [ ] **Step 4: Run green** — Step 2 command → PASS. Then the full App non-UI suite: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`.

- [ ] **Step 5: Commit** — `git commit -am "feat(cue): factory registration + eager startup resolve + real-container wiring test"`

---

### Task 7: straggler publishes — emoji category bar + eye-care banner

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` (optional trailing `IMessenger?`, lambda publish)
- Modify: `src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs` (same)
- Test: extend `tests/EyeCtrl.App.Tests/Overlays/EmojiGridManagerTests.cs`; create `tests/EyeCtrl.App.Tests/Overlays/EyeCareBannerCueTests.cs`

**Interfaces:** consumes `DwellActivationMessage`. Both classes keep every existing ctor parameter; the new parameter is optional-trailing, so no caller changes (the production `EyeCareBannerWindow` factory in `AppBootstrapper.cs:451` passes none and lands on `WeakReferenceMessenger.Default` — the registered app instance, spec D9).

- [ ] **Step 1: Failing emoji test.** In `EmojiGridManagerTests`, extend `NewManager()` to accept `IMessenger? messenger = null` passed through to the constructor, and add:

```csharp
    [Fact]
    public void CategoryActivation_PublishesDwellActivation_AfterTheHandler()
    {
        var messenger = new WeakReferenceMessenger();
        var (manager, _) = NewManager(messenger);
        var count = 0;
        var probe = new ActionProbe(() => count++);
        messenger.Register(probe);
        manager.BuildCategoryTabs();          // if the existing tests need extra setup (emoji json),
                                               // mirror whichever test drives OnCategoryDwelled today

        DriveCategoryDwell(manager, "_skinTone"); // mirror the existing category-dwell drive helper;
                                                  // "_skinTone" exercises the early-return branch (spec review 2 #3)

        Check.That(count).IsEqualTo(1);
    }

    private sealed record ActionProbe(Action OnReceive) : IRecipient<EyeCtrl.ViewModels.Messages.DwellActivationMessage>
    {
        public void Receive(EyeCtrl.ViewModels.Messages.DwellActivationMessage message) => OnReceive();
    }
```

Before writing the drive helper, READ the existing tests in the file: if one already drives `OnCategoryDwelled`/`_categoryDwellDetector` (public gaze entry or reflection — `KeyboardPredictionExclusionTests` reflection-on-private-field is the sanctioned pattern), mirror it. Assert BOTH branches: category switch (`"smileys"`-shaped id) and `_skinTone`.

- [ ] **Step 2: Run red.** `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~EmojiGridManagerTests"`. Expected: new fact FAILs (no publish, and ctor has no messenger parameter).

- [ ] **Step 3: Implement emoji.** Ctor gains trailing `IMessenger? messenger = null`; field `private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;` (assigned in ctor body); subscription line 97 becomes:

```csharp
        _categoryDwellDetector.TargetActivated += id =>
        {
            OnCategoryDwelled(id);
            _messenger.Send(new DwellActivationMessage());
        };
```

- [ ] **Step 4: Failing EyeCare test.** Create `EyeCareBannerCueTests.cs` — `[Collection("DpiHelper Tests (App)")]` + `WpfApplicationGate.EnsureApplicationWithTokens()` (XAML tokens), constructing `new EyeCareBannerWindow(dwellMs: 25, messenger: probeMessenger)`, registering a target via its public dwell API (mirror how `DwellRoutingRegistrationWiringTests`/`DwellRoutingOwnerRegistrationTests` construct and drive it — read them first), driving an activation, asserting the probe received exactly one message. Include the `messenger:` named argument — the new optional parameter.

- [ ] **Step 5: Run red, implement, run green.** Same lambda-publish change on `_dwell.TargetActivated` in `EyeCareBannerWindow` (ctor gains trailing `IMessenger? messenger = null` → field `_messenger`), then green.

- [ ] **Step 6: Commit** — `git commit -am "feat(cue): emoji category bar + eye-care banner publish DwellActivationMessage (optional messenger, D9)"`

---

### Task 8: straggler publishes — notes + manual selection

**Files:**
- Modify: `src/EyeCtrl.App/Services/NoteManager.cs:45` (lambda publish — already has `IMessenger`)
- Modify: `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs:378` (publish inside continuation)
- Test: extend `tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs` and `tests/EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests.cs`

- [ ] **Step 1: Failing note test.** In `NoteManagerTests` (read its existing construction/drive first — it injects an `IMessenger`), add: a probe on the injected messenger, drive an activation on a registered note target (mirror the existing dwell-drive helper), assert the probe fired once. Include a target id with a malformed suffix (`"note1:zzz"` — the `parts.Length != 2` early return is a no-op that still publishes; that is the accepted no-op-cue trade-off, spec §Pause, worth an assert-comment).

- [ ] **Step 2: Implement note.** Line 45 becomes:

```csharp
        _dwellDetector.TargetActivated += id =>
        {
            OnTargetActivated(id);
            _messenger.Send(new DwellActivationMessage());
        };
```

(and the matching `-=` in `Dispose` becomes `-=` the same lambda? NO — lambdas cannot be unsubscribed: hoist to a private method `OnDetectorTargetActivated(string id)` containing the two calls, subscribe and unsubscribe that name — the Task 1 wrapper pattern.)

- [ ] **Step 3: Failing ManualSelection test.** In `ManualSelectionDwellServiceTests` (read its construction/drive first — it injects `IMessenger` + a `Dispatcher`; mirror the existing dwell-completion test), add two facts: (a) a completed dwell publishes `DwellActivationMessage` (probe counts 1); (b) the torn-down-session case — end the session after the detector fires but before the dispatched continuation runs (the existing tests already exercise this bail-out; mirror how they force it) — publishes NOTHING.

- [ ] **Step 4: Implement ManualSelection.** Inside the `DispatchSafe` continuation, AFTER the `_dwellDetector == null` bail-out and after the existing `_messenger.Send(new ManualSelectionDwellCompletedMessage(...))`:

```csharp
            _messenger.Send(new DwellActivationMessage());
```

- [ ] **Step 5: Both suites green + commit** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~NoteManagerTests|FullyQualifiedName~ManualSelectionDwellServiceTests"`, then full non-UI suite, then
  `git commit -am "feat(cue): notes + manual-selection publish DwellActivationMessage (post-bail-out)"`

---

### Task 9: prediction-bar click (typing family)

**Files:**
- Modify: `src/EyeCtrl.App/KeyboardSoundService.cs` (`PlayKeyClick` → `virtual`)
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:745-757` (`OnPredictionDwelled` → `internal`, click inside guards)
- Test: `tests/EyeCtrl.App.Tests/Keyboard/KeyboardPredictionClickTests.cs` (new)

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
    public void PredictionAccept_PlaysTheKeyClick_InsideTheGuards()
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

- [ ] **Step 2: Run red** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardPredictionClickTests"`. Expected: FAIL (no click call; `OnPredictionDwelled` private → may be compile error, which is the red).

- [ ] **Step 3: Implement.** `KeyboardSoundService.PlayKeyClick` gains `virtual`. `KeyboardWindow.OnPredictionDwelled` becomes `internal` and the click lands inside the guard block:

```csharp
    internal void OnPredictionDwelled(string targetId)
    {
        if (_viewModel == null || !targetId.StartsWith("pred_")) return;

        if (int.TryParse(targetId.AsSpan(5), out var index)
            && index >= 0 && index < _renderedPredictions.Count)
        {
            _viewModel.HandlePredictionSelected(_renderedPredictions[index].Word);
            _soundService?.PlayKeyClick(); // typing family (D1) — after the guards: a stale index is a no-op and must not click
            UpdatePredictions();
        }
    }
```

- [ ] **Step 4: Green + commit** — Step 2 command → PASS; then `git commit -am "feat(cue): prediction accepts join the typing click family (D1), guarded"`.

---

### Task 10: census allowlist + full gates

**Files:**
- Create: `tests/EyeCtrl.App.Tests/DwellSurfaceCensusTests.cs`
- Modify: only if gates surface drift.

- [ ] **Step 1: Write the census test** — dual-keyed (spec §Testing review 2 #5), source-scan via the `RepoRoot()` walk-up pattern from `ToolbarButtonDriftTests.cs:69`:

```csharp
namespace EyeCtrl.App.Tests;

/// <summary>
/// Every dwell surface must be CLASSIFIED for the cue (spec §Testing): cue family / typing
/// family / excluded. Keyed on BOTH mechanisms — every `new DwellDetector(` construction site
/// AND every `DwellContext` enum member (surfaces that hand-roll their dwell, like
/// KeyboardViewModel's key tracker, still declare a context). A new surface that is silent AND
/// unclassified fails here by name; the staleness half fails when an allowlist entry stops
/// matching the code (ToolbarButtonDriftTests pattern).
/// </summary>
public class DwellSurfaceCensusTests
{
    // file-path fragment → (classification, reason). Mirrors the spec's surface table verbatim.
    private static readonly (string Fragment, string Classification, string Reason)[] DetectorSites =
    [
        ("Overlays/EmojiGridManager.cs", "cue", "category bar = navigation (D1)"),
        ("Overlays/KeyboardWindow.xaml.cs", "typing", "prediction bar — click family (D1)"),
        ("Services/ExternalDwellService.cs", "excluded", "external apps — owner declined"),
        ("Overlays/EyeCareBannerWindow.xaml.cs", "cue", "start/snooze exercise actions"),
        ("Services/NoteManager.cs", "cue", "note button actions"),
        ("Services/ManualSelectionDwellService.cs", "cue", "manual-selection dwell clicks"),
        ("ViewModels/SettingsWindowViewModel.cs", "cue", "tabs/controls/pagination/modal"),
        ("ViewModels/DwellOverlayViewModelBase.cs", "cue", "base wrapper — 9 effectful VMs + 2 opt-outs"),
        ("ViewModels/KeyboardViewModel.cs", "typing", "hand-rolled key tracker (KeyboardContext; keys click)"),
    ];

    // DwellContext members that are NOT a user-facing surface of their own.
    private static readonly string[] ContextMembersNotSurfaces = ["Other"];

    private static DirectoryInfo RepoRoot() { /* copy verbatim from ToolbarButtonDriftTests.cs:69 */ }

    [Fact]
    public void EveryDwellDetectorConstructionSite_IsClassified()
    {
        var src = Path.Combine(RepoRoot().FullName, "src");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => File.ReadAllText(f).Contains("new DwellDetector(") ? f : null)
            .Where(f => f is not null).Select(f => f!.Replace('\\', '/')).ToList();

        var classifiedFragments = DetectorSites.Select(s => s.Fragment).ToList();
        var unclassified = found.Where(f => !classifiedFragments.Any(c =>
            f.EndsWith(c.Replace('\\', '/'), StringComparison.Ordinal))).ToList();

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
            .Where(m => !ContextMembersNotSurfaces.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var reasonsByMember = new Dictionary<string, string>
        {
            ["Keyboard"] = "typing — keys click",
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

        var missing = members.Where(m => !reasonsByMember.ContainsKey(m)).ToList();
        Check.WithCustomMessage(
            "New DwellContext member(s) without a cue classification: " + string.Join(", ", missing)
            + " — classify in this census (spec §Testing) and wire the surface's publish or opt-out.")
            .That(missing).IsEmpty();
    }

    [Fact]
    public void TheAllowlist_HasNoStaleEntries()
    {
        var src = Path.Combine(RepoRoot().FullName, "src");
        foreach (var (fragment, _, _) in DetectorSites)
        {
            var path = Path.Combine(src, fragment.Replace('/', Path.DirectorySeparatorChar));
            Check.WithCustomMessage($"Census entry '{fragment}' no longer exists on disk.")
                .That(File.Exists(path)).IsTrue();
        }
    }
}
```

(Reconcile the `DwellContext` member list against `Core/Adaptation/DwellContext.cs` when implementing — the enum read showed exactly these members plus `Other`; a member used by an excluded surface still gets its exclusion reason.)

- [ ] **Step 2: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSurfaceCensusTests"`. Expected: PASS if every site is wired by Tasks 1-9 (this is the point: run it AFTER Task 9). If it names an unclassified file, classify or wire it — that is the census doing its job.

- [ ] **Step 3: Tolerance spot-check (spec §Settings).** Temporarily add a fact writing `{ "dwell": { "cueEnabled": "bogus" } }` to a temp settings file, `SettingsService.Load` it, assert `Dwell.CueEnabled` is `false` and the OTHER dwell settings survived (no section wipe). Then DELETE the fact (it is a one-time verification, recorded here, not a permanent test — or keep it if it reads cleanly beside the existing tolerance tests).

- [ ] **Step 4: Full gates.**
  - `dotnet test tests/EyeCtrl.Core.Tests`
  - `dotnet test tests/EyeCtrl.ViewModels.Tests`
  - `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
  - GitNexus: `node .gitnexus/run.cjs detect-changes --scope all --repo .` from the MAIN checkout with `worktree:` pointing at `.worktrees/dwell-audio-cue` — record the verdict in the task notes; `partial: true` or `truncated: true` is not a clean check, re-run.

- [ ] **Step 5: Commit** — `git commit -am "test(cue): dual-keyed dwell-surface census + tolerance spot-check + full gates"`

---

## Completion checklist (maps to spec sections)

- Behavior table: Tasks 1, 2, 7, 8, 9 (surfaces), 5 (gate).
- Mechanism invariants: Task 1 (boundary + post-effect + wrapper detach), Task 2 (handled-gate), Task 8 (post-bail-out).
- Service + wiring: Tasks 4, 5, 6 (header validation, latch, eager resolve, wiring test with bus isolation).
- Settings: Task 3 (property, row, description via `Description` initializer, reset, generalized guard + teeth, one-time 11-page census → file issues, NOT in KeyboardStackSnapshot — no change needed there).
- Error handling: Task 5 (all four service tests) — caregiver-chime interplay stays a LIVE-verification item at the owner-gated launch (spec §Error handling; do not attempt headless).
- Testing strategy: every bullet has a task; mutation proofs: Task 3 Step 6 (delete-the-line), per-site deletes happen naturally as the tests are written RED first.
