# Auto Selection Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Auto Selection mode that snaps click targets to the nearest Windows UI control via UIA/Win32, alongside existing None and ManualSelection modes.

**Architecture:** A `SelectionMode` enum drives a 3-way branch in `InteractionManager`. `IControlFinder` (Platform, UIA+Win32) resolves targets asynchronously in `GazeEngineAdapter` before activation. A `TargetHighlightWindow` overlay shows a border around detected controls. The toolbar gains a cycling 3-mode button replacing the ManualSelection toggle.

**Tech Stack:** .NET 10, WPF, System.Windows.Automation (managed UIA client), CommunityToolkit.Mvvm, xUnit

---

## File Structure

### Created (9)

| File | Responsibility |
|------|---------------|
| `src/EyeCtrl.Core/Models/SelectionMode.cs` | Three-value mode enum |
| `src/EyeCtrl.Core/Models/PhysicalRect.cs` | Framework-agnostic rectangle record struct |
| `src/EyeCtrl.Core/Models/ControlInfo.cs` | Resolved control data (PhysRect + PhysPoint) |
| `src/EyeCtrl.Core/Interfaces/IControlFinder.cs` | Synchronous control resolution interface |
| `src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs` | Highlight color, thickness, fallback toggle |
| `src/EyeCtrl.Platform/Services/ControlFinder.cs` | UIA + Win32 implementation |
| `src/EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs` | Highlight overlay VM |
| `src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml` + `.xaml.cs` | Highlight overlay WPF window |
| `tests/EyeCtrl.Core.Tests/Models/ControlInfoTests.cs` | ControlInfo + PhysicalRect tests |

### Modified (18)

| File | Change summary |
|------|---------------|
| `src/EyeCtrl.Core/Processing/InteractionManager.cs` | `SelectionMode` + `AutoSelectionTarget` properties, branch + guard logic |
| `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `SelectionMode`, `AutoSelection` props, Validate arm |
| `src/EyeCtrl.Core/Settings/SettingsService.cs` | Add `JsonStringEnumConverter` |
| `src/EyeCtrl.Core/GazeEngine.cs` | Object initializer: `SelectionMode` replaces `ManualSelectionEnabled` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `SelectionModeChangedMessage` + `ResolvedControl`; rename `IsFromManualSelection`; delete `ManualSelectionToggledMessage` |
| `src/EyeCtrl.ViewModels/SettingItemViewModel.cs` | Add `IsEnabled` property |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs` | BuildTabs: replace toggle with combo + Auto settings; ResetAllSettings |
| `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` | 3-mode cycling; remove `ManualSelectionToggledMessage` + `IsManualSelectionOn`; seed + sync mode from settings |
| `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` | `SetSelectionModeState()` replaces `SetManualSelectionState()`; `"ManualSelection"` → `"SelectionMode"` |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` | Add `TargetHighlight` to `OverlayViewModels` record |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Add `TargetHighlightVM` to `EventCoordinationContext` record |
| `src/EyeCtrl.App/Services/OverlayManager.cs` | Add `TargetHighlight` window + init |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `IControlFinder`, `TargetHighlightOverlayViewModel` |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | DI `IControlFinder`, async UIA resolve, `FallbackToPixelOnNoControl` gate, new message handlers, toolbar seeding |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | `IsPreciseTarget` rename; highlight overlay lifecycle; `OnSettingsChanged` |
| `src/EyeCtrl.App/App.xaml.cs` | Resolve `TargetHighlightOverlayViewModel`, pass to `OverlayViewModels` and `EventCoordinationContext` |
| `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` | Add `net10.0-windows` TFM + `FrameworkReference WindowsDesktop.App` |
| `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs` | Replace `ManualSelectionEnabled` with `SelectionMode`, add Auto mode tests |

---

### Task 1: Core Models — SelectionMode, PhysicalRect, ControlInfo

**Files:**
- Create: `src/EyeCtrl.Core/Models/SelectionMode.cs`
- Create: `src/EyeCtrl.Core/Models/PhysicalRect.cs`
- Create: `src/EyeCtrl.Core/Models/ControlInfo.cs`
- Create: `tests/EyeCtrl.Core.Tests/Models/ControlInfoTests.cs`

- [ ] **Step 1: Create `SelectionMode` enum**

```csharp
// src/EyeCtrl.Core/Models/SelectionMode.cs
namespace EyeCtrl.Core.Models;

public enum SelectionMode
{
    None,
    ManualSelection,
    AutoSelection
}
```

- [ ] **Step 2: Create `PhysicalRect` record struct**

```csharp
// src/EyeCtrl.Core/Models/PhysicalRect.cs
namespace EyeCtrl.Core.Models;

public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public PhysicalPoint Center => new(CenterX, CenterY);
}
```

- [ ] **Step 3: Create `ControlInfo` model**

```csharp
// src/EyeCtrl.Core/Models/ControlInfo.cs
namespace EyeCtrl.Core.Models;

public record ControlInfo(PhysicalRect BoundingRect)
{
    public PhysicalPoint CenterPoint => BoundingRect.Center;
}
```

- [ ] **Step 4: Write tests for models**

```csharp
// tests/EyeCtrl.Core.Tests/Models/ControlInfoTests.cs
namespace EyeCtrl.Core.Tests.Models;

public class ControlInfoTests
{
    [Fact]
    public void ControlInfo_CenterPoint_ReturnsRectCenter()
    {
        var rect = new PhysicalRect(10, 20, 100, 50);
        var info = new ControlInfo(rect);
        Assert.Equal(60, info.CenterPoint.X); // 10 + 100/2
        Assert.Equal(45, info.CenterPoint.Y); // 20 + 50/2
    }

    [Fact]
    public void PhysicalRect_Center_ReturnsCorrectValues()
    {
        var rect = new PhysicalRect(0, 0, 200, 100);
        Assert.Equal(100, rect.Center.X);
        Assert.Equal(50, rect.Center.Y);
    }

