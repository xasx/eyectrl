# Gaze Trail Drag & Drop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two-cycle DragStart/DragEnd action menu flow with a one-cycle gaze trail drag that uses an elastic trail, settle-to-drop, and on-demand zoom magnification.

**Architecture:** New `InteractionState.Dragging` state in the state machine. `InteractionManager.OnActionSelected` special-cases `Drag` to enter `Dragging` instead of resetting to `FreeLook`. A new `DragTrailViewModel` manages drag sub-states (free drag, settling, zoom active) and dwell detection for release/back/cancel buttons. A new `DragTrailOverlay` (transparent, click-through WPF window) renders the elastic trail, floating buttons, progress ring, and reuses `ManualSelectionOverlayWindow` for zoom. `EventCoordinator` orchestrates MouseDown/MouseUp and overlay lifecycle.

**Tech Stack:** C# / .NET 10 / WPF / CommunityToolkit.Mvvm / WeakReferenceMessenger / NFluent / xUnit

---

## File Structure

### New Files

| File | Project | Responsibility |
|---|---|---|
| `src/EyeCtrl.Core/Settings/DragSettings.cs` | EyeCtrl.Core | Trail/settle parameters (ObservableObject + IValidatable) |
| `src/EyeCtrl.ViewModels/DragTrailViewModel.cs` | EyeCtrl.ViewModels | Drag sub-state machine, settle detection, dwell for buttons |
| `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml` | EyeCtrl.App | XAML: Canvas with trail line, floating buttons, progress ring |
| `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs` | EyeCtrl.App | Code-behind: OverlayWindow subclass, rendering updates |
| `tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs` | EyeCtrl.Core.Tests | DragSettings validation tests |
| `tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs` | EyeCtrl.ViewModels.Tests | ViewModel state transition and dwell tests |

### Modified Files

| File | Changes |
|---|---|
| `src/EyeCtrl.Core/Models/InteractionAction.cs` | Rename `DragStart` → `Drag`, remove `DragEnd` |
| `src/EyeCtrl.Core/Models/InteractionState.cs` | Add `Dragging` |
| `src/EyeCtrl.Core/Settings/DwellSettings.cs` | Add `DragReleaseButtonDwellMs`, `DragRightEdgeButtonDwellMs` |
| `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `Drag` property, wire into `Validate()` |
| `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` | Rename `DragStart` → `Drag` in defaults |
| `src/EyeCtrl.Core/Settings/SettingsService.cs` | Pre-deserialization JSON migration for `"dragStart"` → `"drag"` |
| `src/EyeCtrl.Core/Processing/InteractionManager.cs` | Special-case `Drag` in `OnActionSelected`, add `DragStarted` event |
| `src/EyeCtrl.Core/GazeEngine.cs` | Widen cursor movement gate to include `Dragging` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add drag lifecycle messages |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Update glyph map |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs` | Add `Drag` to `ResetAllSettings()` |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Update glyph map |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Subscribe `InteractionManager.DragStarted`; register drag messages; render overlay from `DragTrailVM.PropertyChanged`; remove old `DragStart`/`DragEnd` switch cases; unsubscribe in `Dispose` |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Add `DragTrailViewModel DragTrailVM` to the `EventCoordinationContext` record (this is how `EventCoordinator` reaches the VM — it accesses everything via `_context.*`, not via `OverlayManager`) |
| `src/EyeCtrl.App/App.xaml.cs` | Resolve `DragTrailViewModel` from DI and pass it into the `EventCoordinationContext` (NOT `OverlayViewModels` — the drag overlay is owned by `EventCoordinator`, not `OverlayManager`) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `DragTrailViewModel` in DI |
| `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs` | Add `Drag`-specific transition tests |
| `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs` | Add migration test |
| `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs` | Update the `InteractionAction.DragStart` reference (line ~40) to `InteractionAction.Drag` |

---

### Task 1: DragSettings + DwellSettings Additions

**Files:**
- Create: `src/EyeCtrl.Core/Settings/DragSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs`

