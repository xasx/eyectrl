# Accent Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship per-layout `accents` sub-layouts with auto-return (spec D1–D17): a 28-key French-in-German grid on QWERTZ, a 20-key tail on AZERTY, plus the auto-return rule, spatial swap-landing dwell suppression (D13), prediction auto-return (D14), system-modifier clearing + modifier-visual repaint on entry (D15/M7), zero-movement row-0 `Acc` placements (D17/AZERTY), and the AZERTY/`ß` capital data fixes.

**Architecture:** Pure layout data plus two small ViewModel rules and one Core property. `SubLayout` gains `AutoReturn` (System.Text.Json camelCase binds `autoReturn` with no loader change). `KeyboardViewModel` gains the auto-return trigger (end of `ActivateKey` and `HandlePredictionSelected`, gated on `!IsEmojiMode`), system-modifier clearing + an unconditional `ActiveModifiers` raise after every swap (D15/H1), and spatial swap suppression anchored to the pre-swap gaze position with a radius derived from the OLD grid's centres (D13 — no post-swap key resolution at all, so unarranged first-sample centres cannot mis-latch). No window code changes; no predictor changes.

**Tech Stack:** .NET 10, WPF, xUnit v3 + NFluent 3.1.0, CommunityToolkit.Mvvm.

**Spec:** `docs/superpowers/specs/2026-09-24-accent-input-design.md` (revision 5). Work in `.worktrees/accent-input` on `feat/accent-input`.
**Plan revision 3:** external plan-review round 1 folded (B1–B3, H1–H3, M1–M4 owner-defaulted, L1–L6 — every claim verified against code first). Dispositions inline, marked ⟦Bn⟧/⟦Hn⟧/⟦Mn⟧/⟦Ln⟧. **Revision 4 (round 2):** I1 grid alignment (spec now carries this plan's 10/10/8 QWERTZ grid), I2 landing-pin fix (post-swap layout pass + gaze update before reading centres) + the AZERTY pin, I3 radius-discriminating jitter pair, I4 collapse-test anchor on `key_0_1`, the re-opened M2/L5 (L5's hide seam = `IsVisible = true` then `HideKeyboardMessage`), the sub-2-centre radius guard, exact-capital pins (`ToUpperInvariant`), QWERTY in the switchSub check, `KeyLabel` via the window's `_keyVisuals` field, and step-accuracy corrections.

## Global Constraints

- NFluent 3.1.0 (`Check.That(...)`), xUnit v3 `[Fact]`/`[Theory]`/`[StaFact]` (STA only where WPF controls are constructed); global usings `Xunit` + `NFluent` come from `tests/Directory.Build.props` — do not add per-file usings for them.
- No mocking library: inline JSON / hand-built layout objects / the existing probe harness. Unique temp dir names per test.
- **ViewModel tests use ONE `new WeakReferenceMessenger()` per test** (house pattern, `KeyboardViewModelTests.cs:12`) — never `WeakReferenceMessenger.Default` ⟦M3⟧.
- File-scoped namespaces, Allman braces, `_camelCase` fields, no `#region`.
- German-locale box: never parse formatted numbers; `dotnet test` output is German (`Fehler!` = fail).
- ONE test project per `dotnet test` invocation; never run two suites at once (shared box).
- **Before the first edit of each symbol a task lists, run the GitNexus MCP `impact` pass (upstream)** — this includes `HandlePredictionSelected` and `ProcessKeyGaze` (Task 3) ⟦L2⟧. Treat UNKNOWN as unresolved; the index is main-checkout-era — confirm surprises with a text search. **Before every commit:** MCP `detect_changes` with `worktree` = `C:\Users\andre\source\repos\eyectrl\.worktrees\accent-input`, plus `git diff --stat` vs the task's file list — that diff is the check that counts.
- Never push. Never launch EyeCtrl.App (STA window tests in Task 6 construct windows, not the app — those are fine).
- The spec's decision ledger D1–D17 is binding; if execution surfaces a conflict with the spec text, stop and ask rather than improvise.
- Key-id reality (pin your assumptions to it): regular keys get POSITIONAL ids `key_{row}_{col}`; special-action keys use their action string as the id (`KeyboardWindow.xaml.cs:236`) ⟦B3⟧.

---

### Task 1: Core — `AutoReturn` on `SubLayout`

**Files:**
- Modify: `src/EyeCtrl.Core/Keyboard/SubLayout.cs`
- Test: `tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutAutoReturnTests.cs` (create)

**Interfaces:**
- Produces: `bool SubLayout.AutoReturn { get; set; }` (JSON `"autoReturn"`, camelCase-bound by the existing `KeyboardLayoutLoader` options — no loader change).

- [ ] **Step 1: GitNexus impact pass on `SubLayout`** (upstream). Expected consumers: `KeyboardLayoutLoader` (deserialize) and layout consumers. Low risk; do not proceed on UNKNOWN without a text search.

- [ ] **Step 2: Write the failing tests** (the switchSub-target rule is NOT tested here — it is covered over the real shipped files in Task 4; an inline fixture testing itself proves nothing ⟦L1⟧):

```csharp
namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>autoReturn rides the existing camelCase System.Text.Json binding (spec D5).</summary>
public class KeyboardLayoutAutoReturnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eyectrl_autoreturn_" + Guid.NewGuid());

    public KeyboardLayoutAutoReturnTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private KeyboardLayout SaveAndLoad(string json)
    {
        var path = Path.Combine(_dir, "layout.json");
        File.WriteAllText(path, json);
        return KeyboardLayoutLoader.Load(path);
    }

    [Fact]
    public void AutoReturn_LoadsTrue_AndDefaultsToFalse()
    {
        var layout = SaveAndLoad("""
        {
          "name": "T", "defaultSubLayout": "letters",
          "subLayouts": {
            "letters": { "label": "ABC", "rows": [ [] ] },
            "accents": { "label": "Acc", "autoReturn": true, "rows": [ [] ] }
          }
        }
        """);

        Check.That(layout.SubLayouts["accents"].AutoReturn).IsTrue();
        Check.That(layout.SubLayouts["letters"].AutoReturn).IsFalse();
    }

    [Fact]
    public void AutoReturn_SurvivesSaveRoundTrip()
    {
        var path = Path.Combine(_dir, "roundtrip.json");
        KeyboardLayoutLoader.Save(SaveAndLoad("""
        {
          "name": "T", "defaultSubLayout": "letters",
          "subLayouts": {
            "letters": { "label": "ABC", "rows": [ [] ] },
            "accents": { "label": "Acc", "autoReturn": true, "rows": [ [] ] }
          }
        }
        """), path);

        Check.That(KeyboardLayoutLoader.Load(path).SubLayouts["accents"].AutoReturn).IsTrue();
    }
}
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~KeyboardLayoutAutoReturn"`
  Expected: FAIL — `AutoReturn` does not exist (compile error).

- [ ] **Step 4: Implement.** In `SubLayout`, after `Rows`:

```csharp
    /// <summary>True when the keyboard returns to the layout's default sub-layout after any
    /// text-emitting key or prediction selection (spec D5). Used by the accents sub-layouts:
    /// an accent is always part of ongoing typing, so the user should land back on letters
    /// without a manual switch. Defaults to false — an unset flag must never change existing
    /// sub-layout behavior.</summary>
    public bool AutoReturn { get; set; }
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 2/2 PASS.

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.Core/Keyboard/SubLayout.cs tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutAutoReturnTests.cs
git commit -m "feat(core): SubLayout.AutoReturn flag (accent-input D5)"
```