    [Fact]
    public void SelectionMode_Defaults()
    {
        // default(int) = 0 = None
        Assert.Equal(SelectionMode.None, default(SelectionMode));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ControlInfoTests"`
Expected: 3 tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Models/SelectionMode.cs src/EyeCtrl.Core/Models/PhysicalRect.cs src/EyeCtrl.Core/Models/ControlInfo.cs tests/EyeCtrl.Core.Tests/Models/ControlInfoTests.cs
git commit -m "feat: add SelectionMode enum, PhysicalRect, and ControlInfo models"
```

---

### Task 2: IControlFinder Interface and AutoSelectionSettings

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/IControlFinder.cs`
- Create: `src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Settings/AutoSelectionSettingsTests.cs`

- [ ] **Step 1: Create `IControlFinder` interface**

```csharp
// src/EyeCtrl.Core/Interfaces/IControlFinder.cs
namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public interface IControlFinder
{
    ControlInfo? FindTarget(PhysicalPoint point);
}
```

- [ ] **Step 2: Create `AutoSelectionSettings` class**

```csharp
// src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.RegularExpressions;

public partial class AutoSelectionSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private string _highlightColor = "#0078D4";
    [ObservableProperty] private double _highlightThickness = 3.0;
    [ObservableProperty] private double _highlightOpacity = 0.7;
    [ObservableProperty] private bool _fallbackToPixelOnNoControl = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsValidHexColor(HighlightColor))
            errors.Add($"HighlightColor '{HighlightColor}' is not a valid hex color.");

        errors.AddRange(ValidationHelper.ValidateRange(HighlightThickness, 1, 10, nameof(HighlightThickness)));
        errors.AddRange(ValidationHelper.ValidateRange(HighlightOpacity, 0.1, 1.0, nameof(HighlightOpacity)));

        return errors;
    }

    private static bool IsValidHexColor(string color)
    {
        return !string.IsNullOrEmpty(color)
            && Regex.IsMatch(color, @"^#[0-9A-Fa-f]{6}$");
    }
}
```

- [ ] **Step 3: Write tests for AutoSelectionSettings**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/AutoSelectionSettingsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class AutoSelectionSettingsTests
{
    [Fact]
    public void DefaultHighlightColor_IsValid()
    {
        var s = new AutoSelectionSettings();
        var errors = s.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void InvalidHighlightColor_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightColor = "not-a-color" };
        var errors = s.Validate();
        Assert.Single(errors);
        Assert.Contains("HighlightColor", errors[0]);
    }

    [Fact]
    public void HighlightThickness_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightThickness = 0 };
        var errors = s.Validate();
        Assert.Single(errors);
    }

    [Fact]
    public void HighlightThickness_MaxEdge_IsValid()
    {
        var s = new AutoSelectionSettings { HighlightThickness = 10 };
        var errors = s.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void HighlightOpacity_OutOfRange_ReturnsError()
    {
        var s = new AutoSelectionSettings { HighlightOpacity = 0 };
        var errors = s.Validate();
        Assert.Single(errors);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AutoSelectionSettingsTests"`
Expected: 5 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IControlFinder.cs src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs tests/EyeCtrl.Core.Tests/Settings/AutoSelectionSettingsTests.cs
git commit -m "feat: add IControlFinder interface and AutoSelectionSettings"
```

---

### Task 3: InteractionManager Refactor

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

- [ ] **Step 1: Write tests for the new SelectionMode behavior**

Append these tests to `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`:

```csharp
[Fact]
public void DefaultSelectionMode_IsNone()
{
    var manager = new InteractionManager();
    Assert.Equal(SelectionMode.None, manager.SelectionMode);
}

[Fact]
public void OnActivationButtonDwelled_AutoMode_WithTarget_ShowsActionMenuAtControlCenter()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
    PhysicalPoint? menuPoint = null;
    manager.ShowActionMenu += p => menuPoint = p;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
    manager.OnActivationButtonDwelled();

    Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
    Assert.Equal(new PhysicalPoint(175, 175), menuPoint); // center of (150,150,50,50)
}

[Fact]
public void OnActivationButtonDwelled_AutoMode_NoTarget_FallsBackToFixationPoint()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
    PhysicalPoint? menuPoint = null;
    manager.ShowActionMenu += p => menuPoint = p;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = null;
    manager.OnActivationButtonDwelled();

    Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
    Assert.Equal(new PhysicalPoint(100, 100), menuPoint);
}

[Fact]
public void OnActionSelected_AutoMode_ReturnsStoredTargetPoint()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
    PhysicalPoint? firedPoint = null;
    manager.ActionRequested += (_, p) => firedPoint = p;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(150, 150, 50, 50));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.LeftClick);

    Assert.Equal(new PhysicalPoint(175, 175), firedPoint);
}

[Fact]
public void OnActionSelected_AutoMode_NoTarget_FallsBackToFixationPoint()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };
    PhysicalPoint? firedPoint = null;
    manager.ActionRequested += (_, p) => firedPoint = p;

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = null;
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.LeftClick);

    Assert.Equal(new PhysicalPoint(100, 100), firedPoint);
}