- [ ] **Step 1: Write DragSettings tests**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class DragSettingsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var s = new DragSettings();
        Check.That(s.SettleDwellMs).IsEqualTo(500);
        Check.That(s.SettleRadiusPixels).IsEqualTo(40);
        Check.That(s.TrailColor).IsEqualTo("#ff9800");
        Check.That(s.TrailOpacity).IsEqualTo(0.7);
    }

    [Fact]
    public void SettleDwellMs_ClampsToRange()
    {
        var s = new DragSettings();
        s.SettleDwellMs = 50;
        Check.That(s.SettleDwellMs).IsEqualTo(200);
        s.SettleDwellMs = 5000;
        Check.That(s.SettleDwellMs).IsEqualTo(2000);
    }

    [Fact]
    public void SettleRadiusPixels_ClampsToRange()
    {
        var s = new DragSettings();
        s.SettleRadiusPixels = 2;
        Check.That(s.SettleRadiusPixels).IsEqualTo(10);
        s.SettleRadiusPixels = 500;
        Check.That(s.SettleRadiusPixels).IsEqualTo(200);
    }

    [Fact]
    public void TrailOpacity_ClampsToRange()
    {
        var s = new DragSettings();
        s.TrailOpacity = 0.0;
        Check.That(s.TrailOpacity).IsEqualTo(0.1);
        s.TrailOpacity = 2.0;
        Check.That(s.TrailOpacity).IsEqualTo(1.0);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForDefaults()
    {
        var s = new DragSettings();
        Check.That(s.Validate()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "DragSettingsTests" --no-restore`
Expected: FAIL — `DragSettings` type does not exist yet.

- [ ] **Step 3: Create DragSettings**

```csharp
// src/EyeCtrl.Core/Settings/DragSettings.cs
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DragSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _settleDwellMs = 500;
    [ObservableProperty] private int _settleRadiusPixels = 40;
    [ObservableProperty] private string _trailColor = "#ff9800";
    [ObservableProperty] private double _trailOpacity = 0.7;

    partial void OnSettleDwellMsChanging(int value)
    {
        if (value < 200) _settleDwellMs = 200;
        else if (value > 2000) _settleDwellMs = 2000;
    }

    partial void OnSettleRadiusPixelsChanging(int value)
    {
        if (value < 10) _settleRadiusPixels = 10;
        else if (value > 200) _settleRadiusPixels = 200;
    }

    partial void OnTrailOpacityChanging(double value)
    {
        if (value < 0.1) _trailOpacity = 0.1;
        else if (value > 1.0) _trailOpacity = 1.0;
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(SettleDwellMs, 200, 2000, nameof(SettleDwellMs)),
            ValidationHelper.ValidateRange(SettleRadiusPixels, 10, 200, nameof(SettleRadiusPixels)),
            ValidationHelper.ValidateRange(TrailOpacity, 0.1, 1.0, nameof(TrailOpacity))
        );
    }
}
```

- [ ] **Step 4: Add drag dwell times to DwellSettings**

In `src/EyeCtrl.Core/Settings/DwellSettings.cs`, add two new properties after the existing dwell properties (around line 27):

```csharp
[ObservableProperty] private int _dragReleaseButtonDwellMs = 600;
[ObservableProperty] private int _dragRightEdgeButtonDwellMs = 600;
```

Add range guards. **`DwellSettings` validates by throwing** (see the existing `OnActivationButtonDwellMsChanging` etc.) — match that convention here, not the clamping style used in `DragSettings`:

```csharp
partial void OnDragReleaseButtonDwellMsChanging(int value)
{
    if (value < 200 || value > 2000)
        throw new ArgumentOutOfRangeException(
            nameof(value), value, $"{nameof(DragReleaseButtonDwellMs)} must be between 200 and 2000");
}

partial void OnDragRightEdgeButtonDwellMsChanging(int value)
{
    if (value < 200 || value > 2000)
        throw new ArgumentOutOfRangeException(
            nameof(value), value, $"{nameof(DragRightEdgeButtonDwellMs)} must be between 200 and 2000");
}
```

Add to `Validate()` method's `ValidationHelper.CollectErrors` call:

```csharp
ValidationHelper.ValidateRange(DragReleaseButtonDwellMs, 200, 2000, nameof(DragReleaseButtonDwellMs)),
ValidationHelper.ValidateRange(DragRightEdgeButtonDwellMs, 200, 2000, nameof(DragRightEdgeButtonDwellMs)),
```

- [ ] **Step 5: Wire DragSettings into EyeCtrlSettings**

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`, add property after `EyeCare` (around line 38):

```csharp
[ObservableProperty] private DragSettings _drag = new();
```

In `Validate()`, add after the `EyeCare` validation block:

```csharp
if (Drag is null)
    errors.Add("Drag settings must not be null.");
else
    errors.AddRange(Drag.Validate());
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "DragSettingsTests" --no-restore`
Expected: PASS

- [ ] **Step 7: Run full build to verify no regressions**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Core/Settings/DragSettings.cs src/EyeCtrl.Core/Settings/DwellSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs
git commit -m "feat: add DragSettings and drag dwell times to DwellSettings"
```

---

### Task 2: Rename InteractionAction (DragStart → Drag, remove DragEnd)

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionAction.cs`
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

This is a rename + removal that touches many files. Do all changes in one step to keep the build green.

- [ ] **Step 1: Rename DragStart to Drag in InteractionAction enum**

In `src/EyeCtrl.Core/Models/InteractionAction.cs`, change:

```csharp
DragStart,
DragEnd,
```

to:

```csharp
Drag,
```

- [ ] **Step 2: Update ActionMenuSettings defaults**

In `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`, change both occurrences of `InteractionAction.DragStart` to `InteractionAction.Drag`:

In the `_visibleActions` field initializer (line 13):
```csharp
InteractionAction.DoubleClick, InteractionAction.Drag,
```

In `EnsureDefaults()` (line 33):
```csharp
InteractionAction.DoubleClick, InteractionAction.Drag,
```

- [ ] **Step 3: Update ActionMenuViewModel glyph map**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, change the `GetActionSymbol` switch (lines 118-131):

Replace:
```csharp
InteractionAction.DragStart => "\u270A",      // ✊ Raised fist (grab)
InteractionAction.DragEnd => "\u270B",        // ✋ Raised hand (release)
```

With:
```csharp
InteractionAction.Drag => "\u270A",           // ✊ Raised fist (grab)
```

- [ ] **Step 4: Update ActionMenuWindow glyph map**

In `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`, change the `GetActionSymbol` switch (lines 297-310):

Replace:
```csharp
InteractionAction.DragStart => "\u270A",     // ✊ Raised fist (grab)
InteractionAction.DragEnd => "\u270B",       // ✋ Raised hand (release)
```

With:
```csharp
InteractionAction.Drag => "\u270A",          // ✊ Raised fist (grab)
```

- [ ] **Step 5: Update EventCoordinator HandleActionRequested**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, in the `switch` block inside `HandleActionRequested` (around lines 793-804), remove both `DragStart` and `DragEnd` cases:

```csharp
case InteractionAction.DragStart:    _context.InputSimulator.MouseDown(pt);        break;
case InteractionAction.DragEnd:      _context.InputSimulator.MouseUp(pt);          break;
```

These will be replaced by the new `DragStarted` event handler in Task 7.

- [ ] **Step 6: Fix test references to DragStart/DragEnd**

The enum rename is a **compile-time** break: any test still referencing the old members fails the *build*, not just the test run, so fix these before building. There is currently one known reference — `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs:40` uses `InteractionAction.DragStart`. Search all test files for `DragStart` / `DragEnd`, replace `InteractionAction.DragStart` with `InteractionAction.Drag`, and remove any `DragEnd` references.

- [ ] **Step 7: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors. (A `DragStart`/`DragEnd` reference left anywhere — src or tests — surfaces here as a compile error, not a test failure.)

- [ ] **Step 8: Run all tests**

Run: `dotnet test --filter "Category!=UI"`
Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: rename DragStart to Drag, remove DragEnd from InteractionAction"
```

---

### Task 3: Settings Migration (pre-deserialization JSON rewrite)

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs`

- [ ] **Step 1: Write migration test**

In `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs`, add:

```csharp
[Fact]
public void Load_MigratesDragStartToDrag_InVisibleActions()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
    try
    {
        var json = """
        {
            "actionMenu": {
                "visibleActions": ["leftClick", "rightClick", "doubleClick", "dragStart", "scrollUp", "scrollDown", "pinNote"]
            },
            "selectionMode": "ManualSelection",
            "dwell": { "activationButtonDwellMs": 800 }
        }
        """;
        File.WriteAllText(tempPath, json);
        var service = new SettingsService(tempPath);
        var settings = service.Load();

        Check.That(settings.ActionMenu.VisibleActions).Contains(InteractionAction.Drag);
        Check.That(settings.ActionMenu.VisibleActions).Not.Contains(InteractionAction.DragStart);
        Check.That(settings.SelectionMode).IsEqualTo(SelectionMode.ManualSelection);
        Check.That(settings.Dwell.ActivationButtonDwellMs).IsEqualTo(800);
    }
    finally { File.Delete(tempPath); }
}

[Fact]
public void Load_RemovesDragEnd_FromVisibleActions()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
    try
    {
        var json = """
        {
            "actionMenu": {
                "visibleActions": ["leftClick", "dragEnd", "scrollUp"]
            }
        }
        """;
        File.WriteAllText(tempPath, json);
        var service = new SettingsService(tempPath);
        var settings = service.Load();

        Check.That(settings.ActionMenu.VisibleActions).Not.Contains(InteractionAction.DragEnd);
        Check.That(settings.ActionMenu.VisibleActions).Contains(InteractionAction.Drag);
    }
    finally { File.Delete(tempPath); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "SettingsServiceTests" --no-restore`
Expected: FAIL — `DragStart` is an unknown enum string, deserializer throws, `Load()` returns defaults (which happen to contain `Drag`, but `SelectionMode` and `Dwell` values are lost).

- [ ] **Step 3: Add pre-deserialization migration to SettingsService.Load()**

In `src/EyeCtrl.Core/Settings/SettingsService.cs`, in the `Load()` method, after reading the JSON text (around line 92) and before `JsonSerializer.Deserialize`, add:

```csharp
json = MigrateDragActions(json);
```

Add the migration method to the class:

```csharp
private static string MigrateDragActions(string json)
{
    if (!json.Contains("\"dragStart\"", StringComparison.OrdinalIgnoreCase) &&
        !json.Contains("\"dragEnd\"", StringComparison.OrdinalIgnoreCase))
        return json;

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    if (!root.TryGetProperty("actionMenu", out var actionMenu) ||
        !actionMenu.TryGetProperty("visibleActions", out var visibleActions))
        return json;

    var actions = new List<string>();
    bool modified = false;

    foreach (var item in visibleActions.EnumerateArray())
    {
        var value = item.GetString()!;
        if (value == "dragStart")
        {
            actions.Add("drag");
            modified = true;
        }
        else if (value == "dragEnd")
        {
            modified = true;
        }
        else
        {
            actions.Add(value);
        }
    }

    if (!modified)
        return json;

    if (!actions.Contains("drag"))
        actions.Add("drag");

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "actionMenu")
            {
                writer.WritePropertyName("actionMenu");
                writer.WriteStartObject();
                foreach (var amProp in prop.Value.EnumerateObject())
                {
                    if (amProp.Name == "visibleActions")
                    {
                        writer.WritePropertyName("visibleActions");
                        writer.WriteStartArray();
                        foreach (var a in actions)
                            writer.WriteStringValue(a);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        amProp.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}
```

Add `using System.Text;` to the file's usings if not already present.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "SettingsServiceTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/SettingsService.cs tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs
git commit -m "feat: migrate dragStart/dragEnd in settings JSON before deserialization"
```

---

### Task 4: Add Dragging to InteractionState + InteractionManager Changes

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionState.cs`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

- [ ] **Step 1: Write InteractionManager Drag transition tests**

In `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`, add:

```csharp
[Fact]
public void OnActionSelected_Drag_FromActionMenu_TransitionsToDragging()
{
    var manager = new InteractionManager();
    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.OnActivationButtonDwelled();
    Check.That(manager.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

    manager.OnActionSelected(InteractionAction.Drag);

    Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);
}

[Fact]
public void OnActionSelected_Drag_FromManualSelectionActionMenu_TransitionsToDragging()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
    bool endManualSelectionFired = false;
    manager.EndManualSelection += () => endManualSelectionFired = true;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.OnActivationButtonDwelled();
    manager.OnManualSelectionDwell(new PhysicalPoint(110, 110));
    Check.That(manager.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

    manager.OnActionSelected(InteractionAction.Drag);

    Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);
    Check.That(endManualSelectionFired).IsTrue();
}

[Fact]
public void OnActionSelected_Drag_DoesNotFireActionRequested()
{
    var manager = new InteractionManager();
    bool actionRequestedFired = false;
    manager.ActionRequested += (_, _) => actionRequestedFired = true;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.Drag);

    Check.That(actionRequestedFired).IsFalse();
}

[Fact]
public void OnActionSelected_Drag_FiresDragStarted_WithResolvedOrigin()
{
    var manager = new InteractionManager();
    PhysicalPoint? dragOrigin = null;
    manager.DragStarted += (origin) => dragOrigin = origin;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.Drag);

    Check.That(dragOrigin).IsEqualTo(new PhysicalPoint(100, 100));
}

[Fact]
public void OnActionSelected_Drag_AutoMode_ResolvesControlCenter()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
    PhysicalPoint? dragOrigin = null;
    manager.DragStarted += (origin) => dragOrigin = origin;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.Drag);

    Check.That(dragOrigin).IsEqualTo(new PhysicalPoint(175, 175));
}

[Fact]
public void CompleteDrag_ReturnsToFreeLook()
{
    var manager = new InteractionManager();
    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.Drag);
    Check.That(manager.CurrentState).IsEqualTo(InteractionState.Dragging);

    manager.CompleteDrag();

    Check.That(manager.CurrentState).IsEqualTo(InteractionState.FreeLook);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "InteractionManagerTests" --no-restore`
Expected: FAIL — `Dragging` state and `DragStarted` event don't exist.

- [ ] **Step 3: Add Dragging to InteractionState enum**

In `src/EyeCtrl.Core/Models/InteractionState.cs`, add before `Paused`:

```csharp
/// <summary>A drag operation is in progress — cursor follows gaze with elastic trail.</summary>
Dragging,
```

- [ ] **Step 4: Add DragStarted event and special-case Drag in InteractionManager**

In `src/EyeCtrl.Core/Processing/InteractionManager.cs`:

Add event declaration (after existing events, around line 42):

```csharp
/// <summary>Fired when a Drag action is selected. Carries the resolved grab origin point.</summary>
public event Action<PhysicalPoint>? DragStarted;
```

Modify `OnActionSelected`. **Preserve the existing method's structure exactly** — it already resolves the target point via a `switch` over public properties (`AutoSelectionTarget`, `FixationPoint`, `ManualSelectionDwellPoint` — these are public properties, **not** private fields), null-guards, then calls `HideActionMenu` and (in ManualSelection) `EndManualSelection` before firing the action. Only the tail changes: special-case `Drag` to enter `Dragging` instead of `ActionRequested` + `ResetToFreeLook()`.

```csharp
public void OnActionSelected(InteractionAction action)
{
    PhysicalPoint? targetPoint = CurrentState switch
    {
        InteractionState.ActionMenuVisible
            when SelectionMode == SelectionMode.AutoSelection && AutoSelectionTarget != null
                => AutoSelectionTarget.CenterPoint,
        InteractionState.ActionMenuVisible
                => FixationPoint,
        InteractionState.ManualSelectionActionMenuVisible
                => ManualSelectionDwellPoint,
        _       => null
    };

    if (targetPoint == null) return;

    // Preserved from the original method — must run for ALL actions, including Drag.
    HideActionMenu?.Invoke();
    if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        EndManualSelection?.Invoke();

    if (action == InteractionAction.Drag)
    {
        SetState(InteractionState.Dragging);
        DragStarted?.Invoke(targetPoint.Value);
        return;
    }

    ActionRequested?.Invoke(action, targetPoint.Value);
    ResetToFreeLook();
}
```

> **Why this matters:** the plan's earlier draft used private fields `_manualSelectionDwellPoint` / `_autoSelectionTarget` / `_fixationPoint` (which don't exist — they're auto-properties, so it wouldn't compile) and omitted the `HideActionMenu` / `EndManualSelection` calls, which would regress every *non*-drag action (action menu would stay open, manual-selection overlay would never close).