### Task 2: ViewModels — auto-return trigger (D5/D14) + D15 clearing + M7 repaint

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (`ActivateKey` :346-360, `HandlePredictionSelected` :488-506, `SwitchSubLayout` :458-463, `ClearOneShotModifiers` region :328)
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardAutoReturnTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's `SubLayout.AutoReturn`.
- Produces (all private): `private void AutoReturnIfNeeded()` called at the end of `ActivateKey` and `HandlePredictionSelected`; `private void ClearSystemOneShotModifiers()` called from `SwitchSubLayout`. `SwitchSubLayout`'s public behavior changes: entering an `autoReturn` sub-layout clears Ctrl/Alt/Win, and EVERY swap raises `ActiveModifiers` after the grid rebuild. Task 3 adds the D13 anchor block inside `SwitchSubLayout` — this task owns the method up to and including the `ActiveSubLayoutId` assignment, the D15/M7 tail, and nothing else.

- [ ] **Step 1: GitNexus impact pass on `KeyboardViewModel.ActivateKey`, `SwitchSubLayout`, AND `HandlePredictionSelected`** (upstream; hub class — expect breadth; the changes are additive call-sites at method ends plus a guarded tail in SwitchSubLayout) ⟦L2⟧.

- [ ] **Step 2: Write the failing tests** (positional key ids where ids matter; one messenger per test ⟦M3⟧):

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;

/// <summary>Spec D5 (auto-return), D14 (prediction selections return too), D15 (system
/// modifiers cleared on entry), M7 (modifier repaint after the swap). The trigger is a
/// NON-SPECIAL key activation — whatever modifiers are armed — or a prediction selection,
/// never while IsEmojiMode (emoji keys carry Output and flow through the same path while
/// emoji mode overlays the text sub-layout).</summary>
public class KeyboardAutoReturnTests
{
    private static KeyboardLayout LayoutWithAccents() => new()
    {
        Name = "T",
        DefaultSubLayout = "letters",
        SubLayouts =
        {
            ["letters"] = new SubLayout
            {
                Label = "ABC",
                Rows = new() { new() { new KeyDefinition { Label = "a", Output = "a" } } },
            },
            ["numbers"] = new SubLayout
            {
                Label = "123",
                Rows = new() { new() { new KeyDefinition { Label = "1", Output = "1" } } },
            },
            ["accents"] = new SubLayout
            {
                Label = "Acc",
                AutoReturn = true,
                Rows = new() { new() { new KeyDefinition { Label = "é", Output = "é", ShiftOutput = "É" } } },
            },
        },
    };

    private static KeyboardViewModel Vm()
        => new(new EyeCtrlSettings(), new WeakReferenceMessenger());

    private static KeyboardViewModel VmOnAccents()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.SwitchSubLayout("accents");
        return vm;
    }

    [Fact]
    public void ActivateKey_OnAutoReturnSubLayout_ReturnsToDefault()
    {
        var vm = VmOnAccents();
        vm.ActivateKey("é", "É");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void TargetIsTheLayoutDefault_NotThePreviousSubLayout()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.SwitchSubLayout("numbers");   // previous = numbers
        vm.SwitchSubLayout("accents");
        vm.ActivateKey("é", "É");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters"); // not "numbers"
    }

    [Fact]
    public void SpecialKeys_DoNotTriggerReturn()
    {
        var vm = VmOnAccents();
        vm.HandleSpace();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
        vm.HandleBackspace();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
        vm.HandleTab();
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
    }

    [Fact]
    public void WhileInEmojiMode_AnEmojiOutput_DoesNotReturn()
    {
        var vm = VmOnAccents();
        vm.IsEmojiMode = true;
        vm.ActivateKey("😀", "😀");   // emoji keys carry Output through the same path
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("accents");
    }

    [Fact]
    public void PredictionSelection_OnAutoReturnSubLayout_ReturnsToDefault()
    {
        var vm = VmOnAccents();
        vm.HandlePredictionSelected("être");
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void EnteringAutoReturnSubLayout_ClearsSystemModifiers_KeepsShiftAndCaps()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.ToggleModifier(ModifierKey.Win);
        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ToggleModifier(ModifierKey.CapsLock);

        vm.SwitchSubLayout("accents");

        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Win)).IsFalse();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.CapsLock)).IsTrue();
        Check.That(vm.IsShifted).IsTrue();
    }

    [Fact]
    public void EnteringANonAutoReturnSubLayout_ClearsNothing()
    {
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());
        vm.ToggleModifier(ModifierKey.Win);

        vm.SwitchSubLayout("numbers");

        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Win)).IsTrue();
    }

    [Fact]
    public void ShiftOnAccents_TypesTheCapital_BeforeReturning()
    {
        var vm = VmOnAccents();
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ActivateKey("é", "É");
        Check.That(vm.CurrentInput).IsEqualTo("É");   // D11: capitals via shiftOutput
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void EverySwap_RaisesActiveModifiers_AFTERTheGridSwap()
    {
        // M7/H1: RebuildKeys draws static labels, so every swap must notify ActiveModifiers
        // AFTER the ActiveSubLayoutId change — the window's repaint must land on the NEW
        // grid. The ORDER is the requirement (a raise count cannot discriminate a
        // before-order bug — plan-r2), so the recorded sequence must alternate. The visible
        // outcome is pinned by the STA probe in Task 6.
        var vm = Vm();
        vm.SetLayout(LayoutWithAccents());

        var order = new List<string>();
        vm.PropertyChanged += (_, e) => order.Add(e.PropertyName ?? "");
        vm.SwitchSubLayout("numbers");   // NOT an autoReturn target — must still raise
        vm.SwitchSubLayout("letters");

        var filtered = order.Where(p => p
            is nameof(KeyboardViewModel.ActiveSubLayoutId)
            or nameof(KeyboardViewModel.ActiveModifiers)).ToList();
        Check.WithCustomMessage(
                "expected [ActiveSubLayoutId, ActiveModifiers, ActiveSubLayoutId, " +
                $"ActiveModifiers]; got [{string.Join(", ", filtered)}]")
            .That(filtered).ContainsExactly(
                nameof(KeyboardViewModel.ActiveSubLayoutId),
                nameof(KeyboardViewModel.ActiveModifiers),
                nameof(KeyboardViewModel.ActiveSubLayoutId),
                nameof(KeyboardViewModel.ActiveModifiers));
    }
}
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardAutoReturnTests"`
  Expected: FAIL (auto-return/modifier-clearing/raise behavior absent).

- [ ] **Step 4: Implement.** In `KeyboardViewModel`:

a) Beside `ClearOneShotModifiers`:

```csharp
    /// <summary>D15: entering an auto-return sub-layout clears armed one-shot SYSTEM modifiers.
    /// Accents carries no modifier keys, so an armed modifier would be invisible there — and a
    /// stray Win plus an accent is a system shortcut on the OS layout (Win+é = Win+2 on French
    /// Windows). Shift and Caps Lock survive: accents carry their own capitals (D11).</summary>
    private void ClearSystemOneShotModifiers()
    {
        _activeModifiers.Remove(ModifierKey.Ctrl);
        _activeModifiers.Remove(ModifierKey.Alt);
        _activeModifiers.Remove(ModifierKey.Win);
        OnPropertyChanged(nameof(ActiveModifiers));
    }
```