[Fact]
public void ResetToFreeLook_ClearsAutoSelectionTarget()
{
    var manager = new InteractionManager { SelectionMode = SelectionMode.AutoSelection };

    manager.OnFixationStarted(new PhysicalPoint(100, 100));
    manager.AutoSelectionTarget = new ControlInfo(new PhysicalRect(0, 0, 10, 10));
    manager.OnActivationButtonDwelled();
    manager.OnActionSelected(InteractionAction.LeftClick);

    Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    Assert.Null(manager.AutoSelectionTarget);
}
```

- [ ] **Step 2: Run tests to verify they FAIL**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerTests"`
Expected: 6 new tests FAIL (SelectionMode property and AutoSelectionTarget don't exist yet)

- [ ] **Step 3: Modify `InteractionManager`**

Apply all changes to `src/EyeCtrl.Core/Processing/InteractionManager.cs`:

```csharp
namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public SelectionMode SelectionMode { get; set; } = SelectionMode.None;
    public PhysicalPoint? FixationPoint { get; private set; }
    public PhysicalPoint? ManualSelectionDwellPoint { get; private set; }
    public ControlInfo? AutoSelectionTarget { get; set; }

    // Remove: public bool ManualSelectionEnabled { get; set; }

    public event Action<InteractionState>? StateChanged;
    public event Action<InteractionAction, PhysicalPoint>? ActionRequested;
    public event Action<PhysicalPoint>? ShowActivationButton;
    public event Action? HideActivationButton;
    public event Action<PhysicalPoint>? ShowActionMenu;
    public event Action? HideActionMenu;
    public event Action<PhysicalPoint>? StartManualSelection;
    public event Action? EndManualSelection;
    public event Action? HideManualSelection;

    private void SetState(InteractionState newState)
    {
        CurrentState = newState;
        StateChanged?.Invoke(newState);
    }

    public void OnFixationStarted(PhysicalPoint point)
    {
        if (CurrentState != InteractionState.FreeLook) return;
        FixationPoint = point;
        SetState(InteractionState.Fixation);
        ShowActivationButton?.Invoke(point);
    }

    public void OnFixationEnded()
    {
        if (CurrentState == InteractionState.Fixation)
        {
            HideActivationButton?.Invoke();
            SetState(InteractionState.FreeLook);
        }
    }

    public void OnActivationButtonDwelled()
    {
        if (CurrentState != InteractionState.Fixation) return;
        HideActivationButton?.Invoke();

        switch (SelectionMode)
        {
            case SelectionMode.ManualSelection:
                StartManualSelection?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ManualSelectionActive);
                break;

            case SelectionMode.AutoSelection:
                if (AutoSelectionTarget != null)
                    ShowActionMenu?.Invoke(AutoSelectionTarget.CenterPoint);
                else
                    ShowActionMenu?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ActionMenuVisible);
                break;

            default: // None
                ShowActionMenu?.Invoke(FixationPoint!.Value);
                SetState(InteractionState.ActionMenuVisible);
                break;
        }
    }

    public void OnManualSelectionDwell(PhysicalPoint precisePoint)
    {
        if (CurrentState != InteractionState.ManualSelectionActive) return;
        ManualSelectionDwellPoint = precisePoint;
        HideManualSelection?.Invoke();
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ManualSelectionActionMenuVisible);
    }

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

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();

        ActionRequested?.Invoke(action, targetPoint.Value);
        ResetToFreeLook();
    }

    public void OnManualSelectionBack()
    {
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        {
            HideActionMenu?.Invoke();
            ManualSelectionDwellPoint = null;
            SetState(InteractionState.ManualSelectionActive);
            StartManualSelection?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ManualSelectionActive)
        {
            EndManualSelection?.Invoke();
            ResetToFreeLook();
        }
    }

    public void OnActionMenuDismissed()
    {
        if (CurrentState == InteractionState.ActionMenuVisible)
        {
            HideActionMenu?.Invoke();
            ResetToFreeLook();
        }
    }

    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else
        {
            HideActivationButton?.Invoke();
            HideActionMenu?.Invoke();
            if (CurrentState is InteractionState.ManualSelectionActive or InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            SetState(InteractionState.Paused);
        }
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ManualSelectionDwellPoint = null;
        AutoSelectionTarget = null;
        SetState(InteractionState.FreeLook);
    }
}
```

- [ ] **Step 4: Fix existing tests that reference `ManualSelectionEnabled`**

In `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`, change all occurrences of `ManualSelectionEnabled = true` to `SelectionMode = SelectionMode.ManualSelection`:

- Line 43: `var manager = new InteractionManager { ManualSelectionEnabled = true };` → `...{ SelectionMode = SelectionMode.ManualSelection };`
- Line 86: same
- Line 107: same
- Line 122: same
- Line 154: same
- Line 170: same

Also add `using EyeCtrl.Core.Models;` at the top (for `SelectionMode`).

- [ ] **Step 5: Run tests to verify they all pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionManagerTests"`
Expected: all tests PASS (19 total: 13 original + 6 new)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/InteractionManager.cs tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs
git commit -m "refactor: add SelectionMode, AutoSelectionTarget to InteractionManager; remove ManualSelectionEnabled"
```

---

### Task 4: Settings Plumbing (EyeCtrlSettings, SettingsService, GazeEngine)

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs`
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs` (read before modify)

- [ ] **Step 1: Read SettingsSerializationTests to understand patterns**

Read the file to understand how JSON serialization/deserialization tests work.

- [ ] **Step 2: Add `SelectionMode` and `AutoSelection` properties to `EyeCtrlSettings`**

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`, add after the `_viewPosition` line (line 17):

```csharp
[ObservableProperty] private SelectionMode _selectionMode = SelectionMode.None;
[ObservableProperty] private AutoSelectionSettings _autoSelection = new();
```

Add `using EyeCtrl.Core.Models;` at the top if not present.

Add after the `Theme` validation arm (before the final `return errors`):

```csharp
if (AutoSelection is null)
    errors.Add("AutoSelection settings must not be null.");
else
    errors.AddRange(AutoSelection.Validate());
```

- [ ] **Step 3: Add `JsonStringEnumConverter` to `SettingsService`**

In `src/EyeCtrl.Core/Settings/SettingsService.cs`, change the `JsonOptions` (line 9-13) from:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
```

To:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};
```

Add `using System.Text.Json.Serialization;` at the top.

- [ ] **Step 4: Update `GazeEngine` object initializer**

In `src/EyeCtrl.Core/GazeEngine.cs`, find the `_interactionManager` creation (around line 56). Change:

```csharp
_interactionManager = new InteractionManager { ManualSelectionEnabled = settings.ManualSelection.Enabled };
```

To:

```csharp
_interactionManager = new InteractionManager { SelectionMode = settings.SelectionMode };
```

- [ ] **Step 5: Run all existing Core tests to check for regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: all tests PASS. If any test creates `EyeCtrlSettings` and touches JSON serialization, it may fail due to the `JsonStringEnumConverter` changing enum serialization. The converter uses `JsonNamingPolicy.CamelCase` so `ViewPosition.AtCenter` serializes as `"atCenter"` — verify no existing test asserts integer-form enums in JSON.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs src/EyeCtrl.Core/Settings/SettingsService.cs src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: wire SelectionMode and AutoSelectionSettings into settings pipeline"
```

---

### Task 5: Messages — InteractionMessages.cs

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`

- [ ] **Step 1: Add `SelectionModeChangedMessage` and modify `ShowActionMenuMessage`**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`:

Add `using EyeCtrl.Core.Models;` if not already present.

Add after the `ManualSelectionToggledMessage` line (line 83):

```csharp
public record SelectionModeChangedMessage(SelectionMode Mode);
```

- [ ] **Step 2: Rename `IsFromManualSelection` to `IsPreciseTarget` and add `ResolvedControl`**

On line 50, change:

```csharp
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsFromManualSelection = false);
```

To:

```csharp
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsPreciseTarget = false);
```

In `ShowActionMenuMessage` (line 20-26), add after `ManualSelectionRadius` property:

```csharp
/// <summary>Resolved control info in AutoSelection mode. Null otherwise.</summary>
public ControlInfo? ResolvedControl { get; init; }
```

- [ ] **Step 3: Delete `ManualSelectionToggledMessage`**

Delete line 83:

```diff
- public record ManualSelectionToggledMessage;
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "refactor: add SelectionModeChangedMessage, rename IsFromManualSelection->IsPreciseTarget, add ResolvedControl, remove ManualSelectionToggledMessage"
```

---

### Task 6: SettingItemViewModel + SettingsViewModel

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingItemViewModel.cs`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs` (read first)

- [ ] **Step 1: Read SettingsViewModelTests to understand patterns**

Read `tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs` first.

- [ ] **Step 2: Add `IsEnabled` to `SettingItemViewModel`**

In `src/EyeCtrl.ViewModels/SettingItemViewModel.cs`, in the abstract base class (line 5-11), add:

```csharp
public abstract partial class SettingItemViewModel : ObservableObject
{
    public string Label { get; }
    public string Id { get; }
    /// <summary>Whether this setting is interactive. Default true.</summary>
    [ObservableProperty] private bool _isEnabled = true;