Add `CompleteDrag()` method:

```csharp
/// <summary>Ends the drag operation and returns to FreeLook.</summary>
public void CompleteDrag()
{
    if (CurrentState != InteractionState.Dragging) return;
    ResetToFreeLook();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "InteractionManagerTests" --no-restore`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/InteractionState.cs src/EyeCtrl.Core/Processing/InteractionManager.cs tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs
git commit -m "feat: add Dragging state and DragStarted event to InteractionManager"
```

---

### Task 5: Widen GazeEngine Cursor Movement Gate

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`

- [ ] **Step 1: Modify the cursor movement gate**

In `src/EyeCtrl.Core/GazeEngine.cs`, in `OnGazePoint` (around lines 130-133), change:

```csharp
if (InteractionManager.CurrentState == InteractionState.FreeLook)
{
    _inputSimulator.MoveCursor(filtered);
}
```

To:

```csharp
if (InteractionManager.CurrentState is InteractionState.FreeLook or InteractionState.Dragging)
{
    _inputSimulator.MoveCursor(filtered);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run all tests**

Run: `dotnet test --filter "Category!=UI"`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: allow cursor movement during Dragging state"
```

---

### Task 6: Add Drag Messages + Settings Wiring

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add drag lifecycle messages**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, add at the end of the file (before the closing of the namespace):