b) `SwitchSubLayout` becomes (Task 3 will insert the D13 anchor block immediately before the
`ActiveSubLayoutId` assignment — leave that seam):

```csharp
    public void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (ActiveSubLayoutId == subLayoutId) return;

        ActiveSubLayoutId = subLayoutId;

        // D15 … and M7: RebuildKeys (synchronous, on the assignment above) draws static
        // labels, so the modifier work runs AFTER the swap — the repaint then lands on the
        // NEW grid: an armed Shift/Caps relabels the fresh keys and the Caps key shows its
        // armed state (plan review H1).
        if (_layout.SubLayouts[subLayoutId].AutoReturn)
            ClearSystemOneShotModifiers();
        OnPropertyChanged(nameof(ActiveModifiers));
    }
```

c) Beside `SwitchSubLayout`:

```csharp
    /// <summary>
    /// Returns to the layout's default sub-layout after TEXT was emitted from an
    /// <c>autoReturn</c> sub-layout (spec D5/D14): a non-special key activation or a prediction
    /// selection. Never fires in emoji mode — emoji keys carry Output and flow through the same
    /// activation path while IsEmojiMode overlays the text sub-layout, and swapping the grid
    /// under the open emoji surface is the RebuildKeys trap (KeyboardWindow.xaml.cs:362-369).
    /// </summary>
    private void AutoReturnIfNeeded()
    {
        if (IsEmojiMode) return;
        if (_layout is null
            || !_layout.SubLayouts.TryGetValue(ActiveSubLayoutId, out var sub)
            || !sub.AutoReturn)
            return;
        SwitchSubLayout(_layout.DefaultSubLayout);
    }
```

d) At the very end of `ActivateKey` (after `ClearOneShotModifiers();`) and at the very end of
`HandlePredictionSelected` (after `Messenger.Send(new PredictionSelectedMessage(...));`), add:

```csharp
        AutoReturnIfNeeded();
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 9/9 PASS.
  Then mutation-check (revert each, watch the named test fail, restore):
  remove the `IsEmojiMode` guard → `WhileInEmojiMode…` fails; remove the `AutoReturnIfNeeded()`
  call in `HandlePredictionSelected` → `PredictionSelection…` fails; remove the
  `ClearSystemOneShotModifiers()` call → `EnteringAutoReturnSubLayout…` fails; remove the
  unconditional `OnPropertyChanged(nameof(ActiveModifiers))` → `EverySwap_Raises…` fails.

- [ ] **Step 6: Run the neighbors** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardViewModel|FullyQualifiedName~KeyboardHideReset|FullyQualifiedName~KeyboardAddWord"`

- [ ] **Step 7: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardAutoReturnTests.cs
git commit -m "feat(viewmodels): auto-return from autoReturn sub-layouts (D5/D14); D15 clearing + M7 modifier repaint on swap"
```

### Task 3: ViewModels — spatial swap suppression (D13)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (`ProcessKeyGaze` :191-270, `SwitchSubLayout`, `ResetInteractionState` :175-184, fields near `_nearestKeyEnteredTimestamp`)
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardSwapSuppressionTests.cs` (create)

**Interfaces:**
- Produces (all private): `private Dictionary<string, PhysicalPoint>? _lastKeyScreenPositions;`, `private PhysicalPoint? _lastGazeInsideWindowPoint;`, `private PhysicalPoint? _swapSuppressedAnchor;`, `private double _swapSuppressedClearRadiusSquared;` and the suppression block inside `ProcessKeyGaze`. Uses `PhysicalPoint.DistanceSquaredTo` (already used there).

**Design (why spatial, not key-identity):** the first samples after a swap see UNARRANGED key positions — every new centre collapses onto one point until WPF arranges them (the probe harness documents this at `KeyboardGateProbeTests.cs:236-240`) — so resolving the landing key ONCE latches garbage and the suppression silently switches off on the first arranged sample ⟦B1⟧. The spatial rule never consults post-swap positions: the radius is computed at swap time from the OLD grid's still-valid centres, and clearing is a single gaze-to-anchor distance check — immune to unarranged centres AND to the boundary-flicker the review measured (É's landing sits 0.03 key widths from the Win/Q boundary; fixation σ ≈ 0.064) ⟦M1, owner-defaulted⟧.

- [ ] **Step 1: GitNexus impact pass on `KeyboardViewModel.ProcessKeyGaze`** (upstream) ⟦L2⟧.