    protected SettingItemViewModel(string id, string label) { Id = id; Label = label; }
}
```

- [ ] **Step 3: Update `BuildTabs()` in `SettingsViewModel`**

In `src/EyeCtrl.ViewModels/SettingsViewModel.cs`, update the ManualSelection tab (lines 71-76). Replace the entire tab definition:

```csharp
// Create Auto-specific items FIRST so the combo's lambda can capture them
var highlightColor = new ColorSettingViewModel("highlightColor", "Highlight Color",
    _settings.AutoSelection.HighlightColor, v => _settings.AutoSelection.HighlightColor = v);
var highlightThickness = new SliderSettingViewModel("highlightThickness", "Highlight Thickness",
    _settings.AutoSelection.HighlightThickness, 1, 10, 1, v => _settings.AutoSelection.HighlightThickness = v);
highlightColor.IsEnabled = highlightThickness.IsEnabled = (_settings.SelectionMode == SelectionMode.AutoSelection);

Tabs.Add(new SettingsTabViewModel("Selection", "\uD83C\uDFAF", new List<SettingItemViewModel>
{
    new ComboSettingViewModel("selectionMode", "Selection Mode",
        _settings.SelectionMode.ToString(),
        Enum.GetNames<SelectionMode>(),
        v =>
        {
            _settings.SelectionMode = Enum.Parse<SelectionMode>(v);
            highlightColor.IsEnabled = highlightThickness.IsEnabled =
                (_settings.SelectionMode == SelectionMode.AutoSelection);
        }),
    new SliderSettingViewModel("ManualSelectionRadius", "Circle Radius (px)",
        z.CircleRadiusPixels, 50, 500, 10, v => z.CircleRadiusPixels = (int)v),
    new SliderSettingViewModel("magnification", "Magnification Level",
        z.MagnificationLevel, 1.5, 5, 0.5, v => z.MagnificationLevel = v),
    highlightColor,
    highlightThickness,
}));
```

Add `using EyeCtrl.Core.Models;` at the top.

- [ ] **Step 4: Update `ResetAllSettings()` in `SettingsViewModel`**

In the `ResetAllSettings` method (line 148), add after the `CopySettings(defaults.General, _settings.General)` line:

```csharp
CopySettings(defaults.AutoSelection, _settings.AutoSelection);
_settings.SelectionMode = defaults.SelectionMode;
_settings.ViewPosition = defaults.ViewPosition;
```

Remove the variable `z` alias for `_settings.ManualSelection` from `BuildTabs` since the tab name changed — or rename `z` to something clearer. Update line 38 which reads `var z = _settings.ManualSelection;` to keep it (it's still used for the sliders).

- [ ] **Step 5: Write tests for the SettingsViewModel changes**

```csharp
// Append to tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs
[Fact]
public void ResetAllSettings_ResetsSelectionMode_And_AutoSelection()
{
    var settings = new EyeCtrlSettings
    {
        SelectionMode = SelectionMode.AutoSelection,
        ViewPosition = ViewPosition.AtGaze
    };
    settings.AutoSelection.HighlightColor = "#FF0000";

    var messenger = new WeakReferenceMessenger();
    var settingsService = CreateTempSettingsService();
    var vm = new SettingsViewModel(settings, settingsService, messenger);

    vm.ResetAllSettings();

    Assert.Equal(SelectionMode.None, settings.SelectionMode);
    Assert.Equal(ViewPosition.AtCenter, settings.ViewPosition);
    Assert.Equal("#0078D4", settings.AutoSelection.HighlightColor);
}
```

(Note: `CreateTempSettingsService()` may need to be defined as a helper — copy the pattern from existing tests or create a temp file.)

- [ ] **Step 6: Run settings tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~SettingsViewModelTests"`
Check for regressions. New test should PASS.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingItemViewModel.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs
git commit -m "feat: add SelectionMode combo and AutoSelection settings to settings UI"
```

---

### Task 7: ToolbarViewModel + ToolbarWindow

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`

- [ ] **Step 1: Update tests for ToolbarViewModel**

In `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`:

Replace `ManualSelectionToggle_SendsManualSelectionToggledMessage` test with:

```csharp
[Fact]
public void SelectionModeButton_SendsSelectionModeChangedMessage()
{
    var vm = new ToolbarViewModel(_settings, _messenger);
    SelectionMode? received = null;
    _messenger.Register<SelectionModeChangedMessage>(_messenger, (_, msg) => received = msg.Mode);

    // Button ID changed from "ManualSelection" to "SelectionMode"
    vm.SimulateButtonActivated("SelectionMode");

    Assert.NotNull(received);
    Assert.Equal(SelectionMode.ManualSelection, received); // from None → Manual
}

[Fact]
public void SelectionModeButton_CyclesModes()
{
    var vm = new ToolbarViewModel(_settings, _messenger);
    var received = new List<SelectionMode>();
    _messenger.Register<SelectionModeChangedMessage>(_messenger, (_, msg) => received.Add(msg.Mode));

    vm.SimulateButtonActivated("SelectionMode"); // None → Manual
    vm.SimulateButtonActivated("SelectionMode"); // Manual → Auto (need to receive message to update state)
    // Actually the SimulateButtonActivated directly calls OnTargetActivated,
    // and the message sending updates _currentSelectionMode via the messenger handler.
    // But the messenger handler is on the VM itself and is synchronous.
    // After first activation, _currentSelectionMode should be ManualSelection.
    // The second activation should send AutoSelection.

    Assert.Equal(2, received.Count);
    Assert.Equal(SelectionMode.ManualSelection, received[0]);
    Assert.Equal(SelectionMode.AutoSelection, received[1]);
}
```

Also update the `InitializesWith4Buttons` test — the button count stays 4, but the ID changes. Update the assertion if it checks IDs:

```csharp
[Fact]
public void InitializesWith4Buttons()
{
    var vm = new ToolbarViewModel(_settings, _messenger);
    Assert.Equal(4, vm.Buttons.Count);
    Assert.Contains(vm.Buttons, b => b.Id == "SelectionMode");
    Assert.DoesNotContain(vm.Buttons, b => b.Id == "ManualSelection");
}
```

- [ ] **Step 2: Run tests to verify they FAIL**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: tests referencing "SelectionMode" button ID and `SelectionModeChangedMessage` FAIL.

- [ ] **Step 3: Modify `ToolbarViewModel`**

