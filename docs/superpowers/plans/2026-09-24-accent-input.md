# Accent Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship per-layout `accents` sub-layouts with auto-return (spec D1–D16): a 28-key French-in-German grid on QWERTZ, a 20-key tail on AZERTY, plus the auto-return rule, swap-landing dwell suppression (D13), prediction auto-return (D14), system-modifier clearing on entry (D15), zero-movement `Acc` placements (D16/AZERTY), and the AZERTY/`ß` capital data fixes.

**Architecture:** Pure layout data plus two small ViewModel rules and one Core property. `SubLayout` gains `AutoReturn` (System.Text.Json camelCase binds `autoReturn` with no loader change). `KeyboardViewModel` gains the auto-return trigger (end of `ActivateKey` and `HandlePredictionSelected`, gated on `!IsEmojiMode`), system-modifier clearing in `SwitchSubLayout` (D15), and position-anchored swap suppression in `ProcessKeyGaze`/`SwitchSubLayout` (D13). No window code changes; no predictor changes.

**Tech Stack:** .NET 10, WPF, xUnit v3 + NFluent 3.1.0, CommunityToolkit.Mvvm.

**Spec:** `docs/superpowers/specs/2026-09-24-accent-input-design.md` (revision 3, commit `30982ddb`). Work in `.worktrees/accent-input` on `feat/accent-input`.

## Global Constraints

- NFluent 3.1.0 (`Check.That(...)`), xUnit v3 `[Fact]`/`[Theory]`/`[StaFact]` (STA only where WPF controls are constructed); global usings `Xunit` + `NFluent` come from `tests/Directory.Build.props` — do not add per-file usings for them.
- No mocking library: inline JSON / hand-built layout objects / existing probe harnesses. Unique temp dir names per test.
- File-scoped namespaces, Allman braces, `_camelCase` fields, no `#region`.
- German-locale box: never parse formatted numbers; `dotnet test` output is German (`Fehler!` = fail).
- ONE test project per `dotnet test` invocation; never run two suites at once (shared box).
- **Before the first edit of each symbol a task lists, run the GitNexus MCP `impact` pass (upstream)**; treat UNKNOWN as unresolved. The index is main-checkout-era — confirm surprises with a text search. **Before every commit:** MCP `detect_changes` with `worktree` = `C:\Users\andre\source\repos\eyectrl\.worktrees\accent-input`, plus `git diff --stat` vs the task's file list — that diff is the check that counts.
- Never push. Never launch EyeCtrl.App (STA window tests in Task 6 construct windows, not the app — those are fine).
- The spec's decision ledger D1–D16 is binding; if a task's execution surfaces a conflict with the spec text, stop and ask rather than improvise.

---

### Task 1: Core — `AutoReturn` on `SubLayout`

**Files:**
- Modify: `src/EyeCtrl.Core/Keyboard/SubLayout.cs`
- Test: `tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutAutoReturnTests.cs` (create)

**Interfaces:**
- Produces: `bool SubLayout.AutoReturn { get; set; }` (JSON `"autoReturn"`, camelCase-bound by the existing `KeyboardLayoutLoader` options — no loader change).

- [ ] **Step 1: GitNexus impact pass on `SubLayout`** (upstream). Expected consumers: `KeyboardLayoutLoader` (deserialize) and layout consumers. Low risk; do not proceed on UNKNOWN without a text search.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>autoReturn rides the existing camelCase System.Text.Json binding (spec D5): these
/// tests pin the property's load/save round-trip and the switchSub-target sanity rule (spec M5).</summary>
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

    [Fact]
    public void EverySwitchSubTarget_NamesAnExistingSubLayout()
    {
        var layout = SaveAndLoad("""
        {
          "name": "T", "defaultSubLayout": "letters",
          "subLayouts": {
            "letters": { "label": "ABC", "rows": [ [
              { "label": "Acc", "output": "", "specialAction": "switchSub:accents" },
              { "label": "123", "output": "", "specialAction": "switchSub:numbers" }
            ] ] },
            "numbers": { "label": "123", "rows": [ [] ] },
            "accents": { "label": "Acc", "autoReturn": true, "rows": [ [] ] }
          }
        }
        """);

        var ids = layout.SubLayouts.Keys.ToHashSet();
        var targets = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Select(k => k.SpecialAction)
            .Where(a => a is not null && a.StartsWith("switchSub:", StringComparison.Ordinal))
            .Select(a => a!["switchSub:".Length..])
            .ToList();

        Check.WithCustomMessage("every switchSub: target must name a sub-layout in the same layout")
            .That(targets.All(t => ids.Contains(t))).IsTrue();
    }
}
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~KeyboardLayoutAutoReturn"`
  Expected: FAIL — `AutoReturn` does not exist (compile error).