- [ ] **Step 2: Write the failing tests** (positional ids `key_0_0`/`key_0_1`, mirroring the app's real id scheme so the same-id-across-sub-layouts case is exercised ⟦B3⟧; loops BREAK at the first fire — with `Configure(400, 0)` a resting gaze re-fires every 400 ms, so `ContainsExactly` on an unbroken loop is wrong ⟦B2⟧):

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Spec D13 (spatial): after ANY sub-layout swap (manual or auto-return), while the gaze
/// stays within ~half a key width of its pre-swap position, nothing dwells. The radius comes
/// from the OLD grid's centres (post-swap centres are unarranged at the first samples and are
/// never consulted — plan review B1). The anchor is only set when the gaze was on the key
/// area (a prediction pick happens on the bar — its swap suppresses nothing — plan review
/// H3). Hiding clears the suppression.
/// </summary>
public class KeyboardSwapSuppressionTests
{
    private const int DwellMs = 400;

    private static readonly Dictionary<string, PhysicalPoint> Grid =
        new() { ["key_0_0"] = new(100, 100), ["key_0_1"] = new(300, 100) };

    private static KeyboardLayout Layout() => new()
    {
        Name = "T",
        DefaultSubLayout = "letters",
        SubLayouts =
        {
            ["letters"] = new SubLayout
            {
                Label = "ABC",
                Rows = new() { new() { new KeyDefinition { Label = "a", Output = "a" } } },
            },
            ["accents"] = new SubLayout
            {
                Label = "Acc",
                AutoReturn = true,
                Rows = new() { new() { new KeyDefinition { Label = "é", Output = "é", ShiftOutput = "É" } } },
            },
        },
    };

    private static KeyboardViewModel Vm(WeakReferenceMessenger? messenger = null)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger ?? new WeakReferenceMessenger());
        vm.SetLayout(Layout());
        vm.Configure(DwellMs, 0);
        return vm;
    }

    private static List<string> RecordFires(KeyboardViewModel vm)
    {
        var fired = new List<string>();
        vm.KeyDwellCompleted += id => fired.Add(id);
        return fired;
    }

    private static void RestGaze(KeyboardViewModel vm, Dictionary<string, PhysicalPoint> keys,
        PhysicalPoint at, ref long ts, List<string> fired, string expectNoFireFor)
    {
        // Hold the gaze far past the dwell time; BREAK on any fire so the no-fire cases fail
        // loudly instead of double-firing through the zero cooldown (plan review B2).
        for (long t = ts; t < ts + 3_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(keys, at, true, t);
            Check.WithCustomMessage($"{expectNoFireFor} fired while resting")
                .That(fired).IsEmpty();
        }
        ts += 3_100_000;
    }

    [Fact]
    public void ManualSwap_RestingGaze_NeverDwells()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // gaze rests on key_0_0
        vm.SwitchSubLayout("accents");                                   // manual swap under the gaze

        long ts = 1_000;
        RestGaze(vm, Grid, new PhysicalPoint(100, 100), ref ts, fired, "the landing key");
    }

    [Fact]
    public void GazeBeyondRadius_DwellsNormally()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.SwitchSubLayout("accents");

        // 200 units away = a full key pitch > the ~100-unit radius (half the pitch) — cleared.
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, 1_000);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void AutoReturnSwap_SuppressesTheLandingSpot()
    {
        var vm = Vm();
        vm.SwitchSubLayout("accents");
        var fired = RecordFires(vm);

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // rest on key_0_0
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);
        Check.That(fired).ContainsExactly("key_0_0");

        vm.ActivateKey("é", "É");   // the window's role after KeyDwellCompleted → auto-return
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");

        // The letters grid's key_0_0 sits where accents' key_0_0 was (same positional id, same
        // spot): the resting gaze must not latch it (plan review B3's same-id case).
        ts += 100_000;
        for (long t = ts; t < ts + 3_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);
            Check.WithCustomMessage("the letters key under the swap fired while resting")
                .That(fired).ContainsExactly("key_0_0");
        }
    }

    [Fact]
    public void RadiusDiscriminates_JitterStaysIn_OvershootClears()
    {
        // Plan-r2 I3: with the pitch at 200 units, the radius is 100 (half of d2=200²).
        // Jitter of 0.4 pitch (80 units) must stay suppressed; 0.6 pitch (120 units) must
        // clear. This is the pair that catches the 0.25*d2 → 0.25*d1 slip (d1 = 0 for a
        // gaze resting dead-centre — under that mutation the radius collapses to 0 and even
        // the 80-unit jitter clears, firing a key).
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // dead-centre anchor
        vm.SwitchSubLayout("accents");

        // 0.4 pitch of wobble, never leaving the radius: nothing fires.
        long ts = 1_000;
        for (long t = ts; t < ts + 2_000_000; t += 50_000)
        {
            var wobble = (t / 50_000) % 2 == 0 ? 80 : -80;
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100 + wobble, 100), true, t);
            Check.WithCustomMessage("jitter inside the radius fired a key")
                .That(fired).IsEmpty();
        }

        // 0.6 pitch away: past the radius — the nearest key (key_0_1) dwells normally.
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, ts + 2_050_000);
        for (long t = ts + 2_100_000; t < ts + 4_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(220, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void UnarrangedPostSwapCentres_DoNotDefeatTheSuppression()
    {
        // Plan review B1: before WPF arranges the new rows, every centre reports the same
        // point. The spatial rule must not care — it never reads post-swap positions. The
        // gaze rests on key_0_1's spot: under the REV-1 key-latch design the collapsed dict
        // latched key_0_0 (first entry) and the suppression then cleared on the first
        // arranged sample, firing key_0_1 — resting here makes this test FAIL against that
        // bug (plan-r2 I4); the spatial rule keeps it suppressed either way.
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(300, 100), true, 0);   // rest on key_0_1
        vm.SwitchSubLayout("accents");

        var collapsed = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(0, 0), ["key_0_1"] = new(0, 0),
        };
        long ts = 1_000;
        for (long t = ts; t < ts + 1_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(collapsed, new PhysicalPoint(300, 100), true, t);
            Check.That(fired).IsEmpty();
        }

        // Real (arranged) centres arrive; the gaze is still resting — still nothing.
        RestGaze(vm, Grid, new PhysicalPoint(300, 100), ref ts, fired, "the landing key after arrangement");
    }

    [Fact]
    public void NoPriorGaze_NoSuppression()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.SwitchSubLayout("accents");   // swap with no in-window gaze yet: anchor stays null

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // normal dwell, nothing suppressed
    }

    [Fact]
    public void GazeLeavingTheKeyArea_BeforeASwap_NullsTheAnchor()
    {
        // Plan review H3: a prediction is picked with the gaze on the bar (outside the key
        // area). The next swap must not suppress the last key the user looked at.
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);   // gaze on the grid
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(500, 500), false, 50_000); // gaze on the bar: outside

        vm.SwitchSubLayout("accents");   // anchor must be null — gaze was not on the grid

        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 100_000);
        long ts = 150_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // the "next key the user wants" dwells
    }

    [Fact]
    public void HidingClearsTheSuppression()
    {
        // L5: a hide right after a swap must not carry the suppression into the next open.
        // Seam per KeyboardHideResetTests (:67-79, :105-109): the keyboard must be OPEN for
        // a hide to be a transition, and the production path is HideKeyboardMessage (a
        // direct IsVisible=false write from hidden does nothing) — plan-r2 re-opened L5.
        var messenger = new WeakReferenceMessenger();
        var vm = Vm(messenger);
        var fired = RecordFires(vm);
        vm.IsVisible = true;   // the keyboard must be OPEN for the hide to be a transition
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 0);
        vm.SwitchSubLayout("accents");

        messenger.Send(new HideKeyboardMessage());   // the production hide path
        Check.That(vm.IsVisible).IsFalse();

        vm.IsVisible = true;   // reopen
        vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, 1_000);
        long ts = 50_000;
        for (long t = ts; t < ts + 2_000_000 && fired.Count == 0; t += 50_000)
            vm.ProcessKeyGaze(Grid, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("key_0_0");   // fresh session: nothing suppressed
    }
}
```

(If `HandleHideKeyboard` is not the method `KeyboardHideResetTests` drives, use that file's
exact seam and say so in the report — the behavior under test is "hide resets the
suppression".)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardSwapSuppressionTests"`
  Expected: the suppression tests FAIL (landing keys fire — including
  `RadiusDiscriminates_JitterStaysIn_OvershootClears`, whose wobble fires without the rule);
  `NoPriorGaze_NoSuppression`, `GazeLeaving…`, and the overshoot HALF of the jitter test
  PASS already (they assert unsuppressed behavior).

- [ ] **Step 4: Implement.** In `KeyboardViewModel`:

a) Fields beside `_nearestKeyEnteredTimestamp`:

```csharp
    // D13 spatial swap suppression: while the gaze stays within ~half a key width of its
    // pre-swap position, no key dwells — a sub-layout swap (manual or auto-return) happened
    // under a resting gaze and whatever materialized there must not fire. The radius is
    // computed at swap time from the OLD grid's still-valid centres; post-swap centres are
    // unarranged at the first samples and are never consulted (plan review B1).
    private Dictionary<string, PhysicalPoint>? _lastKeyScreenPositions;
    private PhysicalPoint? _lastGazeInsideWindowPoint;
    private PhysicalPoint? _swapSuppressedAnchor;
    private double _swapSuppressedClearRadiusSquared;
```

b) First statements of `ProcessKeyGaze` after the `keyScreenPositions.Count == 0` guard
(before the `!isGazeInsideWindow` block):

```csharp
        _lastKeyScreenPositions = keyScreenPositions;
        if (isGazeInsideWindow)
            _lastGazeInsideWindowPoint = gazePoint;
```

and inside the `if (!isGazeInsideWindow)` block (before its `return;`), so a gaze resting on
the prediction bar nulls the anchor source (H3):

```csharp
            _lastGazeInsideWindowPoint = null;
```

c) In `SwitchSubLayout`, immediately BEFORE `ActiveSubLayoutId = subLayoutId;` (the seam Task
2 left):