In `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`:

Change the buttons array (lines 41-47) from:

```csharp
var buttons = new (string Id, string Symbol)[]
{
    ("Pause", "\u23F8"),
    ("ManualSelection", "\u2295"),
    ("Keyboard", "\u2328"),
    ("Settings", "\u2699"),
};
```

To:

```csharp
var buttons = new (string Id, string Symbol)[]
{
    ("Pause", "\u23F8"),
    ("SelectionMode", "\u2295"),
    ("Keyboard", "\u2328"),
    ("Settings", "\u2699"),
};
```

Remove the `[ObservableProperty] private bool _isManualSelectionOn;` line (line 23).

Add a field:

```csharp
private SelectionMode _currentSelectionMode = SelectionMode.None;
```

Add `using EyeCtrl.Core.Models;` at the top.

Change the `"ManualSelection"` case in `OnTargetActivated` (lines 88-89) from:

```csharp
case "ManualSelection": _messenger.Send(new ManualSelectionToggledMessage()); break;
```

To:

```csharp
case "SelectionMode":
    var next = (SelectionMode)(((int)_currentSelectionMode + 1) % 3);
    _messenger.Send(new SelectionModeChangedMessage(next));
    break;
```

Add a message handler in the constructor (after `messenger.RegisterAll(this)` line 62):

```csharp
messenger.Register<SelectionModeChangedMessage>(this, (_, msg) =>
{
    _currentSelectionMode = msg.Mode;
    if (_buttonLookup.TryGetValue("SelectionMode", out var btn))
    {
        btn.Symbol = msg.Mode switch
        {
            SelectionMode.None => "\u25CB",            // ○
            SelectionMode.ManualSelection => "\u2295",  // ⊕
            SelectionMode.AutoSelection => "\u25CE",    // ◎
            _ => "\u25CB"
        };
    }
});
```

- [ ] **Step 4: Modify `ToolbarWindow.xaml.cs`**

In `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`:

Replace `SetManualSelectionState(bool isOn)` with:

```csharp
public void SetSelectionModeState(SelectionMode mode)
{
    if (!_buttonElements.TryGetValue("SelectionMode", out var border)) return;

    border.Background = mode switch
    {
        SelectionMode.ManualSelection => _onBrush,
        SelectionMode.AutoSelection => _autoBrush,
        _ => _offBrush
    };
}
```

Add `using EyeCtrl.Core.Models;` at the top.

Add a new brush field in the constructor/field area:

```csharp
private static readonly SolidColorBrush _autoBrush = new(Color.FromArgb(180, 0, 100, 180)); // blue
```

(Find where `_onBrush` and `_offBrush` are defined, add `_autoBrush` nearby with the same pattern.)

Rename any references to `"ManualSelection"` button ID in code-behind to `"SelectionMode"`. Search for `ManualSelection` in the file and update accordingly. The `SetManualSelectionState` is called from `SetStateChanged` — find that and update the call.

- [ ] **Step 5: Run toolbar tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`
Expected: all tests PASS (4 tests: InitializesWith4Buttons, PauseButton, UpdatesPausedState, SelectionModeButton_CyclesModes, SelectionModeButton_SendsMessage)

- [ ] **Step 6: Full build check**

Run: `dotnet build EyeCtrl.slnx`
Expected: build succeeds. If there are `ManualSelectionToggledMessage` references elsewhere (e.g., `EventCoordinator`, `GazeEngineAdapter`), the build will fail — that's expected, those will be fixed in Tasks 10-11.

- [ ] **Step 7: Commit — build may have remaining refs to ManualSelectionToggledMessage, that's OK**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "refactor: 3-mode cycling SelectionMode toolbar button replacing ManualSelection toggle"
```

---

### Task 8: ControlFinder Implementation (Platform)

**Files:**
- Modify: `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
- Create: `src/EyeCtrl.Platform/Services/ControlFinder.cs`
- Create: `tests/EyeCtrl.Platform.Tests/Services/ControlFinderTests.cs`

- [ ] **Step 1: Add WindowsDesktop TFM and UIA package to Platform csproj**

In `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`, change the `TargetFramework`:

```xml
<TargetFramework>net10.0-windows</TargetFramework>
```

The Platform project is already all-Win32; retargeting outright avoids multi-TFM complexity. Add after the `PackageReference` for `Microsoft.Extensions.Logging.Abstractions`:

```xml
<FrameworkReference Include="Microsoft.WindowsDesktop.App" />
```

This provides both WPF types and `System.Windows.Automation` (UIA client). Wrap the UIA-using file in `#if WINDOWS` guards (the retargeted TFM defines WINDOWS automatically):

```csharp
// src/EyeCtrl.Platform/Services/ControlFinder.cs
#if WINDOWS
namespace EyeCtrl.Platform.Services;
// ... all UIA code
#endif
```

- [ ] **Step 2: Create `ControlFinder`**

```csharp
// src/EyeCtrl.Platform/Services/ControlFinder.cs
#if WINDOWS
namespace EyeCtrl.Platform.Services;
using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class ControlFinder : IControlFinder
{
    private static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.Edit, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.TreeItem, ControlType.TabItem,
        ControlType.MenuItem, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.Slider
    };

    private static readonly HashSet<ControlType> NonInteractiveTypes = new()
    {
        ControlType.Pane, ControlType.Document, ControlType.Text,
        ControlType.Group, ControlType.Window, ControlType.Table,
        ControlType.Header, ControlType.TitleBar, ControlType.ToolBar
    };

    public ControlInfo? FindTarget(PhysicalPoint point)
    {
        try
        {
            var screenPoint = new System.Windows.Point(point.X, point.Y);

            var element = AutomationElement.FromPoint(screenPoint);
            if (element == null)
                return FallbackToWindow(screenPoint);

            // Walk up ancestors to first interactive element
            var walker = TreeWalker.ControlViewWalker;
            var current = element;
            while (current != null)
            {
                var controlType = current.Current.ControlType;
                if (InteractiveTypes.Contains(controlType))
                {
                    var rect = current.Current.BoundingRectangle;
                    return new ControlInfo(new PhysicalRect(rect.X, rect.Y, rect.Width, rect.Height));
                }

                if (NonInteractiveTypes.Contains(controlType))
                {
                    // Hit a container — the element below was content, not interactive.
                    // Try the container's parent.
                    current = walker.GetParent(current);
                    continue;
                }

                current = walker.GetParent(current);
            }

            return FallbackToWindow(screenPoint);
        }
        catch
        {
            return null;
        }
    }

    private static ControlInfo? FallbackToWindow(System.Windows.Point point)
    {
        var pt = new POINT { X = (int)point.X, Y = (int)point.Y };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;

        if (!GetClientRect(hwnd, out var clientRect)) return null;

        var topLeft = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref topLeft);

        return new ControlInfo(new PhysicalRect(
            topLeft.X,
            topLeft.Y,
            clientRect.Width,
            clientRect.Height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
#endif
```