- [ ] **Step 4: Implement.** In `SubLayout`, after `Rows`:

```csharp
    /// <summary>True when the keyboard returns to the layout's default sub-layout after any
    /// text-emitting key (spec D5). Used by the accents sub-layouts: an accent is always part
    /// of ongoing typing, so the user should land back on letters without a manual switch.
    /// Defaults to false — an unset flag must never change existing sub-layout behavior.</summary>
    public bool AutoReturn { get; set; }
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 3/3 PASS.

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.Core/Keyboard/SubLayout.cs tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutAutoReturnTests.cs
git commit -m "feat(core): SubLayout.AutoReturn flag (accent-input D5)"
```

### Task 2: ViewModels — auto-return trigger (D5/D14) + modifier clearing on entry (D15)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (`ActivateKey` :346-360, `HandlePredictionSelected` :488-506, `SwitchSubLayout` :458-463, `ClearOneShotModifiers` region :328)
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardAutoReturnTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's `SubLayout.AutoReturn`.
- Produces (all private except noted): `private void AutoReturnIfNeeded()` called at the end of `ActivateKey` and `HandlePredictionSelected`; `private void ClearSystemOneShotModifiers()` called from `SwitchSubLayout`. No public signature changes.

- [ ] **Step 1: GitNexus impact pass on `KeyboardViewModel.ActivateKey` and `SwitchSubLayout`** (upstream; hub class — expect breadth; this change is additive call-sites at method ends plus a guarded branch in SwitchSubLayout).

- [ ] **Step 2: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;

/// <summary>Spec D5 (auto-return), D14 (prediction selections return too), D15 (system
/// modifiers cleared on entry). The trigger is a NON-SPECIAL key activation — whatever
/// modifiers are armed — or a prediction selection, never while IsEmojiMode (emoji keys carry
/// Output and flow through the same path while emoji mode overlays the text sub-layout).</summary>
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

    private static KeyboardViewModel VmOnAccents()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), WeakReferenceMessenger.Default);
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
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), WeakReferenceMessenger.Default);
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
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), WeakReferenceMessenger.Default);
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
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), WeakReferenceMessenger.Default);
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
}
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardAutoReturnTests"`
  Expected: FAIL (auto-return/modifier-clearing behavior absent).

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

b) `SwitchSubLayout` becomes (Task 3 adds one more line here — the D13 anchor):

```csharp
    public void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (ActiveSubLayoutId == subLayoutId) return;

        if (_layout.SubLayouts[subLayoutId].AutoReturn)
            ClearSystemOneShotModifiers();

        ActiveSubLayoutId = subLayoutId;
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

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 8/8 PASS.
  Then mutation-check (revert each, watch the named test fail, restore):
  remove the `IsEmojiMode` guard → `WhileInEmojiMode…` fails; remove the `AutoReturnIfNeeded()`
  call in `HandlePredictionSelected` → `PredictionSelection…` fails; remove the
  `ClearSystemOneShotModifiers()` call → `EnteringAutoReturnSubLayout…` fails.

- [ ] **Step 6: Run the neighbors** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardViewModel|FullyQualifiedName~KeyboardHideReset|FullyQualifiedName~KeyboardAddWord"`