```csharp
// Drag trail messages
/// <summary>Sent by InteractionManager (via EventCoordinator) to start a drag trail. Origin is the grab point in physical screen pixels.</summary>
public record StartDragTrailMessage(PhysicalPoint GrabOrigin);

/// <summary>Sent by DragTrailViewModel when the drag should complete (drop at current cursor position).</summary>
public record DragDropMessage(PhysicalPoint DropPoint);

/// <summary>Sent by DragTrailViewModel when the drag should be cancelled (MouseUp at origin).</summary>
public record DragCancelMessage;

/// <summary>Sent by DragTrailViewModel when zoom should be dismissed (back to free drag).</summary>
public record DragDismissZoomMessage;
```

- [ ] **Step 2: Add Drag to SettingsViewModel.ResetAllSettings()**

In `src/EyeCtrl.ViewModels/SettingsViewModel.cs`, in `ResetAllSettings()` (around line 215), add after the `EyeCare` copy:

```csharp
CopySettings(defaults.Drag, _settings.Drag);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat: add drag trail messages and wire DragSettings into ResetAllSettings"
```

---

### Task 7: DragTrailViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/DragTrailViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs`

- [ ] **Step 1: Write DragTrailViewModel tests**

```csharp
// tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class DragTrailViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    private DragTrailViewModel CreateVm() => new(_settings, _messenger);

    [Fact]
    public void StartDrag_SetsGrabOrigin_AndIsActive()
    {
        var vm = CreateVm();
        var origin = new PhysicalPoint(500, 300);

        _messenger.Send(new StartDragTrailMessage(origin));

        Check.That(vm.IsActive).IsTrue();
        Check.That(vm.GrabOrigin).IsEqualTo(origin);
        Check.That(vm.DragState).IsEqualTo(DragSubState.FreeDrag);
    }

    [Fact]
    public void GazeUpdate_UpdatesCursorPosition()
    {
        var vm = CreateVm();
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), 0));

        Check.That(vm.CursorPosition).IsEqualTo(new PhysicalPoint(200, 200));
    }

    [Fact]
    public void Settle_ProgressFills_WhenGazeStops()
    {
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 500;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        var pos = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos, 250_000));

        Check.That(vm.SettleProgress).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Settle_CompletesAndSendsDragDrop()
    {
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 200;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        DragDropMessage? dropMsg = null;
        _messenger.Register<DragDropMessage>(_messenger, (_, msg) => dropMsg = msg);

        var pos = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos, 300_000));

        Check.That(dropMsg).IsNotNull();
        Check.That(vm.IsActive).IsFalse();
    }

    [Fact]
    public void GazeMove_ResetsSettleProgress()
    {
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 500;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        var pos1 = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos1, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos1, 200_000));
        Check.That(vm.SettleProgress).IsStrictlyGreaterThan(0);

        var pos2 = new PhysicalPoint(500, 500);
        _messenger.Send(new GazeCursorMovedMessage(pos2, 300_000));
        Check.That(vm.SettleProgress).IsEqualTo(0);
    }

    [Fact]
    public void CancelButton_DwellSendsDragCancel()
    {
        var vm = CreateVm();
        _settings.Dwell.DragRightEdgeButtonDwellMs = 200;
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        DragCancelMessage? cancelMsg = null;
        _messenger.Register<DragCancelMessage>(_messenger, (_, msg) => cancelMsg = msg);

        var cancelPos = vm.CancelButtonCenter;
        _messenger.Send(new GazeCursorMovedMessage(cancelPos, 0));
        _messenger.Send(new GazeCursorMovedMessage(cancelPos, 300_000));

        Check.That(cancelMsg).IsNotNull();
        Check.That(vm.IsActive).IsFalse();
    }

    [Fact]
    public void ReleaseButton_DwellSendsDragDrop()
    {
        var vm = CreateVm();
        _settings.Dwell.DragReleaseButtonDwellMs = 200;
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 300), 0));

        DragDropMessage? dropMsg = null;
        _messenger.Register<DragDropMessage>(_messenger, (_, msg) => dropMsg = msg);

        var releasePos = vm.ReleaseButtonCenter;
        _messenger.Send(new GazeCursorMovedMessage(releasePos, 100_000));
        _messenger.Send(new GazeCursorMovedMessage(releasePos, 400_000));

        Check.That(dropMsg).IsNotNull();
    }

    [Fact]
    public void ShowBack_IsFalse_InFreeDrag()
    {
        var vm = CreateVm();
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        Check.That(vm.ShowBack).IsFalse();
    }

    [Fact]
    public void CancelButtonCenter_IsOnRightEdge()
    {
        var vm = CreateVm();
        _messenger.Send(new StartDragTrailMessage(new PhysicalPoint(100, 100)));

        Check.That(vm.CancelButtonCenter.X).IsStrictlyGreaterThan(1800);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "DragTrailViewModelTests" --no-restore`