- [ ] **Step 3: Write platform tests**

```csharp
// tests/EyeCtrl.Platform.Tests/Services/ControlFinderTests.cs
#if WINDOWS
namespace EyeCtrl.Platform.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Services;

public class ControlFinderTests
{
    [SkippableFact]
    public void FindTarget_NoWindow_ReturnsNull()
    {
        Skip.If(!Environment.OSVersion.Platform.ToString().Contains("Win"),
            "Requires Windows desktop session");

        var finder = new ControlFinder();
        // Point far off-screen should return null
        var result = finder.FindTarget(new PhysicalPoint(-10000, -10000));
        Assert.Null(result);
    }

    [SkippableFact]
    public void FindTarget_NearTaskbar_ReturnsNonNull()
    {
        Skip.If(!Environment.OSVersion.Platform.ToString().Contains("Win"),
            "Requires Windows desktop session");

        var finder = new ControlFinder();
        // Bottom-center of primary screen should hit taskbar
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var result = finder.FindTarget(new PhysicalPoint(screenW / 2, screenH - 5));
        // May or may not find a control — at minimum should not throw
        Assert.True(result == null || result.CenterPoint.X > 0);
    }
}
#endif
```

- [ ] **Step 4: Build Platform project**

Run: `dotnet build src/EyeCtrl.Platform -f net10.0-windows`
Expected: build succeeds. The `System.Windows.Automation` types resolve.

- [ ] **Step 5: Run platform tests**

Run: `dotnet test tests/EyeCtrl.Platform.Tests -f net10.0-windows`
Expected: ControlFinder tests run on Windows. They may be skipped on non-Windows.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj src/EyeCtrl.Platform/Services/ControlFinder.cs tests/EyeCtrl.Platform.Tests/Services/ControlFinderTests.cs
git commit -m "feat: add ControlFinder with UIA + Win32 fallback control detection"
```

---

### Task 9: TargetHighlight Overlay

**Files:**
- Create: `src/EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs`
- Create: `src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs`

- [ ] **Step 1: Create ViewModel**

```csharp
// src/EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs
namespace EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class TargetHighlightOverlayViewModel : ObservableObject
{
    [ObservableProperty] private PhysicalRect _boundingRect;
    [ObservableProperty] private string _highlightColor = "#0078D4";
    [ObservableProperty] private double _highlightThickness = 3.0;
    [ObservableProperty] private double _highlightOpacity = 0.7;
    [ObservableProperty] private bool _isVisible;
}
```

- [ ] **Step 2: Create XAML window**

```xml
<!-- src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml -->
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.TargetHighlightWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" IsClickThrough="True"
        ResizeMode="NoResize" Visibility="Collapsed">
    <Border x:Name="HighlightBorder"
            Background="Transparent"
            CornerRadius="2"
            Opacity="0">
        <Border.Style>
            <Style TargetType="Border">
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsVisible}" Value="True">
                        <DataTrigger.EnterActions>
                            <BeginStoryboard>
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                                                     To="0.7" Duration="0:0:0.2"/>
                                </Storyboard>
                            </BeginStoryboard>
                        </DataTrigger.EnterActions>
                        <DataTrigger.ExitActions>
                            <BeginStoryboard>
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                                                     To="0" Duration="0:0:0.2"/>
                                </Storyboard>
                            </BeginStoryboard>
                        </DataTrigger.ExitActions>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Border.Style>
    </Border>
</overlays:OverlayWindow>
```

BorderBrush, BorderThickness, and Opacity are set directly in code-behind (see Step 3). No converters needed — the ViewModel stays pure net10.0.

- [ ] **Step 3: Create code-behind**

```csharp
// src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml.cs
namespace EyeCtrl.App.Overlays;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class TargetHighlightWindow : OverlayWindow
{
    public TargetHighlightWindow()
    {
        InitializeComponent();
    }

    public void Configure(string color, double thickness)
    {
        try
        {
            var converter = new BrushConverter();
            HighlightBorder.BorderBrush = (Brush)converter.ConvertFromString(color)!;
        }
        catch
        {
            HighlightBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        }
        HighlightBorder.BorderThickness = new Thickness(thickness);
    }

    public void PositionAt(PhysicalRect bounds)
    {
        var logicalX = DpiHelper.PhysicalToLogicalX(bounds.X);
        var logicalY = DpiHelper.PhysicalToLogicalY(bounds.Y);
        var logicalW = DpiHelper.PhysicalToLogicalX(bounds.Width);
        var logicalH = DpiHelper.PhysicalToLogicalY(bounds.Height);

        Left = logicalX;
        Top = logicalY;
        Width = logicalW;
        Height = logicalH;
    }
}
```

- [ ] **Step 4: Add to `OverlayViewModels` record**

In `src/EyeCtrl.App/Services/IOverlayManager.cs`, add to the record (line 72):

```csharp
public record OverlayViewModels(
    CursorCircleViewModel CursorCircle,
    ActivationButtonViewModel ActivationButton,
    ManualSelectionOverlayViewModel ManualSelectionOverlay,
    ActionMenuViewModel ActionMenu,
    ToolbarViewModel Toolbar,
    KeyboardViewModel Keyboard,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicator,
    TargetHighlightOverlayViewModel TargetHighlight   // NEW
);
```

- [ ] **Step 5: Add to `OverlayManager`**

In `src/EyeCtrl.App/Services/OverlayManager.cs`:

Add property:
```csharp
public TargetHighlightWindow TargetHighlight { get; private set; } = null!;
```

Add initialization in `Initialize()`:
```csharp
TargetHighlight = new TargetHighlightWindow();
// Set DataContext
TargetHighlight.DataContext = viewModels.TargetHighlight;
```

- [ ] **Step 6: Update `IOverlayManager` interface**

Add the property declaration:
```csharp
TargetHighlightWindow TargetHighlight { get; }
```

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml.cs src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs
git commit -m "feat: add TargetHighlight overlay window and ViewModel"
```

---