- [ ] **Step 7: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardAutoReturnTests.cs
git commit -m "feat(viewmodels): auto-return from autoReturn sub-layouts (D5/D14) + system-modifier clearing on entry (D15)"
```

### Task 3: ViewModels — swap-landing dwell suppression (D13)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs` (`ProcessKeyGaze` :191-270, `SwitchSubLayout`, fields near `_nearestKeyEnteredTimestamp`)
- Test: `tests/EyeCtrl.ViewModels.Tests/KeyboardSwapSuppressionTests.cs` (create)

**Interfaces:**
- Produces (all private): `private PhysicalPoint? _lastGazeInsideWindowPoint;`, `private PhysicalPoint? _swapSuppressedAnchor;`, `private string? _swapSuppressedKeyId;` and the suppression block inside `ProcessKeyGaze`. Consumes `PhysicalPoint.DistanceSquaredTo` (already used there).

- [ ] **Step 1: No new impact pass needed** — same symbols as Task 2, whose pass covers `ProcessKeyGaze`'s class.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Spec D13: after ANY sub-layout swap (manual or auto-return), the key that materializes
/// under the pre-swap gaze position cannot START dwelling until the gaze leaves it. Key ids
/// differ across sub-layouts, so the suppression anchors to the gaze POSITION and resolves
/// lazily against the post-swap keys. The gaze cadence for every other key is unchanged.
/// </summary>
public class KeyboardSwapSuppressionTests
{
    private static readonly Dictionary<string, PhysicalPoint> Letters =
        new() { ["a"] = new(100, 100), ["b"] = new(300, 100) };
    private static readonly Dictionary<string, PhysicalPoint> Accents =
        new() { ["é"] = new(100, 100), ["ê"] = new(300, 100) };

    private static KeyboardViewModel Vm()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), WeakReferenceMessenger.Default);
        vm.SetLayout(new KeyboardLayout
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
        });
        vm.Configure(400, 0);   // 400 ms dwell, no cooldown — suppression is position-based
        return vm;
    }

    private static List<string> RecordFires(KeyboardViewModel vm)
    {
        var fired = new List<string>();
        vm.KeyDwellCompleted += id => fired.Add(id);
        return fired;
    }

    [Fact]
    public void ManualSwap_LandingKeyNeverDwells_WhileGazeRests()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Letters, new PhysicalPoint(100, 100), true, 0);   // gaze rests on "a"
        vm.SwitchSubLayout("accents");                                     // manual swap: anchor at (100,100)

        for (long t = 1_000; t <= 2_000_000; t += 50_000)                  // 2 s of resting gaze
            vm.ProcessKeyGaze(Accents, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).IsEmpty();   // "é" sits under the anchor and must never fire
    }

    [Fact]
    public void AfterGazeMoves_SuppressionClears_AndOtherKeysDwell()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.ProcessKeyGaze(Letters, new PhysicalPoint(100, 100), true, 0);
        vm.SwitchSubLayout("accents");
        vm.ProcessKeyGaze(Accents, new PhysicalPoint(100, 100), true, 50_000);

        vm.ProcessKeyGaze(Accents, new PhysicalPoint(300, 100), true, 100_000);   // gaze moves to "ê"
        for (long t = 150_000; t <= 1_000_000; t += 50_000)
            vm.ProcessKeyGaze(Accents, new PhysicalPoint(300, 100), true, t);

        Check.That(fired).ContainsExactly("ê");
    }

    [Fact]
    public void AutoReturnSwap_AnchorsAtTheAccentJustTyped()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.SwitchSubLayout("accents");

        // Dwell "é" to completion (the window then calls ActivateKey — simulated here), which
        // auto-returns to letters and anchors suppression at é's old position.
        vm.ProcessKeyGaze(Accents, new PhysicalPoint(100, 100), true, 0);
        for (long t = 50_000; t <= 1_000_000; t += 50_000)
        {
            vm.ProcessKeyGaze(Accents, new PhysicalPoint(100, 100), true, t);
            if (fired.Count > 0) break;
        }
        Check.That(fired).ContainsExactly("é");
        vm.ActivateKey("é", "É");   // the window's role after KeyDwellCompleted
        Check.That(vm.ActiveSubLayoutId).IsEqualTo("letters");

        // Letters' "a" sits where "é" was: resting there must not fire it.
        for (long t = 1_050_000; t <= 3_000_000; t += 50_000)
            vm.ProcessKeyGaze(Letters, new PhysicalPoint(100, 100), true, t);
        Check.That(fired).ContainsExactly("é");
    }

    [Fact]
    public void NoPriorGaze_NoSuppression()
    {
        var vm = Vm();
        var fired = RecordFires(vm);
        vm.SwitchSubLayout("accents");   // swap with no in-window gaze yet: anchor stays null

        for (long t = 0; t <= 1_000_000; t += 50_000)
            vm.ProcessKeyGaze(Accents, new PhysicalPoint(100, 100), true, t);

        Check.That(fired).ContainsExactly("é");   // normal dwell, nothing suppressed
    }
}
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardSwapSuppressionTests"`
  Expected: the two resting tests FAIL (landing keys fire); the no-prior-gaze test PASSES already.