```csharp
        // D13: arm the suppression BEFORE the swap — the old grid's centres are still valid
        // here. Anchor only when the gaze was actually on the key area; a prediction pick
        // happens with the gaze on the bar and its auto-return swap must suppress nothing
        // (plan review H3). Radius = half the distance to the second-nearest old centre
        // (≈ half a key width; a resting-on-centre gaze yields d1≈0, d2≈pitch). Fewer than
        // two positioned old centres means no suppression — otherwise d2 stays
        // double.MaxValue and the radius becomes effectively infinite (plan-r2).
        if (_lastGazeInsideWindowPoint is { } anchor
            && _lastKeyScreenPositions is { } positions
            && positions.Count >= 2)
        {
            var d1 = double.MaxValue;
            var d2 = double.MaxValue;
            foreach (var center in positions.Values)
            {
                var d = anchor.DistanceSquaredTo(center);
                if (d < d1)
                {
                    d2 = d1;
                    d1 = d;
                }
                else if (d < d2)
                {
                    d2 = d;
                }
            }

            _swapSuppressedAnchor = anchor;
            _swapSuppressedClearRadiusSquared = 0.25 * d2;
        }
        else
        {
            _swapSuppressedAnchor = null;
        }
```

d) In `ProcessKeyGaze`, AFTER the cooldown block and BEFORE the nearest-key loop:

```csharp
        if (_swapSuppressedAnchor is { } suppressionAnchor)
        {
            if (gazePoint.DistanceSquaredTo(suppressionAnchor) > _swapSuppressedClearRadiusSquared)
            {
                _swapSuppressedAnchor = null;   // gaze moved meaningfully away — normal dwell resumes
            }
            else
            {
                // D13: still resting on the landing spot — nothing dwells.
                if (NearestKeyId != null)
                {
                    NearestKeyProgress = 0;
                    NearestKeyId = null;
                }
                return;
            }
        }
```

e) In `ResetInteractionState`, add beside the other resets (L5):

```csharp
        _swapSuppressedAnchor = null;
        _lastGazeInsideWindowPoint = null;
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 8/8 PASS. Mutation-check: delete
  the suppression block in `ProcessKeyGaze` → the resting tests and the jitter test fail;
  swap `0.25 * d2` for `0.25 * d1` → `RadiusDiscriminates…` fails (the plan-r2 I3 slip);
  delete the H3 nulling → `GazeLeaving…` fails; delete the `ResetInteractionState` lines →
  `HidingClears…` fails.

- [ ] **Step 6: Run the full ViewModel suite once** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests` (one invocation).

- [ ] **Step 7: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardSwapSuppressionTests.cs
git commit -m "feat(viewmodels): spatial swap-landing dwell suppression, radius from old-grid centres (D13)"
```

### Task 4: QWERTZ data — accents sub-layout, row-0 zero-move `Acc` (D17), `ß` capital (D7)

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwertz.json`
- Test: `tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1's `autoReturn` JSON property; the existing `switchSub:` action vocabulary.
- Produces: `qwertz.json` gains `subLayouts.accents` (`autoReturn: true`) and a letters row-0 `Acc` key; `ß` gains `"shiftOutput": "ẞ"`. The letters FOOTER is untouched (`Space` keeps 2.0 — D17 supersedes D16) ⟦plan M4, owner-defaulted⟧.

- [ ] **Step 1: No impact pass** (data file — no indexed symbols).

- [ ] **Step 2: Write the failing tests** (append to `ShippedLayoutFilesTests`; reuse the class's `Load`/`LayoutDir` helpers):

```csharp
    [Theory]
    [InlineData("qwertz.json")]
    [InlineData("azerty.json")]
    public void AccentsSubLayout_IsAutoReturn_WithWellFormedKeys(string file)
    {
        var layout = Load(file);
        Check.That(layout.SubLayouts.ContainsKey("accents")).IsTrue();
        var accents = layout.SubLayouts["accents"];

        Check.WithCustomMessage("accents must auto-return (spec D5)")
            .That(accents.AutoReturn).IsTrue();

        var keys = accents.Rows.SelectMany(r => r).ToList();
        Check.WithCustomMessage("every non-special accents key must emit text (spec M2)")
            .That(keys.Where(k => k.SpecialAction is null).All(k => !string.IsNullOrEmpty(k.Output)))
            .IsTrue();
        Check.WithCustomMessage(
                "every lower-case accents key must carry EXACTLY its invariant upper-case as " +
                "shiftOutput (spec D11; a wrong-but-non-empty capital fails — plan-r2)")
            .That(keys.Where(k => k.SpecialAction is null
                                   && k.Output.Length == 1
                                   && char.IsLetter(k.Output[0])
                                   && char.IsLower(k.Output[0]))
                      .All(k => k.ShiftOutput == char.ToUpperInvariant(k.Output[0]).ToString()))
            .IsTrue();

        // Widest row must equal the 12.8 standard so the SizeToContent window's WIDTH never
        // changes between sub-layouts (spec shape rules).
        var widest = accents.Rows.Max(r => r.Sum(k => k.Width));
        Check.WithCustomMessage($"widest accents row is {widest:0.##}, expected 12.8")
            .That(Math.Abs(widest - 12.8) < 0.01).IsTrue();

        // Row count must EQUAL the letters sub-layout's (both layouts ship 3 content rows +
        // footer = 4) so the bottom-docked window's HEIGHT never changes and no row shifts
        // under the gaze (spec shape rules, plan review H2).
        Check.WithCustomMessage(
                $"accents has {accents.Rows.Count} rows, letters has " +
                $"{layout.SubLayouts[layout.DefaultSubLayout].Rows.Count} — must match (plan H2)")
            .That(accents.Rows.Count)
            .IsEqualTo(layout.SubLayouts[layout.DefaultSubLayout].Rows.Count);

    }

    [Theory]
    [InlineData("qwerty.json")]
    [InlineData("azerty.json")]
    [InlineData("qwertz.json")]
    public void EverySwitchSubTarget_NamesAnExistingSubLayout(string file)
    {
        // Spec M5 over the REAL shipped data, ALL THREE files including QWERTY (plan-r2:
        // the accents theory only covers the two layouts that carry accents).
        var layout = Load(file);
        var ids = layout.SubLayouts.Keys.ToHashSet();
        var targets = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Select(k => k.SpecialAction)
            .Where(a => a is not null && a.StartsWith("switchSub:", StringComparison.Ordinal))
            .Select(a => a!["switchSub:".Length..]);
        Check.WithCustomMessage($"{file} dangles a switchSub: target")
            .That(targets.All(t => ids.Contains(t))).IsTrue();
    }

    [Fact]
    public void Qwertz_LettersRow0_GainsAcc_WithZeroMovement()
    {
        var layout = Load("qwertz.json");
        var row0 = layout.SubLayouts["letters"].Rows[0];

        // D17 (supersedes D16; owner-defaulted): row 0 is Win 1.2 + Q..P = 11.2 — the same
        // zero-move trick as AZERTY. Win widens 1.2 -> 2.0 (consuming the 0.8 left margin),
        // Acc (0.8) consumes the right margin; every letter keeps its exact position and the
        // row hits the 12.8 standard. Pinned so a future edit cannot silently shift letters.
        Check.That(row0.Single(k => k.Label == "Win").Width).IsCloseTo(2.0, 0.01);
        var acc = row0.Single(k => k.SpecialAction == "switchSub:accents");
        Check.That(acc.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.IndexOf(acc)).IsEqualTo(row0.Count - 1);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);

        // D16 explicitly NOT applied: the footer is untouched.
        var footer = layout.SubLayouts["letters"].Rows[^1];
        Check.That(footer.Sum(k => k.Width)).IsCloseTo(11.7, 0.01);
        Check.That(footer.Single(k => k.Label == "Space").Width).IsCloseTo(2.0, 0.01);
    }

    [Fact]
    public void Qwertz_SharpS_GainsCapitalShiftOutput()
    {
        var layout = Load("qwertz.json");
        // D7: ẞ (U+1E9E). Data-pinned, not view-model echo (spec I6): deleting the JSON
        // attribute must fail right here.
        var sharpS = layout.SubLayouts["letters"].Rows.SelectMany(r => r)
            .Single(k => k.Output == "ß");
        Check.That(sharpS.ShiftOutput).IsEqualTo("ẞ");
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ShippedLayoutFilesTests"`
  Expected: `AccentsSubLayout…[qwertz.json]` and `Qwertz_LettersRow0…` FAIL (no accents
  sub-layout, no Acc key); `Qwertz_SharpS…` FAILS only on the `IsEqualTo("ẞ")` assertion
  (the generic non-empty capital check in the theory is vacuous until accents exist — the
  ß pin is what makes it red) ⟦plan-r2 step-accuracy⟧; `AccentsSubLayout…[azerty.json]`
  still fails (Task 5's data); the file's other pre-existing theories PASS.

- [ ] **Step 4: Implement.** In `qwertz.json`:

a) Letters row 0 — and ONLY the letters row 0: each shipped layout file carries THREE
   identical `Win` lines (letters row 0, numbers row 0, symbols row 0); widening any but the
   letters one would shift that sub-layout's row for nothing ⟦plan-r2⟧. Change the LETTERS
   row-0 `{"label": "Win", … "width": 1.2, "specialAction": "modifier:win"}` to
   `"width": 2.0`, and after the `P` key append:

```json
{ "label": "Acc", "output": "", "width": 0.8, "specialAction": "switchSub:accents" }
```

b) The `ß` key becomes:

```json
{ "label": "ß", "output": "ß", "shiftOutput": "ẞ", "width": 1.0 }
```

c) Add the accents sub-layout (place it after `"symbols"` in `subLayouts`; 3 content rows +
footer = 4 rows, matching letters ⟦H2⟧; rows sum 12.8 each; no filler keys; Space carries
`"output": " "` like every shipped footer ⟦L3⟧):

```json
"accents": {
  "label": "Acc",
  "autoReturn": true,
  "rows": [
    [
      { "label": "é", "output": "é", "shiftOutput": "É", "width": 1.28 },
      { "label": "É", "output": "É", "width": 1.28 },
      { "label": "è", "output": "è", "shiftOutput": "È", "width": 1.28 },
      { "label": "È", "output": "È", "width": 1.28 },
      { "label": "ê", "output": "ê", "shiftOutput": "Ê", "width": 1.28 },
      { "label": "Ê", "output": "Ê", "width": 1.28 },
      { "label": "ë", "output": "ë", "shiftOutput": "Ë", "width": 1.28 },
      { "label": "Ë", "output": "Ë", "width": 1.28 },
      { "label": "à", "output": "à", "shiftOutput": "À", "width": 1.28 },
      { "label": "À", "output": "À", "width": 1.28 }
    ],
    [
      { "label": "â", "output": "â", "shiftOutput": "Â", "width": 1.28 },
      { "label": "Â", "output": "Â", "width": 1.28 },
      { "label": "ç", "output": "ç", "shiftOutput": "Ç", "width": 1.28 },
      { "label": "Ç", "output": "Ç", "width": 1.28 },
      { "label": "î", "output": "î", "shiftOutput": "Î", "width": 1.28 },
      { "label": "Î", "output": "Î", "width": 1.28 },
      { "label": "ï", "output": "ï", "shiftOutput": "Ï", "width": 1.28 },
      { "label": "Ï", "output": "Ï", "width": 1.28 },
      { "label": "ô", "output": "ô", "shiftOutput": "Ô", "width": 1.28 },
      { "label": "Ô", "output": "Ô", "width": 1.28 }
    ],
    [
      { "label": "ù", "output": "ù", "shiftOutput": "Ù", "width": 1.2 },
      { "label": "Ù", "output": "Ù", "width": 1.2 },
      { "label": "û", "output": "û", "shiftOutput": "Û", "width": 1.2 },
      { "label": "Û", "output": "Û", "width": 1.2 },
      { "label": "œ", "output": "œ", "shiftOutput": "Œ", "width": 1.2 },
      { "label": "Œ", "output": "Œ", "width": 1.2 },
      { "label": "«", "output": "«", "width": 1.2 },
      { "label": "»", "output": "»", "width": 1.2 },
      { "label": "Bksp", "output": "", "width": 1.6, "specialAction": "backspace" },
      { "label": "DelWd", "output": "", "width": 1.6, "specialAction": "deleteWord" }
    ],
    [
      { "label": "ABC", "output": "", "width": 1.5, "specialAction": "switchSub:letters" },
      { "label": "123", "output": "", "width": 1.5, "specialAction": "switchSub:numbers" },
      { "label": "#+=", "output": "", "width": 1.5, "specialAction": "switchSub:symbols" },
      { "label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji" },
      { "label": "Fn", "output": "", "width": 1.0, "specialAction": "switchSub:function" },
      { "label": "Space", "output": " ", "width": 2.0, "specialAction": "space" },
      { "label": "Enter", "output": "", "width": 1.5, "specialAction": "enter" },
      { "label": "Tab", "output": "", "width": 1.0, "specialAction": "tab" }
    ]
  ]
}
```

(Row sums: 10 × 1.28 = 12.8; 10 × 1.28 = 12.8; 8 × 1.2 + 2 × 1.6 = 12.8. Footer 11.5.)

- [ ] **Step 5: Run to verify pass** — same filter. Expected: ALL PASS including the file's
  pre-existing theories (the accents rows use only known actions; Bksp/DelWd present).

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/qwertz.json tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs
git commit -m "feat(app): QWERTZ accents sub-layout (28 keys) + row-0 zero-move Acc + ẞ (D2/D17/D7)"
```

### Task 5: AZERTY data — accents tail (3 content rows ⟦H2⟧), zero-move `Acc`, four capitals (D3/D6)

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/azerty.json`
- Test: `tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs` (extend)

**Interfaces:**
- Consumes: Task 4's shared `AccentsSubLayout_IsAutoReturn_WithWellFormedKeys` theory (already parameterized over azerty) and its row-count-equality assertion.

- [ ] **Step 1: No impact pass** (data file).

- [ ] **Step 2: Write the failing tests** (append to `ShippedLayoutFilesTests`):