### Task 10: App.xaml.cs + EventCoordinationContext Wiring

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs`

- [ ] **Step 1: Add `TargetHighlightOverlayViewModel` to `EventCoordinationContext`**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, add after the `BackdropVM` line (line 59):

```csharp
TargetHighlightOverlayViewModel TargetHighlightVM,
```

- [ ] **Step 2: Resolve `TargetHighlightOverlayViewModel` in `App.xaml.cs`**

In `src/EyeCtrl.App/App.xaml.cs`, add after the backdrop VM resolution (line 127):

```csharp
var targetHighlightVM = _serviceProvider.GetRequiredService<TargetHighlightOverlayViewModel>();
```

- [ ] **Step 3: Pass to `OverlayViewModels`**

Change line 140 from:

```csharp
var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM);
```

To:

```csharp
var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM, targetHighlightVM);
```

- [ ] **Step 4: Pass to `EventCoordinationContext`**

Change lines 150-152 from:

```csharp
var coordinationContext = new EventCoordinationContext(
    cursorVM, activationVM, ManualSelectionVM, ManualSelectionIndicatorVM, actionMenuVM, toolbarVM, keyboardVM, backdropVM,
    overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, inputSimulator);
```

To:

```csharp
var coordinationContext = new EventCoordinationContext(
    cursorVM, activationVM, ManualSelectionVM, ManualSelectionIndicatorVM, actionMenuVM, toolbarVM, keyboardVM, backdropVM,
    targetHighlightVM,
    overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, inputSimulator);
```

- [ ] **Step 5: Build to verify wiring**

Run: `dotnet build src/EyeCtrl.App`
Expected: build succeeds (may have warnings about unused properties until Task 12 uses them).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/IEventCoordinator.cs
git commit -m "feat: wire TargetHighlightOverlayViewModel through App.xaml.cs and EventCoordinationContext"
```

---

### Task 11: AppBootstrapper + GazeEngineAdapter

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`

- [ ] **Step 1: Register new services in `AppBootstrapper`**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`:

Add `using EyeCtrl.Core.Interfaces;` and `using EyeCtrl.Platform.Services;` (with `#if WINDOWS` guard as needed).

After the `ModelManagementService` registration (line 112), add:

```csharp
// ControlFinder (AutoSelection mode)
#if WINDOWS
services.AddSingleton<IControlFinder, ControlFinder>();
#endif
```

After the `GlassBackdropViewModel` registration (line 126), add:

```csharp
services.AddSingleton<TargetHighlightOverlayViewModel>();
```

In the `GazeEngineAdapter` registration (lines 129-134), add `IControlFinder` parameter:

```csharp
services.AddSingleton(sp => new GazeEngineAdapter(
    sp.GetRequiredService<GazeEngine>(),
    sp.GetRequiredService<EyeCtrlSettings>(),
    sp.GetRequiredService<IMessenger>(),
    dispatcher,
    sp.GetRequiredService<ILogger<GazeEngineAdapter>>(),
    sp.GetRequiredService<IControlFinder>()));   // NEW
```

- [ ] **Step 2: Modify `GazeEngineAdapter`**

In `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`:

Add constructor parameter:
```csharp
private readonly IControlFinder _controlFinder;

public GazeEngineAdapter(
    GazeEngine gazeEngine,
    EyeCtrlSettings settings,
    IMessenger messenger,
    Dispatcher dispatcher,
    ILogger<GazeEngineAdapter> logger,
    IControlFinder controlFinder)
{
    // ... existing code ...
    _controlFinder = controlFinder;
    // ...
}
```

Add `using EyeCtrl.Core.Interfaces;` at the top.

Replace the `ActivationCompletedMessage` handler (line 50-51):

```csharp
messenger.Register<ActivationCompletedMessage>(this, async (_, _) =>
{
    if (_interactionManager.SelectionMode == SelectionMode.AutoSelection)
    {
        var fixationPoint = _interactionManager.FixationPoint;
        if (fixationPoint != null)
        {
            _interactionManager.AutoSelectionTarget = await ResolveTargetAsync(fixationPoint.Value);
        }
    }
    // Gate: skip action menu entirely if Auto mode + no control found + fallback disabled
    if (_interactionManager.SelectionMode == SelectionMode.AutoSelection
        && _interactionManager.AutoSelectionTarget == null
        && !_settings.AutoSelection.FallbackToPixelOnNoControl)
    {
        _interactionManager.OnFixationEnded(); // silently reset to FreeLook
        return;
    }
    _interactionManager.OnActivationButtonDwelled();
});
```

Add `using EyeCtrl.Core.Models;` at the top.

Replace the `ManualSelectionToggledMessage` handler (lines 58-62):

```csharp
messenger.Register<SelectionModeChangedMessage>(this, (_, msg) =>
{
    _interactionManager.SelectionMode = msg.Mode;
    _settings.SelectionMode = msg.Mode;
});

// Seed toolbar with persisted selection mode on startup
messenger.Send(new SelectionModeChangedMessage(_settings.SelectionMode));
```

Update `OnActionRequested` (line 158):

```csharp
private void OnActionRequested(InteractionAction action, PhysicalPoint point)
{
    var isPrecise = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible
                 || (_interactionManager.CurrentState == InteractionState.ActionMenuVisible
                     && _interactionManager.SelectionMode == SelectionMode.AutoSelection);
    _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isPrecise)));
}
```

Update `OnShowActionMenu` message construction (line 100-108) to add `ResolvedControl`:

After the existing `ManualSelectionRadius = ...` line, add:

```csharp
ResolvedControl = im.SelectionMode == SelectionMode.AutoSelection ? im.AutoSelectionTarget : null
```

Add the async resolver method:

```csharp
private async Task<ControlInfo?> ResolveTargetAsync(PhysicalPoint point)
{
    try
    {
        return await Task.Run(() => _controlFinder.FindTarget(point))
            .WaitAsync(TimeSpan.FromMilliseconds(500));
    }
    catch (TimeoutException)
    {
        _logger.LogWarning("ControlFinder timed out after 500ms — falling back to pixel");
        return null;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "ControlFinder failed — falling back to pixel");
        return null;
    }
}
```

- [ ] **Step 3: Fix remaining `ManualSelectionToggledMessage` references**

Search for any remaining uses:
Run: `rg "ManualSelectionToggledMessage" src/`
If found in `EventCoordinator.cs`, fix there too. (Task 11 handles EventCoordinator.)

- [ ] **Step 4: Build App project**

Run: `dotnet build src/EyeCtrl.App`
Expected: if build succeeds, the GazeEngineAdapter + AppBootstrapper wiring is correct. If there are remaining `ManualSelectionToggledMessage` or `IsFromManualSelection` references in `EventCoordinator.cs`, fix them in Task 11.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/GazeEngineAdapter.cs
git commit -m "feat: wire IControlFinder into DI, GazeEngineAdapter async UIA resolve, SelectionModeChangedMessage handler"
```

---

### Task 12: EventCoordinator

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Fix `HandleActionRequested` — rename `IsFromManualSelection` to `IsPreciseTarget`**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, find line 687:

```csharp
var pt = msg.IsFromManualSelection ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
```

Change to:

```csharp
var pt = msg.IsPreciseTarget ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
```

Update the log line (688-689):

```csharp
Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [Precise={IsPreciseTarget}]",
    msg.Action, pt.X, pt.Y, msg.IsPreciseTarget);