- [ ] **Step 4: Implement.** In `KeyboardViewModel`:

a) Fields beside `_nearestKeyEnteredTimestamp`:

```csharp
    // D13 swap suppression: the gaze position at swap time anchors a one-key suppression; the
    // key that materializes under it (resolved lazily against POST-swap positions — key ids do
    // not carry across sub-layouts) cannot start dwelling until the gaze leaves it.
    private PhysicalPoint? _lastGazeInsideWindowPoint;
    private PhysicalPoint? _swapSuppressedAnchor;
    private string? _swapSuppressedKeyId;
```

b) First statement of `ProcessKeyGaze` after the `keyScreenPositions.Count == 0` guard
(before the `!isGazeInsideWindow` block):

```csharp
        if (isGazeInsideWindow)
            _lastGazeInsideWindowPoint = gazePoint;
```

c) In `SwitchSubLayout`, immediately before `ActiveSubLayoutId = subLayoutId;`:

```csharp
        // D13: anchor the suppression at the last in-window gaze position. Applies to manual
        // switches and auto-return alike — the swap is under the user's resting gaze either way.
        _swapSuppressedAnchor = _lastGazeInsideWindowPoint;
        _swapSuppressedKeyId = null;
```

d) In `ProcessKeyGaze`, after the nearest-key loop and BEFORE `if (nearestId != NearestKeyId)`:

```csharp
        if (_swapSuppressedAnchor is { } anchor)
        {
            if (_swapSuppressedKeyId is null)
            {
                string? suppressed = null;
                var best = double.MaxValue;
                foreach (var (id, center) in keyScreenPositions)
                {
                    var d = anchor.DistanceSquaredTo(center);
                    if (d < best)
                    {
                        best = d;
                        suppressed = id;
                    }
                }

                _swapSuppressedKeyId = suppressed;
            }

            if (nearestId == _swapSuppressedKeyId)
            {
                // D13: the gaze is still resting on the landing key — no dwell accumulation.
                if (NearestKeyId != null)
                {
                    NearestKeyProgress = 0;
                    NearestKeyId = null;
                }
                return;
            }

            _swapSuppressedAnchor = null;
            _swapSuppressedKeyId = null;
        }
```

- [ ] **Step 5: Run to verify pass** — same filter. Expected: 4/4 PASS. Mutation-check: delete
  the suppression block → both resting tests fail; anchor the suppression AFTER resolving
  nearest (i.e. skip the `return`) → resting tests fail.

- [ ] **Step 6: Run the full ViewModel suite once** —
  `dotnet test tests/EyeCtrl.ViewModels.Tests` (one invocation).

- [ ] **Step 7: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/KeyboardSwapSuppressionTests.cs
git commit -m "feat(viewmodels): swap-landing dwell suppression — position-anchored, until gaze leaves (D13)"
```

### Task 4: QWERTZ data — accents sub-layout, `Acc` key (D16 zero-move), `ß` capital (D7)

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/qwertz.json`
- Test: `tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs` (extend)