Expected: FAIL — `DragTrailViewModel` does not exist.

- [ ] **Step 3: Create DragTrailViewModel**

```csharp
// src/EyeCtrl.ViewModels/DragTrailViewModel.cs
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public enum DragSubState
{
    FreeDrag,
    Settling,
    ZoomActive
}

public partial class DragTrailViewModel : ObservableObject,
    IRecipient<StartDragTrailMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string ReleaseTargetId = "Drag_release";
    private const string CancelTargetId = "Drag_cancel";
    private const string BackTargetId = "Drag_back";

    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _dwellDetector;
    private readonly FixationDetector _settleDetector;
    private bool _disposed;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private PhysicalPoint _grabOrigin;
    [ObservableProperty] private PhysicalPoint _cursorPosition;
    [ObservableProperty] private DragSubState _dragState = DragSubState.FreeDrag;
    [ObservableProperty] private double _settleProgress;
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private PhysicalPoint _releaseButtonCenter;
    [ObservableProperty] private PhysicalPoint _cancelButtonCenter;
    [ObservableProperty] private PhysicalPoint _backButtonCenter;

    public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;

        _settleDetector = new FixationDetector(
            settings.Drag.SettleRadiusPixels,
            settings.Drag.SettleDwellMs);
        _settleDetector.FixationStarted += OnSettleStarted;
        _settleDetector.FixationEnded += OnSettleEnded;

        messenger.RegisterAll(this);
    }

    public void Receive(StartDragTrailMessage message)
    {
        GrabOrigin = message.GrabOrigin;
        CursorPosition = message.GrabOrigin;
        IsActive = true;
        DragState = DragSubState.FreeDrag;
        SettleProgress = 0;
        ShowBack = false;

        UpdateButtonPositions(message.GrabOrigin);
        SetupDwellTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (!IsActive) return;

        CursorPosition = message.Position;
        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);

        if (DragState == DragSubState.FreeDrag || DragState == DragSubState.Settling)
        {
            _settleDetector.Update(message.Position, message.TimestampMicroseconds);
            SettleProgress = _settleDetector.GetProgress();

            if (SettleProgress > 0 && DragState == DragSubState.FreeDrag)
                DragState = DragSubState.Settling;
            else if (SettleProgress == 0 && DragState == DragSubState.Settling)
                DragState = DragSubState.FreeDrag;
        }

        UpdateReleaseButtonPosition(message.Position);
    }

    // FixationDetector fires FixationStarted only AFTER the full settle dwell elapses,
    // i.e. when the progress ring reaches 100%. That is the drop-confirmation moment.
    private void OnSettleStarted(PhysicalPoint point)
    {
        if (_settings.SelectionMode == SelectionMode.ManualSelection)
        {
            // Enter precision refinement: show the ↩ back button and (per the design) a zoom
            // magnifier. The drop is NOT auto-confirmed here — the user fine-tunes, then dwells
            // ✋ release to drop. NOTE: actually displaying the magnifier and translating the
            // refined point into the drop location is an UNRESOLVED integration — see the
            // "Open design question: zoom during drag" note below. Do not assume reusing
            // ManualSelectionOverlay via StartManualSelectionMessage works as-is.
            DragState = DragSubState.ZoomActive;
            ShowBack = true;
            SetupDwellTargetsWithBack();
        }
        else
        {
            // None / AutoSelection: ring reached 100% → confirm the drop at the settle point.
            CompleteDrag(point);
        }
    }

    // FixationEnded fires when gaze LEAVES the settle radius. That is an interruption, never a
    // confirmation — reset the settle and return to free drag regardless of sub-state.
    private void OnSettleEnded()
    {
        SettleProgress = 0;
        if (DragState == DragSubState.Settling)
            DragState = DragSubState.FreeDrag;
        // ZoomActive intentionally persists: leaving the settle radius while zoomed does not
        // drop and does not exit zoom (the user is aiming). Zoom is exited only via ↩ back.
    }

    private void UpdateButtonPositions(PhysicalPoint cursorPos)
    {
        var screenWidth = DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
        var buttonX = screenWidth - DpiHelper.LogicalToPhysicalX(30);
        var centerY = DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2.0);

        CancelButtonCenter = new PhysicalPoint(buttonX, centerY + DpiHelper.LogicalToPhysicalY(30));
        BackButtonCenter = new PhysicalPoint(buttonX, centerY - DpiHelper.LogicalToPhysicalY(30));

        UpdateReleaseButtonPosition(cursorPos);
    }

    private void UpdateReleaseButtonPosition(PhysicalPoint cursorPos)
    {
        var offset = DpiHelper.LogicalToPhysicalX(25);
        ReleaseButtonCenter = new PhysicalPoint(cursorPos.X - offset, cursorPos.Y - offset);
    }

    private void SetupDwellTargets()
    {
        _dwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(20);

        _dwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
    }

    private void SetupDwellTargetsWithBack()
    {
        _dwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(20);

        _dwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            BackTargetId, BackButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == ReleaseTargetId)
            CompleteDrag(CursorPosition);
        else if (targetId == CancelTargetId)
            CancelDrag();
        else if (targetId == BackTargetId)
            DismissZoom();
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        // Progress is exposed via ObservableProperties for XAML binding
    }

    private void CompleteDrag(PhysicalPoint dropPoint)
    {
        IsActive = false;
        _settleDetector.Reset();
        _dwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        _messenger.Send(new DragDropMessage(dropPoint));
    }

    private void CancelDrag()
    {
        IsActive = false;
        _settleDetector.Reset();
        _dwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        _messenger.Send(new DragCancelMessage());
    }

    private void DismissZoom()
    {
        DragState = DragSubState.FreeDrag;
        ShowBack = false;
        SettleProgress = 0;
        _settleDetector.Reset();
        SetupDwellTargets();
        _messenger.Send(new DragDismissZoomMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _settleDetector.FixationStarted -= OnSettleStarted;
        _settleDetector.FixationEnded -= OnSettleEnded;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

Note: `SystemParameters.PrimaryScreenWidth` requires `using System.Windows;` — add it to the usings. The ViewModel references WPF types for screen dimensions; this is acceptable because ViewModels already reference `System.Windows.Point` in this codebase (see `ManualSelectionOverlayViewModel` using `DpiHelper`). `DpiHelper` lives in `EyeCtrl.Core.Services` and is reachable from the ViewModels project.

> **⚠ Open design question: zoom during drag (ManualSelection mode).** This plan fully implements the core flow (initiate → trail → settle → drop) for **None** and **AutoSelection** modes. The **ManualSelection zoom-magnifier refinement is NOT fully specified** and must be resolved before implementing the ManualSelection path:
> - The VM tracks a `ZoomActive` sub-state but nothing renders a magnifier. Reusing `ManualSelectionOverlayWindow` via `StartManualSelectionMessage` is **not** drop-in: that overlay's dwell pipeline raises `ManualSelectionDwellCompletedMessage` → `InteractionManager.OnManualSelectionDwell(...)`, which **early-returns unless the state is `ManualSelectionActive`**. During a drag the state is `Dragging`, so the magnifier's own selection pipeline is inert, and there is no path translating the refined precise point into the drop location. It also triggers backdrop/cursor side effects tied to the manual-selection flow.
> - Drop confirmation inside zoom is currently only the **✋ release button** (`OnTargetActivated(ReleaseTargetId)`). A "re-settle inside the magnified view" confirmation would require re-arming `FixationDetector` (it stays fixating and won't re-fire `FixationStarted` until gaze leaves the radius).
>
> **Recommendation:** either (a) split the ManualSelection zoom into a follow-up plan and ship the None/AutoSelection trail+settle+drop first, or (b) design a dedicated zoom render + refined-point→drop path owned by `DragTrailViewModel`/`DragTrailOverlay` (do not route through `InteractionManager.OnManualSelectionDwell`). The `DragTrailViewModelTests` above deliberately cover only None-mode settle and the release/cancel buttons for this reason.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "DragTrailViewModelTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/DragTrailViewModel.cs tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs
git commit -m "feat: add DragTrailViewModel with settle detection and dwell buttons"
```