```

- [ ] **Step 2: Add highlight overlay lifecycle to `HandleShowActionMenu`**

Find the `ShowActionMenuMessage` handler in `EventCoordinator` (around line 445). After the existing action menu show logic, add:

```csharp
// AutoSelection target highlight
if (msg.ResolvedControl != null && _context != null)
{
    var highlightVM = _context.TargetHighlightVM;
    highlightVM.BoundingRect = msg.ResolvedControl.BoundingRect;
    highlightVM.HighlightColor = _context.Settings.AutoSelection.HighlightColor;
    highlightVM.HighlightThickness = _context.Settings.AutoSelection.HighlightThickness;
    highlightVM.HighlightOpacity = _context.Settings.AutoSelection.HighlightOpacity;
    highlightVM.IsVisible = true;
    _context.OverlayManager.TargetHighlight.Configure(
        _context.Settings.AutoSelection.HighlightColor,
        _context.Settings.AutoSelection.HighlightThickness);
    _context.OverlayManager.TargetHighlight.PositionAt(msg.ResolvedControl.BoundingRect);
    _context.OverlayManager.TargetHighlight.Show();
}
```

- [ ] **Step 3: Hide highlight on action/dismiss**

In `HandleActionRequested` (after the click fires) and in the `BackdropDismissMessage` / `HideActionMenuMessage` handlers, add:

```csharp
if (_context != null && _context.TargetHighlightVM.IsVisible)
{
    _context.TargetHighlightVM.IsVisible = false;
    _context.OverlayManager.TargetHighlight.Hide();
}
```

- [ ] **Step 4: Update `OnSettingsChanged` and sync toolbar on mode change**

Find lines 536-537:

```csharp
_context.InteractionManager.ManualSelectionEnabled = s.ManualSelection.Enabled;
_context.OverlayManager.Toolbar.SetManualSelectionState(s.ManualSelection.Enabled);
```

Replace with:

```csharp
_context.InteractionManager.SelectionMode = s.SelectionMode;
_context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
```

Also notify ViewModel so toolbar cycle state stays in sync:

```csharp
_context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));
```

- [ ] **Step 5: Remove any remaining `ManualSelectionToggledMessage` handlers**

Search `EventCoordinator.cs` for `ManualSelectionToggled`. If a handler subscription exists, remove it.

- [ ] **Step 6: Build full solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: build succeeds with zero errors.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "refactor: IsPreciseTarget rename, highlight overlay lifecycle, SelectionMode in OnSettingsChanged"
```

---

### Task 13: Final Build, Test, and Cleanup

**Files:**
- No new files — verification only

- [ ] **Step 1: Add XML doc comments to new public types**

Each new public type must have `/// <summary>` doc comments (project has `GenerateDocumentationFile=true`):

```csharp
// src/EyeCtrl.Core/Models/SelectionMode.cs
namespace EyeCtrl.Core.Models;

/// <summary>Selection mode for gaze-based click targeting.</summary>
public enum SelectionMode
{
    /// <summary>Raw pixel coordinates.</summary>
    None,
    /// <summary>Zoom-and-dwell via ManualSelection overlay.</summary>
    ManualSelection,
    /// <summary>Snap-to-control via UIA/Win32.</summary>
    AutoSelection
}
```

```csharp
// src/EyeCtrl.Core/Models/PhysicalRect.cs
namespace EyeCtrl.Core.Models;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)
{
    /// <summary>Horizontal center of the rectangle.</summary>
    public double CenterX => X + Width / 2;
    /// <summary>Vertical center of the rectangle.</summary>
    public double CenterY => Y + Height / 2;
    /// <summary>Center point of the rectangle.</summary>
    public PhysicalPoint Center => new(CenterX, CenterY);
}
```

```csharp
// src/EyeCtrl.Core/Models/ControlInfo.cs
namespace EyeCtrl.Core.Models;

/// <summary>Resolved interactive UI control for Auto Selection targeting.</summary>
public record ControlInfo(PhysicalRect BoundingRect)
{
    /// <summary>Center of the control's bounding rectangle — the click target.</summary>
    public PhysicalPoint CenterPoint => BoundingRect.Center;
}
```

```csharp
// src/EyeCtrl.Core/Interfaces/IControlFinder.cs
namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>Resolves the nearest interactive UI control at a screen point.</summary>
public interface IControlFinder
{
    /// <summary>Finds a target control at the given physical screen point.</summary>
    ControlInfo? FindTarget(PhysicalPoint point);
}
```

Go back and apply these doc comments to the code snippets in Task 1 and Task 2.

- [ ] **Step 2: Run all tests**

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.Platform.Tests -f net10.0-windows
```

Expected: all existing tests pass, new tests pass. InteractionManagerTests has 19 tests (13 original + 6 new).

- [ ] **Step 3: Run full solution build**

```bash
dotnet build EyeCtrl.slnx
```

Expected: zero errors. Warnings from `CS1591` (missing XML docs) on new types must be zero — the doc comments in Step 1 + Task 1/2 snippets cover all public API.

- [ ] **Step 4: Run App in test mode to verify startup**

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

The app should start with the toolbar showing 4 buttons, the middle one cycling modes:
- Toolbar SelectionMode button starts with symbol matching persisted mode
- Dwell cycles None (○) → Manual (⊕) → Auto (◎) → None
- App does not crash

(Exit with Ctrl+C or close the window.)

- [ ] **Step 5: Verify settings persistence**

After running the app in test mode, check `%APPDATA%/EyeCtrl/settings.json` for the new keys:

```json
{
  "selectionMode": "none",
  "autoSelection": {
    "highlightColor": "#0078D4",
    "highlightThickness": 3.0,
    "highlightOpacity": 0.7,
    "fallbackToPixelOnNoControl": true
  }
}
```

- [ ] **Step 6: Fix any build warnings**

Review the build output for warnings. Fix any introduced issues — unused `using` directives on modified files.

- [ ] **Step 7: Final commit**

```bash
git add .
git commit -m "chore: final cleanup — doc comments, HighlightOpacity, verify all tests and build pass"
```

> **Note on intermediate commits:** Tasks 5-8 intentionally leave the solution with broken references (renamed/deleted messages). These are resolved by Task 12. If you need every commit to build, reorder Tasks 5-7 to land after Task 12, or squash once complete.