```csharp
    [Fact]
    public void Azerty_LettersRow0_GainsAcc_WithZeroMovement()
    {
        var layout = Load("azerty.json");
        var row0 = layout.SubLayouts["letters"].Rows[0];

        // D3 zero-move: Win widens 1.2 -> 2.0 (consuming the 0.8 left margin), Acc (0.8)
        // consumes the right margin; every letter keeps its exact position and the row hits
        // the 12.8 standard. Pinned so a future edit cannot silently shift the letters.
        Check.That(row0.Single(k => k.Label == "Win").Width).IsCloseTo(2.0, 0.01);
        var acc = row0.Single(k => k.SpecialAction == "switchSub:accents");
        Check.That(acc.Width).IsCloseTo(0.8, 0.01);
        Check.That(row0.IndexOf(acc)).IsEqualTo(row0.Count - 1);
        Check.That(row0.Sum(k => k.Width)).IsCloseTo(12.8, 0.01);
    }

    [Fact]
    public void Azerty_LettersAccentKeys_GainCapitals()
    {
        var layout = Load("azerty.json");
        var letters = layout.SubLayouts["letters"].Rows.SelectMany(r => r).ToList();

        // D6 data pins (spec I6): the four letters accent keys carry their capitals IN THE
        // FILE — deleting an attribute fails here, not just in a view-model echo.
        Check.That(letters.Single(k => k.Output == "é").ShiftOutput).IsEqualTo("É");
        Check.That(letters.Single(k => k.Output == "è").ShiftOutput).IsEqualTo("È");
        Check.That(letters.Single(k => k.Output == "à").ShiftOutput).IsEqualTo("À");
        Check.That(letters.Single(k => k.Output == "ç").ShiftOutput).IsEqualTo("Ç");
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ShippedLayoutFilesTests"`.
  Expected: the two new tests FAIL; `AccentsSubLayout…[azerty.json]` still fails until
  Step 4; `[qwertz.json]` and the new `EverySwitchSubTarget…` theory PASS from Task 4
  (the switchSub theory runs over the CURRENT shipped files and is green before any accents
  data exists — it goes red only if a later edit dangles a target) ⟦plan-r2 step-accuracy⟧.

- [ ] **Step 4: Implement.** In `azerty.json`:

a) Letters row 0 — and ONLY the letters row 0 (azerty.json also carries three identical
   `Win` lines across its sub-layouts; widening any but the letters one shifts that row for
   nothing ⟦plan-r2⟧): change the LETTERS row-0
   `{"label": "Win", … "width": 1.2, "specialAction": "modifier:win"}` to `"width": 2.0`,
   and after the `P` key append:

```json
{ "label": "Acc", "output": "", "width": 0.8, "specialAction": "switchSub:accents" }
```

b) The four letters accent keys gain `"shiftOutput"`: `é → "É"`, `è → "È"`, `à → "À"`,
`ç → "Ç"` (in their existing rows — two in a content row, two in the letters footer).

c) Add the accents sub-layout after `"symbols"` — **3 content rows + footer = 4 rows,
matching azerty letters** (letters has 3 content + footer; a shorter grid would shrink the
bottom-docked window and shift every row + the prediction bar under the gaze — plan review
H2). Row sums 12.8 each:

```json
"accents": {
  "label": "Acc",
  "autoReturn": true,
  "rows": [
    [
      { "label": "ê", "output": "ê", "shiftOutput": "Ê", "width": 1.6 },
      { "label": "Ê", "output": "Ê", "width": 1.6 },
      { "label": "â", "output": "â", "shiftOutput": "Â", "width": 1.6 },
      { "label": "Â", "output": "Â", "width": 1.6 },
      { "label": "î", "output": "î", "shiftOutput": "Î", "width": 1.6 },
      { "label": "Î", "output": "Î", "width": 1.6 },
      { "label": "ô", "output": "ô", "shiftOutput": "Ô", "width": 1.6 },
      { "label": "Ô", "output": "Ô", "width": 1.6 }
    ],
    [
      { "label": "û", "output": "û", "shiftOutput": "Û", "width": 1.6 },
      { "label": "Û", "output": "Û", "width": 1.6 },
      { "label": "ë", "output": "ë", "shiftOutput": "Ë", "width": 1.6 },
      { "label": "Ë", "output": "Ë", "width": 1.6 },
      { "label": "ï", "output": "ï", "shiftOutput": "Ï", "width": 1.6 },
      { "label": "Ï", "output": "Ï", "width": 1.6 },
      { "label": "ù", "output": "ù", "shiftOutput": "Ù", "width": 1.6 },
      { "label": "Ù", "output": "Ù", "width": 1.6 }
    ],
    [
      { "label": "œ", "output": "œ", "shiftOutput": "Œ", "width": 1.6 },
      { "label": "Œ", "output": "Œ", "width": 1.6 },
      { "label": "«", "output": "«", "width": 1.6 },
      { "label": "»", "output": "»", "width": 1.6 },
      { "label": "Bksp", "output": "", "width": 3.2, "specialAction": "backspace" },
      { "label": "DelWd", "output": "", "width": 3.2, "specialAction": "deleteWord" }
    ],
    [
      { "label": "ABC", "output": "", "width": 1.5, "specialAction": "switchSub:letters" },
      { "label": "123", "output": "", "width": 1.5, "specialAction": "switchSub:numbers" },
      { "label": "#+=", "output": "", "width": 1.5, "specialAction": "switchSub:symbols" },
      { "label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji" },
      { "label": "Fn", "output": "", "width": 1.0, "specialAction": "switchSub:function" },
      { "label": "Space", "output": " ", "width": 2.0, "specialAction": "space" },
      { "label": "Enter", "output": "", "width": 1.5, "specialAction": "enter" },
      { "label": "Tab", "output": "", "width": 1.0, "specialAction": "tab" }
    ]
  ]
}
```

(`ù Ù` are in the tail because `ù` lives on symbols today — symbols is left untouched,
spec R2.)