---

### Task 8: DragTrailOverlay (WPF Window)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml`
- Create: `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs`

- [ ] **Step 1: Create the XAML**

```xml
<!-- src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml -->
<local:OverlayWindow x:Class="EyeCtrl.App.Overlays.DragTrailOverlay"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="clr-namespace:EyeCtrl.App.Overlays"
    WindowStyle="None" AllowsTransparency="True" Background="Transparent"
    Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
    WindowState="Maximized">
    <Canvas x:Name="TrailCanvas" />
</local:OverlayWindow>
```

- [ ] **Step 2: Create the code-behind**

```csharp
// src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class DragTrailOverlay : OverlayWindow
{
    private readonly Line _trailLine;
    private readonly Ellipse _settleRing;
    private readonly TextBlock _grabIcon;
    private readonly TextBlock _releaseIcon;
    private readonly TextBlock _cancelIcon;
    private readonly TextBlock _backIcon;
    private readonly Ellipse _cancelBg;
    private readonly Ellipse _backBg;
    private readonly Ellipse _releaseBg;

    public DragTrailOverlay()
    {
        IsClickThrough = true;
        InitializeComponent();

        _trailLine = new Line
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800")),
            StrokeThickness = 2,
            Opacity = 0.7
        };
        TrailCanvas.Children.Add(_trailLine);

        _settleRing = new Ellipse
        {
            Width = 40, Height = 40,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_settleRing);

        _grabIcon = CreateIcon("\u270A", "#ff9800");
        _releaseIcon = CreateIcon("\u270B", "#4caf50");
        _cancelIcon = CreateIcon("\u2715", "#f44336");
        _backIcon = CreateIcon("\u21A9", "#4fc3f7");

        _releaseBg = CreateButtonBg("#1a3a1a", "#4caf50");
        _cancelBg = CreateButtonBg("#3a1a1a", "#f44336");
        _backBg = CreateButtonBg("#1a2a3a", "#4fc3f7");

        TrailCanvas.Children.Add(_releaseBg);
        TrailCanvas.Children.Add(_releaseIcon);
        TrailCanvas.Children.Add(_cancelBg);
        TrailCanvas.Children.Add(_cancelIcon);
        TrailCanvas.Children.Add(_backBg);
        TrailCanvas.Children.Add(_backIcon);
        TrailCanvas.Children.Add(_grabIcon);
    }

    public void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
        bool showBack, PhysicalPoint releasePos, PhysicalPoint cancelPos, PhysicalPoint backPos)
    {
        var originLogical = new Point(DpiHelper.PhysicalToLogicalX(grabOrigin.X), DpiHelper.PhysicalToLogicalY(grabOrigin.Y));
        var cursorLogical = new Point(DpiHelper.PhysicalToLogicalX(cursorPos.X), DpiHelper.PhysicalToLogicalY(cursorPos.Y));

        _trailLine.X1 = originLogical.X;
        _trailLine.Y1 = originLogical.Y;
        _trailLine.X2 = cursorLogical.X;
        _trailLine.Y2 = cursorLogical.Y;

        Canvas.SetLeft(_grabIcon, cursorLogical.X - 10);
        Canvas.SetTop(_grabIcon, cursorLogical.Y - 10);

        var releaseLogical = new Point(DpiHelper.PhysicalToLogicalX(releasePos.X), DpiHelper.PhysicalToLogicalY(releasePos.Y));
        PositionButton(_releaseBg, _releaseIcon, releaseLogical);

        var cancelLogical = new Point(DpiHelper.PhysicalToLogicalX(cancelPos.X), DpiHelper.PhysicalToLogicalY(cancelPos.Y));
        PositionButton(_cancelBg, _cancelIcon, cancelLogical);

        _backBg.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        _backIcon.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        if (showBack)
        {
            var backLogical = new Point(DpiHelper.PhysicalToLogicalX(backPos.X), DpiHelper.PhysicalToLogicalY(backPos.Y));
            PositionButton(_backBg, _backIcon, backLogical);
        }

        if (settleProgress > 0)
        {
            _settleRing.Visibility = Visibility.Visible;
            Canvas.SetLeft(_settleRing, cursorLogical.X - 20);
            Canvas.SetTop(_settleRing, cursorLogical.Y - 20);

            var circumference = Math.PI * 40;
            var dashArray = new DoubleCollection { settleProgress * circumference / 3, circumference };
            _settleRing.StrokeDashArray = dashArray;
        }
        else
        {
            _settleRing.Visibility = Visibility.Collapsed;
        }
    }

    public void Clear()
    {
        _trailLine.X1 = 0; _trailLine.Y1 = 0;
        _trailLine.X2 = 0; _trailLine.Y2 = 0;
        _settleRing.Visibility = Visibility.Collapsed;
        _backBg.Visibility = Visibility.Collapsed;
        _backIcon.Visibility = Visibility.Collapsed;
    }

    private static TextBlock CreateIcon(string glyph, string color)
    {
        return new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            IsHitTestVisible = false
        };
    }

    private static Ellipse CreateButtonBg(string fill, string stroke)
    {
        return new Ellipse
        {
            Width = 26, Height = 26,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
    }

    private static void PositionButton(Ellipse bg, TextBlock icon, Point logicalPos)
    {
        Canvas.SetLeft(bg, logicalPos.X - 13);
        Canvas.SetTop(bg, logicalPos.Y - 13);
        Canvas.SetLeft(icon, logicalPos.X - 8);
        Canvas.SetTop(icon, logicalPos.Y - 8);
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs
git commit -m "feat: add DragTrailOverlay WPF window with elastic trail and floating buttons"
```