**Interfaces:**
- Consumes: Task 1's `autoReturn` JSON property; the existing `switchSub:` action vocabulary.
- Produces: `qwertz.json` gains `subLayouts.accents` (`autoReturn: true`) and a letters-footer `Acc` key; `ß` gains `"shiftOutput": "ẞ"`.

- [ ] **Step 1: No impact pass** (data file — no indexed symbols).

- [ ] **Step 2: Write the failing tests** (append to `ShippedLayoutFilesTests`; the class's
  `Load`/`LayoutDir` helpers are reused):

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

        // Spec M4: content rows + footer must not exceed the letters height (QWERTZ ships
        // 3+1, AZERTY 2+1) so the SizeToContent window's height never changes either.
        Check.WithCustomMessage("accents exceeds the 4-row height budget (spec M4)")
            .That(accents.Rows.Count).IsLessOrEqualThan(4);

        var keys = accents.Rows.SelectMany(r => r).ToList();
        Check.WithCustomMessage("every non-special accents key must emit text (spec M2)")
            .That(keys.Where(k => k.SpecialAction is null).All(k => !string.IsNullOrEmpty(k.Output)))
            .IsTrue();
        Check.WithCustomMessage("every lower-case accents key must carry its capital (spec D11)")
            .That(keys.Where(k => k.SpecialAction is null
                                   && k.Output.Length == 1
                                   && char.IsLetter(k.Output[0])
                                   && char.IsLower(k.Output[0]))
                      .All(k => !string.IsNullOrEmpty(k.ShiftOutput)))
            .IsTrue();

        // Widest row must equal the 12.8 standard so the SizeToContent window never resizes
        // between sub-layouts (spec shape rules). No other invariant on narrower rows.
        var widest = accents.Rows.Max(r => r.Sum(k => k.Width));
        Check.WithCustomMessage($"widest accents row is {widest:0.##}, expected 12.8")
            .That(Math.Abs(widest - 12.8) < 0.01).IsTrue();

        // The existing EverySubLayout_CarriesBackspaceAndDeleteWord theory covers Bksp/DelWd;
        // switchSub targets must all resolve (spec M5) — now over the real shipped data.
        var ids = layout.SubLayouts.Keys.ToHashSet();
        var targets = layout.SubLayouts.Values.SelectMany(s => s.Rows.SelectMany(r => r))
            .Select(k => k.SpecialAction)
            .Where(a => a is not null && a.StartsWith("switchSub:", StringComparison.Ordinal))
            .Select(a => a!["switchSub:".Length..]);
        Check.WithCustomMessage("a shipped layout dangles a switchSub: target")
            .That(targets.All(t => ids.Contains(t))).IsTrue();
    }

    [Fact]
    public void Qwertz_LettersFooter_GainsAcc_WithoutMovingAnything()
    {
        var layout = Load("qwertz.json");
        var footer = layout.SubLayouts["letters"].Rows[^1];

        // D16 zero-move: Space narrows 2.0 -> 1.0, Acc (1.0) appended after Tab; total stays
        // 11.7 so the centred row does not shift. Pinned so a future edit cannot silently
        // reshuffle the footer the owner's gaze knows.
        Check.That(footer.Sum(k => k.Width)).IsCloseTo(11.7, 0.01);
        Check.That(footer.Single(k => k.Label == "Space").Width).IsCloseTo(1.0, 0.01);
        var acc = footer.Single(k => k.SpecialAction == "switchSub:accents");
        Check.That(acc.Width).IsCloseTo(1.0, 0.01);
        Check.That(footer.IndexOf(acc)).IsEqualTo(footer.Count - 1);   // appended, last
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
  Expected: the accents/footer/sharp-s tests FAIL (no accents sub-layout, no Acc key, no ẞ);
  the file's pre-existing theories PASS (accents not yet present, so they don't constrain it).

- [ ] **Step 4: Implement.** In `qwertz.json`:

a) Letters footer row 3 — narrow Space and append Acc (total stays 11.7):
`{"label": "Space", "output": "", "width": 2.0, "specialAction": "space"}` becomes width `1.0`,
and after the Tab key append:

```json
{"label": "Acc", "output": "", "width": 1.0, "specialAction": "switchSub:accents"}
```

b) The `ß` key becomes:

```json
{"label": "ß", "output": "ß", "shiftOutput": "ẞ", "width": 1.0}
```

c) Add the accents sub-layout (place it after `"symbols"` in `subLayouts`; rows exactly as
spec §1 — widths make rows 0/1 sum 12.8 and row 2 sum 12.8; no filler keys):

```json
"accents": {
  "label": "Acc",
  "autoReturn": true,
  "rows": [
    [
      {"label": "é", "output": "é", "shiftOutput": "É", "width": 1.28},
      {"label": "É", "output": "É", "width": 1.28},
      {"label": "è", "output": "è", "shiftOutput": "È", "width": 1.28},
      {"label": "È", "output": "È", "width": 1.28},
      {"label": "ê", "output": "ê", "shiftOutput": "Ê", "width": 1.28},
      {"label": "Ê", "output": "Ê", "width": 1.28},
      {"label": "ë", "output": "ë", "shiftOutput": "Ë", "width": 1.28},
      {"label": "Ë", "output": "Ë", "width": 1.28},
      {"label": "à", "output": "à", "shiftOutput": "À", "width": 1.28},
      {"label": "À", "output": "À", "width": 1.28}
    ],
    [
      {"label": "â", "output": "â", "shiftOutput": "Â", "width": 1.28},
      {"label": "Â", "output": "Â", "width": 1.28},
      {"label": "ç", "output": "ç", "shiftOutput": "Ç", "width": 1.28},
      {"label": "Ç", "output": "Ç", "width": 1.28},
      {"label": "î", "output": "î", "shiftOutput": "Î", "width": 1.28},
      {"label": "Î", "output": "Î", "width": 1.28},
      {"label": "ï", "output": "ï", "shiftOutput": "Ï", "width": 1.28},
      {"label": "Ï", "output": "Ï", "width": 1.28},
      {"label": "ô", "output": "ô", "shiftOutput": "Ô", "width": 1.28},
      {"label": "Ô", "output": "Ô", "width": 1.28}
    ],
    [
      {"label": "ù", "output": "ù", "shiftOutput": "Ù", "width": 1.2},
      {"label": "Ù", "output": "Ù", "width": 1.2},
      {"label": "û", "output": "û", "shiftOutput": "Û", "width": 1.2},
      {"label": "Û", "output": "Û", "width": 1.2},
      {"label": "œ", "output": "œ", "shiftOutput": "Œ", "width": 1.2},
      {"label": "Œ", "output": "Œ", "width": 1.2},
      {"label": "«", "output": "«", "width": 1.2},
      {"label": "»", "output": "»", "width": 1.2},
      {"label": "Bksp", "output": "", "width": 1.6, "specialAction": "backspace"},
      {"label": "DelWd", "output": "", "width": 1.6, "specialAction": "deleteWord"}
    ],
    [
      {"label": "ABC", "output": "", "width": 1.5, "specialAction": "switchSub:letters"},
      {"label": "123", "output": "", "width": 1.5, "specialAction": "switchSub:numbers"},
      {"label": "#+=", "output": "", "width": 1.5, "specialAction": "switchSub:symbols"},
      {"label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji"},
      {"label": "Fn", "output": "", "width": 1.0, "specialAction": "switchSub:function"},
      {"label": "Space", "output": "", "width": 2.0, "specialAction": "space"},
      {"label": "Enter", "output": "", "width": 1.5, "specialAction": "enter"},
      {"label": "Tab", "output": "", "width": 1.0, "specialAction": "tab"}
    ]
  ]
}
```

(Row sums: 10 × 1.28 = 12.8; 10 × 1.28 = 12.8; 8 × 1.2 + 2 × 1.6 = 12.8. Content rows 3 +
footer 1 — spec M4. Footer total 11.5, narrower than content rows, matching the shipped
numbers/symbols pattern.)