- [ ] **Step 5: Run to verify pass** — same filter. Expected: ALL PASS (both theory cases +
  all pre-existing theories).

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/azerty.json tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs
git commit -m "feat(app): AZERTY accents tail (20 keys, 3 content rows) + zero-move Acc + É È À Ç (D3/D6, H2)"
```

### Task 6: Real-window probes at scale 2.0 + landing-geometry pins + competitive-doc close-out

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Keyboard/KeyboardGateProbeTests.cs` (extend — reuse `Open`, `DwellOn`, `RoundTrip`, `Centres`, `ShippedEmojiJson`; add a `ShippedQwertz` loader mirroring `ShippedQwerty` at :118)
- Modify: `docs/COMPETITIVE_FEATURE_ANALYSIS.md` (gap #11 → implemented; queue re-rank)

**Interfaces:**
- Consumes: Tasks 1–5. Key ids in the real window are POSITIONAL for regular keys (`key_{row}_{col}`) and the action string for special keys ⟦B3⟧.

- [ ] **Step 1: No impact pass** (test + docs).

- [ ] **Step 2: Write the failing tests.** Add beside `ShippedQwerty`:

```csharp
    private static KeyboardLayout ShippedQwertz() =>
        KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
```

The probe tests (the harness's `DwellOn` moves the gaze to the target key's centre — which
is exactly what clears D13's spatial suppression between dwells; never re-dwell the same
spot without a move ⟦spec I6⟧; regular-key ids are positional ⟦B3⟧ — `é` is accents
`key_0_0`):

```csharp
    [StaFact]
    public void ShippedQwertz_Accents_AutoReturnRoundTrip_AtScale2()
    {
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;

            // Manual round trip: Acc opens accents; the ABC key returns (existing helper dwells
            // out and back, proving both directions of the switch).
            RoundTrip(probe, "switchSub:accents", "accents", ref ts);

            // Auto-return: dwell Acc, then dwell é (accents key_0_0) — é types and the
            // keyboard is back on letters.
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "key_0_0", ref ts);
            Check.WithCustomMessage("dwelling an accent did not auto-return to letters")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
            Check.That(probe.Vm.CurrentInput).Contains("é");

            // Emoji entered FROM accents must not auto-return under the open emoji grid (R1):
            // exiting emoji lands back on accents, and dwelling an accent from there returns.
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "switchEmoji", ref ts);
            Check.That(probe.Vm.IsEmojiMode).IsTrue();
            Check.WithCustomMessage("emoji entry fired auto-return under the emoji grid")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");
            DwellOn(probe, "special_switchText", ref ts);
            Check.That(probe.Vm.IsEmojiMode).IsFalse();
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");

            DwellOn(probe, "key_0_0", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_AccEntry_LandsOnTheAccentsRow0EndKey()
    {
        // Plan review M2 / plan-r2 I2: pin the landing geometry. Acc sits at the letters
        // row-0 right end; the accents key whose centre is nearest Acc's centre must be
        // row 0's last key (À — key_0_9). A later layout edit that moves the landing shows
        // up here. The swap dwell empties the window's key positions (_keyScreenPositions
        // is rebuilt inside the next gaze pass), so ONE GAZE PASS + one layout pass must
        // run before Centres() — otherwise the collection is empty and this throws.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            var lettersCentres = Centres(probe.Window);
            var accCentre = lettersCentres["switchSub:accents"];

            long ts = 1_000;
            DwellOn(probe, "switchSub:accents", ref ts);

            // Refresh positions before reading them (plan-r2 I2).
            LookAt(probe, accCentre, ref ts);
            probe.Content.UpdateLayout();

            var accentsCentres = Centres(probe.Window);
            var landing = accentsCentres
                .OrderBy(kvp => kvp.Value.DistanceSquaredTo(accCentre))
                .First().Key;
            Check.WithCustomMessage(
                    $"the key under Acc's resting gaze is {landing}, expected key_0_9 (À)")
                .That(landing).IsEqualTo("key_0_9");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedAzerty_AccEntry_LandsOnTheAccentsRow0EndKey()
    {
        // The AZERTY half of the landing pin (spec Testing; expected Ô — key_0_7, the last
        // key of the 8-wide accents row 0). Same refresh dance as the QWERTZ pin.
        var probe = Open(ShippedAzerty(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            var lettersCentres = Centres(probe.Window);
            var accCentre = lettersCentres["switchSub:accents"];

            long ts = 1_000;
            DwellOn(probe, "switchSub:accents", ref ts);

            LookAt(probe, accCentre, ref ts);
            probe.Content.UpdateLayout();

            var accentsCentres = Centres(probe.Window);
            var landing = accentsCentres
                .OrderBy(kvp => kvp.Value.DistanceSquaredTo(accCentre))
                .First().Key;
            Check.WithCustomMessage(
                    $"the key under Acc's resting gaze is {landing}, expected key_0_7 (Ô)")
                .That(landing).IsEqualTo("key_0_7");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_SwapRepaintsModifierVisuals_CapsStaysArmed()
    {
        // Plan review H1: RebuildKeys draws static labels; the VM's unconditional
        // ActiveModifiers raise must relabel the fresh grid. With Caps on, accents key_0_0
        // must show É (its shiftOutput), and after auto-return the letters Caps key must
        // still paint as armed.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            probe.Vm.ToggleModifier(ModifierKey.CapsLock);

            DwellOn(probe, "switchSub:accents", ref ts);

            var accentLabel = KeyLabel(probe, "key_0_0");
            Check.WithCustomMessage($"accents key_0_0 reads '{accentLabel}', expected the capital É")
                .That(accentLabel).IsEqualTo("É");

            DwellOn(probe, "key_0_0", ref ts);   // É types; auto-return to letters
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");

            // Guards the D15-preserved half: a future refactor that clears ALL modifiers
            // (not just the system ones) on swap fails here. The label half above is the
            // stronger discriminator for the M7 repaint itself (plan-r2: this assert cannot
            // fail against the planned ClearSystemOneShotModifiers, which never touches
            // CapsLock — it is kept as a regression net, not as the H1 proof).
            Check.WithCustomMessage("Caps Lock was silently disarmed by the swap")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.CapsLock)).IsTrue();
        }
        finally { probe.Dispose(); }
    }
```

`KeyLabel` reads the label through the window's PRIVATE `_keyVisuals` registry (id →
`(Border, TextBlock?)`) — keys carry no XAML names, so a `FindName` route cannot work
⟦plan-r2⟧; `ShippedAzerty` mirrors `ShippedQwertz`:

```csharp
    private static KeyboardLayout ShippedAzerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "azerty.json"));

    private static string? KeyLabel(Probe probe, string keyId)
    {
        var field = typeof(KeyboardWindow).GetField("_keyVisuals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(probe.Window) is not System.Collections.IDictionary visuals
            || visuals[keyId] is not { } entry)
            return null;

        // The registry's value is (Border Container, TextBlock? Label) — a ValueTuple; Item2
        // is the label the modifier repaint rewrites, which is exactly what H1 pins.
        var label = entry.GetType().GetField("Item2")?.GetValue(entry)
            as System.Windows.Controls.TextBlock;
        return label?.Text;
    }
```

(If keys are not registered with `FindName`-reachable names, locate the key by its id
through whatever registry `DwellOn` itself uses and walk from there — same helper shape;
say in the report which route compiled.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ShippedQwertz_Accents|FullyQualifiedName~ShippedQwertz_AccEntry|FullyQualifiedName~ShippedAzerty_AccEntry|FullyQualifiedName~ShippedQwertz_SwapRepaints"`.
  Expected: FAIL (compile of the new helpers/tests; with Tasks 4–5 landed the behavioral
  tests may pass on first run — acceptable, note it).

- [ ] **Step 4: Docs.** In `docs/COMPETITIVE_FEATURE_ANALYSIS.md`: change the Tier-2 table's
  row 11 to "**Implemented 2026-09 on `feat/accent-input`, pending merge** — per-layout
  `accents` sub-layouts with auto-return (QWERTZ 28-key French-in-German set, AZERTY 20-key
  tail); dead-key engine declined as the revisit path (spec D1)" ⟦L4⟧, and remove item 1
  from the value-ranked queue so "Dwell-completion audio cue" becomes the head.

- [ ] **Step 5: Run the probe + neighbors** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardGateProbe|FullyQualifiedName~ShippedLayoutFilesTests|FullyQualifiedName~KeyboardGateCensus"`
  Expected: ALL PASS.

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add tests/EyeCtrl.App.Tests/Keyboard/KeyboardGateProbeTests.cs docs/COMPETITIVE_FEATURE_ANALYSIS.md
git commit -m "test(app): shipped-QWERTZ accents probes (round trip, landing pin, modifier repaint); close gap #11"
```

---

## Final verification

- [ ] Full non-UI suite, one project per invocation or the `EyeCtrl.slnx` form:
  `dotnet test EyeCtrl.slnx --filter "Category!=UI"` — expect zero failures against the
  pre-branch baseline (all projects were green at `2e1d9078`).
- [ ] `dotnet build EyeCtrl.slnx` clean.
- [ ] Report per task with test counts. Live pass (launching the app to type an accent by
  gaze) is owner-gated and NOT part of this plan's execution.
- [ ] Known residual, recorded and declined ⟦L6⟧: leaving emoji mode also swaps the grid
  under a resting gaze and is NOT covered by D13 (pre-existing behavior, unchanged).