---

### Task 9: Wire EventCoordinator

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

> **Important:** `EventCoordinator` has **no** `_messenger` or `_overlayManager` fields — it reaches everything through `_context` (`_context.Messenger`, `_context.OverlayManager`, `_context.InputSimulator`, `_context.InteractionManager`, `_context.DragTrailVM`). It registers messages in `SetupMessageHandlers()` using the `Register<EventCoordinator, TMessage>(this, (_, m) => _context.Dispatcher.BeginInvoke(...))` pattern and subscribes VM `PropertyChanged` in `SetupPropertyChangedHandlers()`. The handlers below follow those conventions and are all unregistered in `Dispose()`.

- [ ] **Step 1: Add fields and the DragStarted handler**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, add fields near the other private fields:

```csharp
private DragTrailOverlay? _dragTrailOverlay;
private PhysicalPoint _dragGrabOrigin;   // remembered so cancel can MouseUp at the origin
```

Add a `SetupDragHandlers()` method and call it from `Initialize()` (alongside `SetupMessageHandlers()` etc.):

```csharp
private void SetupDragHandlers()
{
    if (_context == null) return;

    _context.InteractionManager.DragStarted += OnDragStarted;
    _context.DragTrailVM.PropertyChanged += OnDragTrailVMPropertyChanged;

    _context.Messenger.Register<EventCoordinator, DragDropMessage>(this, (_, m) =>
        _context.Dispatcher.BeginInvoke(() => OnDragDrop(m)));
    _context.Messenger.Register<EventCoordinator, DragCancelMessage>(this, (_, _) =>
        _context.Dispatcher.BeginInvoke(OnDragCancel));
    _context.Messenger.Register<EventCoordinator, DragDismissZoomMessage>(this, (_, _) =>
        _context.Dispatcher.BeginInvoke(OnDragDismissZoom));
}

private void OnDragStarted(PhysicalPoint grabOrigin)
{
    if (_context == null) return;

    _dragGrabOrigin = grabOrigin;
    _context.InputSimulator.MouseDown(grabOrigin);

    _dragTrailOverlay = new DragTrailOverlay();
    _dragTrailOverlay.Show();

    _context.Messenger.Send(new StartDragTrailMessage(grabOrigin));
}
```

`DragStarted` is raised from `InteractionManager.OnActionSelected`, which runs on the UI thread (the dwell→action path), so creating/showing the window here is safe.

- [ ] **Step 2: Add DragDrop / DragCancel / DismissZoom handlers**