- [ ] **Step 5: Run to verify pass** — same filter. Expected: ALL PASS including the file's
  pre-existing theories (the new accents rows use only known actions; Bksp/DelWd present).

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/qwertz.json tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs
git commit -m "feat(app): QWERTZ accents sub-layout (28 keys) + zero-move Acc key + ẞ (D2/D16/D7)"
```

### Task 5: AZERTY data — accents tail, zero-move `Acc` on letters row 0, four capitals (D3/D6)

**Files:**
- Modify: `src/EyeCtrl.App/Resources/Layouts/azerty.json`
- Test: `tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs` (extend)

**Interfaces:**
- Consumes: Task 4's shared `AccentsSubLayout_IsAutoReturn_WithWellFormedKeys` theory (already covers azerty).

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
  Expected: the two new tests FAIL (also `AccentsSubLayout…[azerty.json]` still fails until
  Step 4; `[qwertz.json]` passes from Task 4).

- [ ] **Step 4: Implement.** In `azerty.json`:

a) Letters row 0: change `{"label": "Win", … "width": 1.2, "specialAction": "modifier:win"}`
to `"width": 2.0`, and after the `P` key append:

```json
{"label": "Acc", "output": "", "width": 0.8, "specialAction": "switchSub:accents"}
```

b) The four letters accent keys gain `"shiftOutput"`:
`é → "É"`, `è → "È"`, `à → "À"`, `ç → "Ç"` (in their existing rows — two in row 2, two in the
letters footer row).

c) Add the accents sub-layout after `"symbols"` (rows per spec §1; row sums 12.8 each):

```json
"accents": {
  "label": "Acc",
  "autoReturn": true,
  "rows": [
    [
      {"label": "ê", "output": "ê", "shiftOutput": "Ê", "width": 1.28},
      {"label": "Ê", "output": "Ê", "width": 1.28},
      {"label": "â", "output": "â", "shiftOutput": "Â", "width": 1.28},
      {"label": "Â", "output": "Â", "width": 1.28},
      {"label": "î", "output": "î", "shiftOutput": "Î", "width": 1.28},
      {"label": "Î", "output": "Î", "width": 1.28},
      {"label": "ô", "output": "ô", "shiftOutput": "Ô", "width": 1.28},
      {"label": "Ô", "output": "Ô", "width": 1.28},
      {"label": "û", "output": "û", "shiftOutput": "Û", "width": 1.28},
      {"label": "Û", "output": "Û", "width": 1.28}
    ],
    [
      {"label": "ë", "output": "ë", "shiftOutput": "Ë", "width": 1.0},
      {"label": "Ë", "output": "Ë", "width": 1.0},
      {"label": "ï", "output": "ï", "shiftOutput": "Ï", "width": 1.0},
      {"label": "Ï", "output": "Ï", "width": 1.0},
      {"label": "ù", "output": "ù", "shiftOutput": "Ù", "width": 1.0},
      {"label": "Ù", "output": "Ù", "width": 1.0},
      {"label": "œ", "output": "œ", "shiftOutput": "Œ", "width": 1.0},
      {"label": "Œ", "output": "Œ", "width": 1.0},
      {"label": "«", "output": "«", "width": 1.0},
      {"label": "»", "output": "»", "width": 1.0},
      {"label": "Bksp", "output": "", "width": 1.4, "specialAction": "backspace"},
      {"label": "DelWd", "output": "", "width": 1.4, "specialAction": "deleteWord"}
    ],
    [
      {"label": "ABC", "output": "", "width": 1.5, "specialAction": "switchSub:letters"},
      {"label": "123", "output": "", "width": 1.5, "specialAction": "switchSub:numbers"},
      {"label": "#+=", "output": "", "width": 1.5, "specialAction": "switchSub:symbols"},
      {"label": "😀", "output": "", "width": 1.0, "specialAction": "switchEmoji"},
      {"label": "Fn", "output": "", "width": 1.0, "specialAction": "switchSub:function"},
      {"label": "Space", "output": "", "width": 2.0, "specialAction": "space"},
      {"label": "Enter", "output": "", "width": 1.5, "specialAction": "enter"},
      {"label": "Tab", "output": "", "width": 1.0, "specialAction": "tab"}
    ]
  ]
}
```

(Row sums: 10 × 1.28 = 12.8; 10 × 1.0 + 2 × 1.4 = 12.8. `ù Ù` are in the tail because `ù`
lives on symbols today — symbols is left untouched, spec R2.)

- [ ] **Step 5: Run to verify pass** — same filter. Expected: ALL PASS (both theory cases + all
  pre-existing theories).

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add src/EyeCtrl.App/Resources/Layouts/azerty.json tests/EyeCtrl.App.Tests/Keyboard/ShippedLayoutFilesTests.cs
git commit -m "feat(app): AZERTY accents tail (20 keys) + zero-move Acc + É È À Ç (D3/D6)"
```