```csharp
private void OnDragDrop(DragDropMessage msg)
{
    _context?.InputSimulator.MouseUp(msg.DropPoint);
    _context?.InteractionManager.CompleteDrag();
    CloseDragOverlay();
}

private void OnDragCancel()
{
    _context?.InputSimulator.MouseUp(_dragGrabOrigin);   // item returns to grab origin
    _context?.InteractionManager.CompleteDrag();
    CloseDragOverlay();
}

private void OnDragDismissZoom()
{
    // Only relevant once zoom-during-drag is implemented (see the open design note in Task 7).
    if (_context?.OverlayManager.ManualSelectionOverlay.IsVisible == true)
        _context.OverlayManager.ManualSelectionOverlay.Hide();
}

private void CloseDragOverlay()
{
    _dragTrailOverlay?.Close();
    _dragTrailOverlay = null;
}
```

The grab origin is read from the `_dragGrabOrigin` field (set in `OnDragStarted`) — `DragTrailOverlay` deliberately exposes no `GrabOrigin` property.

- [ ] **Step 3: Render the overlay from DragTrailVM.PropertyChanged**

Drive overlay rendering from the VM's property changes, mirroring `OnActionMenuVMPropertyChanged`. Render on the position/progress/visibility-relevant property changes:

```csharp
private void OnDragTrailVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (_context == null || _dragTrailOverlay == null) return;
    var vm = _context.DragTrailVM;

    switch (e.PropertyName)
    {
        case nameof(DragTrailViewModel.CursorPosition):
        case nameof(DragTrailViewModel.SettleProgress):
        case nameof(DragTrailViewModel.ShowBack):
        case nameof(DragTrailViewModel.ReleaseButtonCenter):
            _dragTrailOverlay.Update(
                vm.GrabOrigin, vm.CursorPosition, vm.SettleProgress,
                vm.ShowBack, vm.ReleaseButtonCenter, vm.CancelButtonCenter, vm.BackButtonCenter);
            break;
    }
}
```

No separate gaze forwarding is needed: `DragTrailViewModel` is itself an `IRecipient<GazeCursorMovedMessage>` (the same UI-thread gaze stream every other overlay VM consumes), so it updates its own state and these `PropertyChanged` events drive the render.

- [ ] **Step 3b: Unsubscribe in Dispose**

In `Dispose()`, add (the `Messenger.UnregisterAll(this)` call already there covers the message registrations, but the event/`PropertyChanged` subscriptions must be removed explicitly):

```csharp
if (_context != null)
{
    _context.InteractionManager.DragStarted -= OnDragStarted;
    _context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
}
CloseDragOverlay();
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: wire DragStarted/DragDrop/DragCancel in EventCoordinator"
```

---

### Task 10: Wire DragTrailViewModel into DI + EventCoordinationContext

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs` (the `EventCoordinationContext` record)
- Modify: `src/EyeCtrl.App/App.xaml.cs`

> **Why not `OverlayViewModels`/`OverlayManager`?** `OverlayManager` builds and owns the *windows* it's told about, but the drag overlay is created and owned by `EventCoordinator` (Task 9). `EventCoordinator` reads its VMs from `EventCoordinationContext`, not from `OverlayManager`. So the VM must be added to `EventCoordinationContext` — adding it to `OverlayViewModels` would be dead wiring. (If you later prefer `OverlayManager` to own the drag overlay window for consistency with the other overlays, do that as a deliberate refactor — it is not required here.)

- [ ] **Step 1: Register DragTrailViewModel in DI**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, add to the service registration section:

```csharp
services.AddSingleton<DragTrailViewModel>();
```

A singleton is correct: the VM registers for `GazeCursorMovedMessage` for the app lifetime and early-returns when `!IsActive`.

- [ ] **Step 2: Add DragTrailVM to EventCoordinationContext**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, add a parameter to the `EventCoordinationContext` record (e.g. in the ViewModels group):

```csharp
DragTrailViewModel DragTrailVM,
```

- [ ] **Step 3: Resolve and pass it in App.xaml.cs**

In `src/EyeCtrl.App/App.xaml.cs`, where the `EventCoordinationContext` is constructed, resolve the VM from DI and pass it in the matching position:

```csharp
var dragTrailVm = serviceProvider.GetRequiredService<DragTrailViewModel>();
// ... add `dragTrailVm` to the new EventCoordinationContext(...) argument list
```

Resolving it here is also what instantiates the singleton (so its `GazeCursorMovedMessage` registration goes live).

- [ ] **Step 4: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire DragTrailViewModel into DI and EventCoordinationContext"
```

---

### Task 11: Integration + Escape Key Cancel

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Add Escape key cancel during drag**

In `EventCoordinator`, add a keyboard hook or check for Escape key press. The existing codebase may already have a global key handler — search for `KeyDown` or `PreviewKeyDown` in `App.xaml.cs` or `EventCoordinator.cs`.

If a key handler exists, add a check (note: `_context.Messenger`, not a bare `_messenger` field):

```csharp
if (key == Key.Escape && _context?.InteractionManager.CurrentState == InteractionState.Dragging)
{
    _context.Messenger.Send(new DragCancelMessage());
}
```

If no key handler exists, add one in `App.xaml.cs` using `ComponentDispatcher.ThreadFilterMessage` to intercept `WM_KEYDOWN` for VK_ESCAPE (0x1B).

> **Scope note:** the design's Error Handling table also lists *gaze-tracking-lost > 3 s → auto-cancel* and *display-resolution-change → cancel*. This plan implements **only** the Escape-key cancel. If those rows are in scope, add them as explicit tasks (a tracking-lost timer in `DragTrailViewModel`, and a `SystemEvents.DisplaySettingsChanged` handler that sends `DragCancelMessage`); otherwise record them as deferred.

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: add Escape key cancel during drag"
```

---

### Task 12: Final Verification

- [ ] **Step 1: Run full build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run all non-UI tests**

Run: `dotnet test --filter "Category!=UI"`
Expected: All tests pass.

- [ ] **Step 3: Run detect_changes to verify scope**

Use GitNexus `detect_changes()` to verify the changes only affect expected symbols and execution flows.

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

Verify:
1. Fixate → action menu → ✊ Drag → elastic trail appears
2. Move gaze → trail stretches, ✋ release button follows cursor
3. ✕ cancel on right edge → item returns to origin
4. Stop gaze → settle ring fills → zoom appears (in ManualSelection mode)
5. ↩ back on right edge → zoom dismisses, back to free drag
6. Settle completes → drop confirmed, green flash
7. Escape key → cancel drag