### Task 6: Real-window probe at scale 2.0 + competitive-doc close-out

**Files:**
- Test: `tests/EyeCtrl.App.Tests/Keyboard/KeyboardGateProbeTests.cs` (extend — reuse `Open`, `DwellOn`, `RoundTrip`, `ShippedEmojiJson`; add a `ShippedQwertz` loader mirroring `ShippedQwerty` at :118)
- Modify: `docs/COMPETITIVE_FEATURE_ANALYSIS.md` (gap #11 → resolved; queue re-rank)

**Interfaces:**
- Consumes: Tasks 1–5 (the shipped QWERTZ accents data + engine rules).

- [ ] **Step 1: No impact pass** (test + docs).

- [ ] **Step 2: Write the failing test.** Add beside `ShippedQwerty`:

```csharp
    private static KeyboardLayout ShippedQwertz() =>
        KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
```

and the probe test (the harness's `DwellOn` moves the gaze to the target key's centre, which
is what clears D13's suppression between dwells — spec I6's fixture caveat is satisfied by
using the existing helpers, never re-dwelling the same spot without a move):

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

            // Auto-return: dwell Acc, then dwell é — é types and the keyboard is back on letters.
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "é", ref ts);
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

            DwellOn(probe, "é", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
        }
        finally { probe.Dispose(); }
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ShippedQwertz_Accents"`.
  Expected: FAIL (the helper/test are new and the round trip cannot pass until run against the
  Task 4/5 data — with those landed, this step's RED check is the compile of the test itself;
  if Tasks 4/5 are complete the test may pass immediately — that is acceptable; note it).

- [ ] **Step 4: Docs.** In `docs/COMPETITIVE_FEATURE_ANALYSIS.md`: change the Tier-2 table's
  row 11 to a resolved note ("**Shipped 2026-09-24** — per-layout `accents` sub-layouts with
  auto-return on `feat/accent-input` (QWERTZ 28-key French-in-German set, AZERTY 20-key tail);
  dead-key engine declined as the revisit path (spec D1)") and remove item 1 from the
  value-ranked queue so "Dwell-completion audio cue" becomes the head.

- [ ] **Step 5: Run the probe + neighbors** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardGateProbe|FullyQualifiedName~ShippedLayoutFilesTests|FullyQualifiedName~KeyboardGateCensus"`
  Expected: ALL PASS.

- [ ] **Step 6: detect_changes + diff check, then commit**

```bash
git add tests/EyeCtrl.App.Tests/Keyboard/KeyboardGateProbeTests.cs docs/COMPETITIVE_FEATURE_ANALYSIS.md
git commit -m "test(app): shipped-QWERTZ accents probe at scale 2.0; close competitive gap #11"
```

---

## Final verification

- [ ] Full non-UI suite, one project per invocation or the `EyeCtrl.slnx` form:
  `dotnet test EyeCtrl.slnx --filter "Category!=UI"` — expect zero failures against the
  pre-branch baseline (all projects were green at `2e1d9078`).
- [ ] `dotnet build EyeCtrl.slnx` clean.
- [ ] Report per task with test counts. Live pass (launching the app to type an accent by
  gaze) is owner-gated and NOT part of this plan's execution.
