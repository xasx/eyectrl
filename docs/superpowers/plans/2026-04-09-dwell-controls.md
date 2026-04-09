# Shared Dwell Control Library Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract reusable dwell-enabled controls from SettingsWindow into a shared library, consolidate the ViewModel's dwell registration API behind `IDwellRegistrar`, and migrate SettingsWindow to use the new controls.

**Architecture:** New `IDwellRegistrar` interface and `DwellCategory` enum in `EyeCtrl.Core.Processing` (alongside existing `DwellDetector`/`DwellTarget`). `SettingsWindowViewModel` implements `IDwellRegistrar`. New control classes (`DwellButton`, `DwellToggle`, `DwellSlider`, `DwellComboBox`, `DwellColorPicker`) in `EyeCtrl.App.Controls` use `IDwellRegistrar` to self-register. SettingsWindow page builders switch from calling `AddSlider()`/`AddToggle()` to instantiating control objects.

**Tech Stack:** C# / .NET 8 / WPF, xUnit, CommunityToolkit.Mvvm

**Spec:** `docs/superpowers/specs/2026-04-09-settings-dwell-controls-design.md`

**Important dependency note:** `EyeCtrl.ViewModels` references `EyeCtrl.Core` but NOT `EyeCtrl.App`. Therefore `IDwellRegistrar` and `DwellCategory` MUST live in `EyeCtrl.Core.Processing`, not in `EyeCtrl.App.Controls`. The controls in `EyeCtrl.App.Controls` consume the interface; the ViewModel in `EyeCtrl.ViewModels` implements it.

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs` | Interface + `DwellCategory` enum |
| `src/EyeCtrl.App/Controls/ColorBlend.cs` | Static `Blend()` color utility |
| `src/EyeCtrl.App/Controls/DwellControl.cs` | Abstract base: Attach/Detach/UpdatePosition/UpdateVisual |
| `src/EyeCtrl.App/Controls/IDwellComposite.cs` | Interface for multi-target controls |
| `src/EyeCtrl.App/Controls/DwellButton.cs` | Atomic dwell button |
| `src/EyeCtrl.App/Controls/DwellLabel.cs` | Non-dwell text label |
| `src/EyeCtrl.App/Controls/DwellToggle.cs` | Boolean toggle |
| `src/EyeCtrl.App/Controls/DwellSlider.cs` | Numeric slider (composes 2 DwellButtons) |
| `src/EyeCtrl.App/Controls/DwellComboBox.cs` | Option list (composes N DwellButtons) |
| `src/EyeCtrl.App/Controls/DwellColorPicker.cs` | Color swatch picker (composes N DwellButtons) |

### Modified Files

| File | Changes |
|------|---------|
| `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs` | Implement `IDwellRegistrar`, consolidate 12 methods into 4 + helpers |
| `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` | Update to new API, add IDwellRegistrar-specific tests |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Replace builder methods with control instantiation, simplify update loops |

---

## Task 1: IDwellRegistrar Interface and DwellCategory Enum

**Files:**
- Create: `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` (IDwellRegistrar tests added in Task 2)

- [ ] **Step 1: Create IDwellRegistrar.cs**

```csharp
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public enum DwellCategory { Tab, Control, Pagination, Modal }

/// <summary>
/// Contract for dwell target registration, position updates, and progress queries.
/// Each overlay that uses dwell detection implements this independently,
/// wrapping its own DwellDetector instance(s).
/// </summary>
public interface IDwellRegistrar
{
    void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated);
    void Unregister(string id, DwellCategory category);
    void UpdatePosition(string id, DwellCategory category, PhysicalPoint center);
    double GetProgress(string id, DwellCategory category);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Processing/IDwellRegistrar.cs
git commit -m "feat: add IDwellRegistrar interface and DwellCategory enum"
```

---

## Task 2: Refactor SettingsWindowViewModel to Implement IDwellRegistrar

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs:1-303`
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs:1-412`

This task rewrites the ViewModel's public API while preserving all internal behavior. The 4 DwellDetector instances stay. Event wiring stays. Only the public surface changes.

- [ ] **Step 1: Write failing tests for the new IDwellRegistrar API**

Add these tests to the **end** of `SettingsWindowViewModelTests.cs` (before the final `}`), in a new region:

```csharp
#region IDwellRegistrar Tests

[Fact]
public void Register_Tab_CreatesDwellTarget()
{
    var vm = CreateViewModel();
    bool activated = false;
    vm.Register("TestTab", DwellCategory.Tab, 85, 600, () => activated = true);
    vm.UpdatePosition("TestTab", DwellCategory.Tab, new PhysicalPoint(100, 100));

    // Simulate complete dwell
    for (int i = 0; i < 20; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

    Assert.True(activated);
}

[Fact]
public void Register_Control_CreatesDwellTarget()
{
    var vm = CreateViewModel();
    bool activated = false;
    vm.Register("TestCtrl", DwellCategory.Control, 50, 600, () => activated = true);
    vm.UpdatePosition("TestCtrl", DwellCategory.Control, new PhysicalPoint(150, 150));

    for (int i = 0; i < 20; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(150, 150), i * 33000));

    Assert.True(activated);
}

[Fact]
public void GetProgress_ReturnsCurrentDwellProgress()
{
    var vm = CreateViewModel();
    vm.Register("ProgressTest", DwellCategory.Control, 50, 600, () => { });
    vm.UpdatePosition("ProgressTest", DwellCategory.Control, new PhysicalPoint(100, 100));

    // Simulate partial dwell
    for (int i = 0; i < 10; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

    double progress = vm.GetProgress("ProgressTest", DwellCategory.Control);
    Assert.True(progress > 0);
    Assert.True(progress < 1);
}

[Fact]
public void Unregister_RemovesDwellTarget()
{
    var vm = CreateViewModel();
    bool activated = false;
    vm.Register("RemoveMe", DwellCategory.Control, 50, 600, () => activated = true);
    vm.UpdatePosition("RemoveMe", DwellCategory.Control, new PhysicalPoint(100, 100));

    vm.Unregister("RemoveMe", DwellCategory.Control);

    for (int i = 0; i < 20; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

    Assert.False(activated);
}

[Fact]
public void ClearCategory_RemovesAllInCategory()
{
    var vm = CreateViewModel();
    int counter = 0;
    vm.Register("c1", DwellCategory.Control, 50, 600, () => counter++);
    vm.Register("c2", DwellCategory.Control, 50, 600, () => counter++);
    vm.UpdatePosition("c1", DwellCategory.Control, new PhysicalPoint(100, 100));
    vm.UpdatePosition("c2", DwellCategory.Control, new PhysicalPoint(200, 200));

    vm.ClearCategory(DwellCategory.Control);

    for (int i = 0; i < 20; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

    Assert.Equal(0, counter);
}

[Fact]
public void SimulateActivated_InvokesRegisteredAction()
{
    var vm = CreateViewModel();
    bool activated = false;
    vm.Register("SimTest", DwellCategory.Control, 50, 600, () => activated = true);

    vm.SimulateActivated("SimTest", DwellCategory.Control);

    Assert.True(activated);
}

[Fact]
public void Register_Pagination_WorksWithExistingLogic()
{
    var vm = CreateViewModel();
    vm.Settings.ShowTab("Cursor"); // Tab with multiple pages

    vm.Register("pag_next", DwellCategory.Pagination, 60, 600, () => { });
    vm.UpdatePosition("pag_next", DwellCategory.Pagination, new PhysicalPoint(200, 200));

    for (int i = 0; i < 10; i++)
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), i * 33000));

    double progress = vm.GetProgress("pag_next", DwellCategory.Pagination);
    Assert.True(progress > 0);
}

#endregion
```

Also add the using directive at the top of the test file:

```csharp
using EyeCtrl.Core.Processing;
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests/ --filter "Register_Tab_CreatesDwellTarget|Register_Control_CreatesDwellTarget|GetProgress_ReturnsCurrentDwellProgress|Unregister_RemovesDwellTarget|ClearCategory_RemovesAllInCategory|SimulateActivated_InvokesRegisteredAction|Register_Pagination_WorksWithExistingLogic"`
Expected: FAIL — `SettingsWindowViewModel` doesn't have `Register`, `Unregister`, `UpdatePosition`, `GetProgress`, `ClearCategory`, `SimulateActivated` methods yet.

- [ ] **Step 3: Implement IDwellRegistrar on SettingsWindowViewModel**

Replace the full content of `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs` with:

```csharp
namespace EyeCtrl.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class SettingsWindowViewModel : ObservableObject,
    IDwellRegistrar,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IMessenger _messenger;
    private bool _disposed;

    private readonly DwellDetector _tabDwell;
    private readonly DwellDetector _controlDwell;
    private readonly DwellDetector _pagDwell;
    private readonly DwellDetector _modalDwell;

    // Actions registered per category
    private readonly Dictionary<string, Action> _tabActions = new();
    private readonly Dictionary<string, Action> _controlActions = new();
    private readonly Dictionary<string, Action> _pagActions = new();
    private readonly Dictionary<string, Action> _modalActions = new();

    [ObservableProperty] private Dictionary<string, double> _tabProgress = new();
    [ObservableProperty] private Dictionary<string, double> _controlProgress = new();
    [ObservableProperty] private Dictionary<string, double> _pagProgress = new();
    [ObservableProperty] private Dictionary<string, double> _modalProgress = new();

    [ObservableProperty] private bool _isModalVisible;
    [ObservableProperty] private string _modalMessage = "";
    private Action? _modalYesAction;

    [ObservableProperty] private string _activeTabId = "";

    public SettingsViewModel Settings => _settingsViewModel;

    public SettingsWindowViewModel(
        SettingsViewModel settingsViewModel,
        IMessenger messenger,
        DwellSettings dwellSettings)
    {
        _settingsViewModel = settingsViewModel;
        _messenger = messenger;

        _tabDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _controlDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _pagDwell = new DwellDetector(dwellSettings.DwellCooldownMs);
        _modalDwell = new DwellDetector(dwellSettings.DwellCooldownMs);

        _tabDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Tab);
        _tabDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Tab);

        _controlDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Control);
        _controlDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Control);

        _pagDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Pagination);
        _pagDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Pagination);

        _modalDwell.TargetActivated += id => OnTargetActivated(id, DwellCategory.Modal);
        _modalDwell.TargetProgress += (id, p) => OnTargetProgress(id, p, DwellCategory.Modal);

        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (_disposed) return;

        _tabDwell.Update(message.Position, message.TimestampMicroseconds);
        _controlDwell.Update(message.Position, message.TimestampMicroseconds);
        _pagDwell.Update(message.Position, message.TimestampMicroseconds);
        if (IsModalVisible)
            _modalDwell.Update(message.Position, message.TimestampMicroseconds);
    }

    // ────── IDwellRegistrar ──────

    public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated)
    {
        var detector = GetDetector(category);
        detector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0), radiusPhysical, dwellMs));
        GetActions(category)[id] = onActivated;
    }

    public void Unregister(string id, DwellCategory category)
    {
        GetDetector(category).RemoveTarget(id);
        GetActions(category).Remove(id);
    }

    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center)
    {
        GetDetector(category).UpdateTargetCenter(id, center);
    }

    public double GetProgress(string id, DwellCategory category)
    {
        return GetDetector(category).GetProgress(id);
    }

    // ────── Category helpers ──────

    public void ClearCategory(DwellCategory category)
    {
        GetDetector(category).ClearTargets();
        GetActions(category).Clear();
        GetProgressDict(category).Clear();
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    /// <summary>For testing: simulate a target activation without gaze dwell.</summary>
    public void SimulateActivated(string id, DwellCategory category)
    {
        OnTargetActivated(id, category);
    }

    private DwellDetector GetDetector(DwellCategory category) => category switch
    {
        DwellCategory.Tab => _tabDwell,
        DwellCategory.Control => _controlDwell,
        DwellCategory.Pagination => _pagDwell,
        DwellCategory.Modal => _modalDwell,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private Dictionary<string, Action> GetActions(DwellCategory category) => category switch
    {
        DwellCategory.Tab => _tabActions,
        DwellCategory.Control => _controlActions,
        DwellCategory.Pagination => _pagActions,
        DwellCategory.Modal => _modalActions,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private Dictionary<string, double> GetProgressDict(DwellCategory category) => category switch
    {
        DwellCategory.Tab => TabProgress,
        DwellCategory.Control => ControlProgress,
        DwellCategory.Pagination => PagProgress,
        DwellCategory.Modal => ModalProgress,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static string GetProgressPropertyName(DwellCategory category) => category switch
    {
        DwellCategory.Tab => nameof(TabProgress),
        DwellCategory.Control => nameof(ControlProgress),
        DwellCategory.Pagination => nameof(PagProgress),
        DwellCategory.Modal => nameof(ModalProgress),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    // ────── Unified event handlers ──────

    private void OnTargetActivated(string id, DwellCategory category)
    {
        // Special cases for built-in behaviors
        switch (category)
        {
            case DwellCategory.Tab:
                if (id == "close")
                {
                    _messenger.Send(new CloseSettingsMessage());
                    return;
                }
                _settingsViewModel.ShowTab(id);
                ActiveTabId = id;
                break;

            case DwellCategory.Pagination:
                if (id == "pag_prev") _settingsViewModel.PreviousPage();
                else if (id == "pag_next") _settingsViewModel.NextPage();
                break;

            case DwellCategory.Modal:
                if (id == "modal_yes")
                {
                    _modalYesAction?.Invoke();
                    HideModal();
                }
                else if (id == "modal_no")
                {
                    HideModal();
                }
                return; // Modal actions are handled here, not in _modalActions
        }

        // Invoke registered action (for controls, and any custom tab/pagination actions)
        if (GetActions(category).TryGetValue(id, out var action))
            action.Invoke();
    }

    private void OnTargetProgress(string id, double progress, DwellCategory category)
    {
        GetProgressDict(category)[id] = progress;
        OnPropertyChanged(GetProgressPropertyName(category));
    }

    // ────── Modal (kept as high-level API) ──────

    public void ShowModal(string message, Action onYes)
    {
        _modalDwell.ClearTargets();
        ModalProgress.Clear();
        OnPropertyChanged(nameof(ModalProgress));

        ModalMessage = message;
        _modalYesAction = onYes;
        IsModalVisible = true;
    }

    public void HideModal()
    {
        IsModalVisible = false;
        ModalMessage = "";
        _modalDwell.ClearTargets();
        _modalYesAction = null;
    }

    // ────── Legacy API (kept for backward compatibility during migration) ──────
    // These delegate to the IDwellRegistrar methods. Remove after full migration.

    public void AddTabTarget(string tabId, double radius, int dwellMs)
        => Register(tabId, DwellCategory.Tab, radius, dwellMs, () => { });

    public void UpdateTabButtonPosition(string tabId, PhysicalPoint center)
        => UpdatePosition(tabId, DwellCategory.Tab, center);

    public void AddPaginationTarget(string targetId, double radius, int dwellMs)
        => Register(targetId, DwellCategory.Pagination, radius, dwellMs, () => { });

    public void UpdatePaginationButtonPosition(string targetId, PhysicalPoint center)
        => UpdatePosition(targetId, DwellCategory.Pagination, center);

    public void RegisterControlAction(string controlId, Action action)
    {
        _controlActions[controlId] = action;
    }

    public void AddControlTarget(string controlId, double radius, int dwellMs)
    {
        _controlDwell.AddTarget(new DwellTarget(controlId, new PhysicalPoint(0, 0), radius, dwellMs));
    }

    public void UpdateControlPosition(string controlId, PhysicalPoint center)
        => UpdatePosition(controlId, DwellCategory.Control, center);

    public void SimulateControlActivated(string controlId)
        => SimulateActivated(controlId, DwellCategory.Control);

    public void ClearControlTargets()
        => ClearCategory(DwellCategory.Control);

    public void AddModalTarget(string buttonId, double radius, int dwellMs)
    {
        _modalDwell.AddTarget(new DwellTarget(buttonId, new PhysicalPoint(0, 0), radius, dwellMs));
    }

    public void UpdateModalButtonPosition(string buttonId, PhysicalPoint center)
        => UpdatePosition(buttonId, DwellCategory.Modal, center);

    public void SimulateModalButtonActivated(string buttonId)
        => SimulateActivated(buttonId, DwellCategory.Modal);

    // ────── Dispose ──────

    public void Dispose()
    {
        if (_disposed) return;
        _messenger.UnregisterAll(this);
        _settingsViewModel.Dispose();
        _disposed = true;
    }
}
```

Key changes from original:
- Implements `IDwellRegistrar`
- All 4 TargetActivated/TargetProgress handlers unified into `OnTargetActivated`/`OnTargetProgress`
- Action dictionaries per category (previously only `_controlActions`)
- Tab activation (ShowTab, close) and pagination (prev/next) behavior preserved in `OnTargetActivated` switch
- Modal activation (yes/no) preserved in `OnTargetActivated` switch
- Legacy methods kept as thin wrappers during migration — delete in Task 10

- [ ] **Step 4: Run ALL tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests/ -v normal`
Expected: ALL tests pass (both old API tests via legacy wrappers AND new IDwellRegistrar tests)

If any test fails, the legacy wrappers need adjustment. The most likely issue: `AddTabTarget` previously didn't register an action, but `Register` does. The old tab tests don't pass actions — the legacy `AddTabTarget` passes `() => { }` as a no-op, and the real tab activation logic is in `OnTargetActivated`'s switch case. This should work.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs
git commit -m "feat: implement IDwellRegistrar on SettingsWindowViewModel

Consolidates 12 separate dwell registration methods into unified
Register/Unregister/UpdatePosition/GetProgress API. Legacy methods
kept as thin wrappers for backward compatibility during migration."
```

---

## Task 3: ColorBlend Utility and DwellControl Base Class

**Files:**
- Create: `src/EyeCtrl.App/Controls/ColorBlend.cs`
- Create: `src/EyeCtrl.App/Controls/IDwellComposite.cs`
- Create: `src/EyeCtrl.App/Controls/DwellControl.cs`

- [ ] **Step 1: Create ColorBlend.cs**

Extract the static `Blend` method from `SettingsWindow.xaml.cs:1141-1149`:

```csharp
namespace EyeCtrl.App.Controls;

using System.Windows.Media;

public static class ColorBlend
{
    public static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A * (1 - t) + to.A * t),
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }
}
```

- [ ] **Step 2: Create IDwellComposite.cs**

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows.Controls;

public interface IDwellComposite
{
    void Attach(Panel parent);
    void Detach(Panel parent);
    IEnumerable<DwellControl> Children { get; }
}
```

- [ ] **Step 3: Create DwellControl.cs**

```csharp
namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public abstract class DwellControl
{
    public string DwellId { get; }
    public Border Root { get; }
    public IDwellRegistrar Registrar { get; }
    public DwellCategory Category { get; }
    public double RadiusPhysical { get; }
    public int DwellMs { get; }

    protected DwellControl(
        IDwellRegistrar registrar,
        string dwellId,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        Registrar = registrar;
        DwellId = dwellId;
        Category = category;
        RadiusPhysical = radiusPhysical;
        DwellMs = dwellMs;
        Root = CreateRoot();
    }

    protected abstract Border CreateRoot();
    protected abstract void OnActivated();
    protected abstract void ApplyProgress(double progress);

    /// <summary>Adds Root to panel and registers dwell target.</summary>
    public void Attach(Panel parent)
    {
        parent.Children.Add(Root);
        RegisterDwell();
    }

    /// <summary>Removes Root from panel and unregisters dwell target.</summary>
    public void Detach(Panel parent)
    {
        parent.Children.Remove(Root);
        UnregisterDwell();
    }

    /// <summary>
    /// Registers the dwell target without adding Root to a panel.
    /// Used by composite controls that manage their own visual tree.
    /// </summary>
    public void RegisterDwell()
    {
        Registrar.Register(DwellId, Category, RadiusPhysical, DwellMs, OnActivated);
    }

    /// <summary>Unregisters the dwell target without removing Root from a panel.</summary>
    public void UnregisterDwell()
    {
        Registrar.Unregister(DwellId, Category);
    }

    public void UpdatePosition()
    {
        try
        {
            var pos = Root.PointToScreen(
                new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
            Registrar.UpdatePosition(DwellId, Category, new PhysicalPoint(pos.X, pos.Y));
        }
        catch (InvalidOperationException)
        {
            // Not yet rendered or not in visual tree
        }
    }

    public void UpdateVisual()
    {
        double progress = Registrar.GetProgress(DwellId, Category);
        ApplyProgress(progress);
    }
}
```

Note: Properties are `public` (not `protected`) so composite controls can access them. `RegisterDwell()`/`UnregisterDwell()` are public for composites that manage their own visual tree layout.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Controls/ColorBlend.cs src/EyeCtrl.App/Controls/IDwellComposite.cs src/EyeCtrl.App/Controls/DwellControl.cs
git commit -m "feat: add DwellControl base class, IDwellComposite, ColorBlend utility"
```

---

## Task 4: DwellButton and DwellLabel

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellButton.cs`
- Create: `src/EyeCtrl.App/Controls/DwellLabel.cs`

- [ ] **Step 1: Create DwellButton.cs**

The atomic building block. Mirrors the existing `MakeControlButton` at `SettingsWindow.xaml.cs:963-989`.

```csharp
namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellButton : DwellControl
{
    private readonly Action _action;
    private readonly Color _baseColor;

    public DwellButton(
        IDwellRegistrar registrar,
        string id,
        string label,
        Action action,
        Color baseColor,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        double width = 100,
        double height = 80)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _action = action;
        _baseColor = baseColor;

        Root.Width = width;
        Root.Height = height;
        Root.Margin = new Thickness(4, 0, 4, 0);
        Root.CornerRadius = new CornerRadius(8);
        Root.Background = new SolidColorBrush(baseColor);
        Root.Cursor = Cursors.Hand;
        Root.Child = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Root.MouseLeftButtonDown += (_, _) => _action();
    }

    protected override Border CreateRoot() => new();

    protected override void OnActivated() => _action();

    protected override void ApplyProgress(double progress)
    {
        Root.Background = new SolidColorBrush(
            ColorBlend.Blend(_baseColor, Colors.White, progress * 0.5));
    }
}
```

- [ ] **Step 2: Create DwellLabel.cs**

```csharp
namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

public class DwellLabel
{
    public TextBlock Root { get; }

    public DwellLabel(string text)
    {
        Root = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontSize = 22,
            Margin = new Thickness(0, 8, 0, 2)
        };
    }

    public void Attach(Panel parent) => parent.Children.Add(Root);
    public void Detach(Panel parent) => parent.Children.Remove(Root);
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Controls/DwellButton.cs src/EyeCtrl.App/Controls/DwellLabel.cs
git commit -m "feat: add DwellButton and DwellLabel controls"
```

---

## Task 5: DwellToggle

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellToggle.cs`

- [ ] **Step 1: Create DwellToggle.cs**

Mirrors `SettingsWindow.xaml.cs:811-859` (the `AddToggle` method).

```csharp
namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellToggle : DwellControl
{
    private static readonly Color OnColor = Color.FromArgb(200, 30, 100, 30);
    private static readonly Color OffColor = Color.FromArgb(200, 80, 40, 40);

    private readonly string _label;
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private readonly TextBlock _text;

    public DwellToggle(
        IDwellRegistrar registrar,
        string id,
        string label,
        Func<bool> get,
        Action<bool> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
        : base(registrar, id, category, radiusPhysical, dwellMs)
    {
        _label = label;
        _get = get;
        _set = set;

        _text = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, $"Settings_Toggle_{id}");

        Root.Height = 80;
        Root.Margin = new Thickness(0, 4, 0, 12);
        Root.Padding = new Thickness(20, 0, 20, 0);
        Root.CornerRadius = new CornerRadius(8);
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.Cursor = Cursors.Hand;
        Root.Child = _text;

        Root.MouseLeftButtonDown += (_, _) => Toggle();
        RefreshVisual();
    }

    protected override Border CreateRoot() => new();

    protected override void OnActivated() => Toggle();

    protected override void ApplyProgress(double progress)
    {
        var baseColor = _get() ? OnColor : OffColor;
        Root.Background = new SolidColorBrush(
            ColorBlend.Blend(baseColor, Colors.White, progress * 0.5));
    }

    private void Toggle()
    {
        _set(!_get());
        RefreshVisual();
    }

    private void RefreshVisual()
    {
        var on = _get();
        _text.Text = (on ? "\u2611 " : "\u2610 ") + _label;
        Root.Background = new SolidColorBrush(on ? OnColor : OffColor);
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Controls/DwellToggle.cs
git commit -m "feat: add DwellToggle control"
```

---

## Task 6: DwellSlider

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellSlider.cs`

- [ ] **Step 1: Create DwellSlider.cs**

Mirrors `SettingsWindow.xaml.cs:687-722`. Composes two DwellButtons (minus/plus) with a value display. Implements `IDwellComposite`.

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellSlider : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);

    private readonly DwellLabel _label;
    private readonly DwellButton _minus;
    private readonly DwellButton _plus;
    private readonly TextBlock _valueText;
    private readonly StackPanel _row;

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min;
    private readonly double _max;
    private readonly double _step;
    private readonly int _decimals;

    public IEnumerable<DwellControl> Children => new[] { _minus, _plus };

    public DwellSlider(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        double min,
        double max,
        double step,
        int decimals,
        Func<double> get,
        Action<double> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _get = get;
        _set = set;
        _min = min;
        _max = max;
        _step = step;
        _decimals = decimals;

        _label = new DwellLabel(label);

        _valueText = new TextBlock
        {
            Text = FormatVal(get()),
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _minus = new DwellButton(registrar, idPrefix + "_dn", "\u2212",
            () => Adjust(-step), BtnBase, category, radiusPhysical, dwellMs);

        _plus = new DwellButton(registrar, idPrefix + "_up", "+",
            () => Adjust(step), BtnBase, category, radiusPhysical, dwellMs);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_valueText);
        _row.Children.Add(_plus.Root);
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_row);
        _minus.Attach(_row); // Register with dwell (Root already added to _row above)
        _plus.Attach(_row);
        // Remove the extra Root copies that Attach added to _row
        _row.Children.Remove(_minus.Root);
        _row.Children.Remove(_plus.Root);
        // Re-add in correct order
        _row.Children.Clear();
        _row.Children.Add(_minus.Root);
        _row.Children.Add(_valueText);
        _row.Children.Add(_plus.Root);
        // Register dwell targets without re-adding to panel
        Registrar_Register(_minus);
        Registrar_Register(_plus);
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_row);
        _minus.Detach(_row);
        _plus.Detach(_row);
    }

    private void Adjust(double delta)
    {
        var v = Math.Round(Math.Clamp(_get() + delta, _min, _max), _decimals);
        _set(v);
        _valueText.Text = FormatVal(v);
    }

    private string FormatVal(double v) =>
        _decimals == 0 ? ((int)v).ToString() : v.ToString($"F{_decimals}");

    // Helper to register dwell without adding Root to a panel
    private static void Registrar_Register(DwellControl control)
    {
        control.Registrar.Register(control.DwellId, control.Category,
            control.RadiusPhysical, control.DwellMs, () => { });
    }
}
```

Wait — the above Attach logic is tangled. The issue is that `DwellButton.Attach(panel)` both adds Root to the panel AND registers the dwell target, but here we want the Root already positioned inside `_row`. Let me redesign.

The cleaner approach: make DwellControl's registration separate from panel insertion. But changing the base class mid-plan is risky. Instead, DwellSlider should manually register the buttons' dwell targets without calling `Attach`.

**Revised DwellSlider.cs:**

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellSlider : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);

    private readonly DwellLabel _label;
    private readonly DwellButton _minus;
    private readonly DwellButton _plus;
    private readonly TextBlock _valueText;
    private readonly StackPanel _row;

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min;
    private readonly double _max;
    private readonly double _step;
    private readonly int _decimals;

    public IEnumerable<DwellControl> Children => new[] { _minus, _plus };

    public DwellSlider(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        double min,
        double max,
        double step,
        int decimals,
        Func<double> get,
        Action<double> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _get = get;
        _set = set;
        _min = min;
        _max = max;
        _step = step;
        _decimals = decimals;

        _label = new DwellLabel(label);

        _valueText = new TextBlock
        {
            Text = FormatVal(get()),
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _minus = new DwellButton(registrar, idPrefix + "_dn", "\u2212",
            () => Adjust(-_step), BtnBase, category, radiusPhysical, dwellMs);

        _plus = new DwellButton(registrar, idPrefix + "_up", "+",
            () => Adjust(_step), BtnBase, category, radiusPhysical, dwellMs);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_valueText);
        _row.Children.Add(_plus.Root);
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_row);
        // Register dwell targets (Roots are already in _row from constructor)
        _minus.RegisterDwell();
        _plus.RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_row);
        _minus.UnregisterDwell();
        _plus.UnregisterDwell();
    }

    private void Adjust(double delta)
    {
        var v = Math.Round(Math.Clamp(_get() + delta, _min, _max), _decimals);
        _set(v);
        _valueText.Text = FormatVal(v);
    }

    private string FormatVal(double v) =>
        _decimals == 0 ? ((int)v).ToString() : v.ToString($"F{_decimals}");
}
```

This requires adding `RegisterDwell()` and `UnregisterDwell()` to `DwellControl` base class. These are the dwell-only portion of Attach/Detach (without adding/removing Root from panel). Let me revise the base class.

**Revised `DwellControl.cs` (from Task 3) — add RegisterDwell/UnregisterDwell:**

The base class gets two additional public methods:

```csharp
    /// <summary>
    /// Registers the dwell target without adding Root to a panel.
    /// Used by composite controls that manage their own visual tree.
    /// </summary>
    public void RegisterDwell()
    {
        Registrar.Register(DwellId, Category, RadiusPhysical, DwellMs, OnActivated);
    }

    /// <summary>
    /// Unregisters the dwell target without removing Root from a panel.
    /// </summary>
    public void UnregisterDwell()
    {
        Registrar.Unregister(DwellId, Category);
    }
```

And `Attach`/`Detach` become:

```csharp
    public void Attach(Panel parent)
    {
        parent.Children.Add(Root);
        RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        parent.Children.Remove(Root);
        UnregisterDwell();
    }
```

This change applies retroactively to Task 3. The plan instructions for Task 3 Step 3 should use this version.

- [ ] **Step 1: Create DwellSlider.cs**

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellSlider : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);

    private readonly DwellLabel _label;
    private readonly DwellButton _minus;
    private readonly DwellButton _plus;
    private readonly TextBlock _valueText;
    private readonly StackPanel _row;

    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min;
    private readonly double _max;
    private readonly double _step;
    private readonly int _decimals;

    public IEnumerable<DwellControl> Children => [_minus, _plus];

    public DwellSlider(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        double min,
        double max,
        double step,
        int decimals,
        Func<double> get,
        Action<double> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _get = get;
        _set = set;
        _min = min;
        _max = max;
        _step = step;
        _decimals = decimals;

        _label = new DwellLabel(label);

        _valueText = new TextBlock
        {
            Text = FormatVal(get()),
            Foreground = Brushes.White,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _minus = new DwellButton(registrar, idPrefix + "_dn", "\u2212",
            () => Adjust(-_step), BtnBase, category, radiusPhysical, dwellMs);

        _plus = new DwellButton(registrar, idPrefix + "_up", "+",
            () => Adjust(_step), BtnBase, category, radiusPhysical, dwellMs);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        _row.Children.Add(_minus.Root);
        _row.Children.Add(_valueText);
        _row.Children.Add(_plus.Root);
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_row);
        _minus.RegisterDwell();
        _plus.RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_row);
        _minus.UnregisterDwell();
        _plus.UnregisterDwell();
    }

    private void Adjust(double delta)
    {
        var v = Math.Round(Math.Clamp(_get() + delta, _min, _max), _decimals);
        _set(v);
        _valueText.Text = FormatVal(v);
    }

    private string FormatVal(double v) =>
        _decimals == 0 ? ((int)v).ToString() : v.ToString($"F{_decimals}");
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Controls/DwellSlider.cs
git commit -m "feat: add DwellSlider composite control"
```

---

## Task 7: DwellComboBox

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellComboBox.cs`

- [ ] **Step 1: Create DwellComboBox.cs**

Mirrors `SettingsWindow.xaml.cs:728-809` (the `AddHorizontalList` method). Each option is a DwellButton with radio indicators.

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellComboBox : IDwellComposite
{
    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color SelectedColor = Color.FromArgb(200, 30, 100, 30);

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _options = new();
    private readonly List<TextBlock> _optionTexts = new();
    private readonly WrapPanel _panel;

    private readonly string[] _displayOptions;
    private readonly string[] _codeOptions;
    private readonly Func<string> _getCode;
    private readonly Action<string> _setCode;

    public IEnumerable<DwellControl> Children => _options;

    public DwellComboBox(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        string[] displayOptions,
        string[] codeOptions,
        Func<string> getCode,
        Action<string> setCode,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        _displayOptions = displayOptions;
        _codeOptions = codeOptions;
        _getCode = getCode;
        _setCode = setCode;

        _label = new DwellLabel(label);

        _panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        for (int i = 0; i < displayOptions.Length; i++)
        {
            var capturedCode = codeOptions[i];
            var id = $"{idPrefix}_{i}";

            var text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            System.Windows.Automation.AutomationProperties.SetAutomationId(
                text, $"Settings_HList_{id}");

            var btn = new DwellButton(registrar, id, "",
                () => { _setCode(capturedCode); Refresh(); },
                BtnBase, category, radiusPhysical, dwellMs,
                width: 0, height: 70);

            // Override the button's Root to use custom styling
            btn.Root.MinWidth = 150;
            btn.Root.Width = double.NaN; // Auto width
            btn.Root.Margin = new Thickness(0, 0, 8, 8);
            btn.Root.Padding = new Thickness(20, 0, 20, 0);
            btn.Root.Child = text;

            _options.Add(btn);
            _optionTexts.Add(text);
            _panel.Children.Add(btn.Root);
        }

        Refresh();
    }

    /// <summary>Convenience overload: same display and code values.</summary>
    public DwellComboBox(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        string[] options,
        Func<string> get,
        Action<string> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
        : this(registrar, idPrefix, label, options, options, get, set, category, radiusPhysical, dwellMs)
    {
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_panel);
        foreach (var btn in _options)
            btn.RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_panel);
        foreach (var btn in _options)
            btn.UnregisterDwell();
    }

    private void Refresh()
    {
        var active = _getCode();
        for (int j = 0; j < _options.Count; j++)
        {
            bool selected = j < _codeOptions.Length && _codeOptions[j] == active;
            _options[j].Root.Background = new SolidColorBrush(selected ? SelectedColor : BtnBase);
            if (j < _optionTexts.Count)
                _optionTexts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + _displayOptions[j];
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Controls/DwellComboBox.cs
git commit -m "feat: add DwellComboBox composite control"
```

---

## Task 8: DwellColorPicker

**Files:**
- Create: `src/EyeCtrl.App/Controls/DwellColorPicker.cs`

- [ ] **Step 1: Create DwellColorPicker.cs**

Mirrors `SettingsWindow.xaml.cs:868-918`.

```csharp
namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;

public class DwellColorPicker : IDwellComposite
{
    public static readonly (string Hex, string Name)[] DefaultPresets =
    [
        ("#00FF00", "Green"), ("#0088FF", "Blue"), ("#FF0000", "Red"),
        ("#FFFF00", "Yellow"), ("#00FFFF", "Cyan"), ("#FF00FF", "Magenta"),
        ("#FFFFFF", "White"), ("#FF8800", "Orange")
    ];

    private readonly DwellLabel _label;
    private readonly List<DwellButton> _swatches = new();
    private readonly List<Border> _swatchBorders = new();
    private readonly List<string> _hexValues = new();
    private readonly WrapPanel _panel;

    private readonly Func<string> _get;
    private readonly Action<string> _set;

    public IEnumerable<DwellControl> Children => _swatches;

    public DwellColorPicker(
        IDwellRegistrar registrar,
        string idPrefix,
        string label,
        Func<string> get,
        Action<string> set,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs,
        (string Hex, string Name)[]? presets = null)
    {
        _get = get;
        _set = set;
        presets ??= DefaultPresets;

        _label = new DwellLabel(label);

        _panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };

        for (int i = 0; i < presets.Length; i++)
        {
            var (hex, name) = presets[i];
            var swatchId = $"{idPrefix}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var capturedHex = hex;

            var btn = new DwellButton(registrar, swatchId, "",
                () => { _set(capturedHex); RefreshSelection(); },
                color, category, radiusPhysical, dwellMs,
                width: 90, height: 75);

            btn.Root.Margin = new Thickness(4);
            btn.Root.CornerRadius = new CornerRadius(6);
            btn.Root.Background = new SolidColorBrush(color);
            btn.Root.BorderThickness = new Thickness(3);
            btn.Root.BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : Brushes.Transparent;
            btn.Root.ToolTip = name;
            btn.Root.Child = null; // No text label for color swatches

            _swatches.Add(btn);
            _swatchBorders.Add(btn.Root);
            _hexValues.Add(hex);
            _panel.Children.Add(btn.Root);
        }
    }

    public void Attach(Panel parent)
    {
        _label.Attach(parent);
        parent.Children.Add(_panel);
        foreach (var btn in _swatches)
            btn.RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        _label.Detach(parent);
        parent.Children.Remove(_panel);
        foreach (var btn in _swatches)
            btn.UnregisterDwell();
    }

    private void RefreshSelection()
    {
        var selected = _get();
        for (int i = 0; i < _swatchBorders.Count; i++)
        {
            _swatchBorders[i].BorderBrush = _hexValues[i].Equals(selected, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White : Brushes.Transparent;
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Controls/DwellColorPicker.cs
git commit -m "feat: add DwellColorPicker composite control"
```

---

## Task 9: Migrate SettingsWindow — Zoom Tab (Proof of Concept)

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:1-1192`

This task adds the new infrastructure (`Add`, `ClearContent`, `UpdateAllPositionsAndVisuals`) and migrates the Zoom tab as a proof of concept. All other tabs continue using the old builder methods.

- [ ] **Step 1: Add using directive and new fields**

At the top of `SettingsWindow.xaml.cs`, add:

```csharp
using EyeCtrl.App.Controls;
```

After the existing `_pages` field (line 59), add:

```csharp
    private readonly List<object> _activeItems = new(); // DwellControl | IDwellComposite
```

- [ ] **Step 2: Add the Add() helper method**

After the `FormatVal` method (line 992), add:

```csharp
    private void Add(DwellControl control)
    {
        control.Attach(ContentPanel);
        _activeItems.Add(control);
    }

    private void Add(IDwellComposite composite)
    {
        composite.Attach(ContentPanel);
        _activeItems.Add(composite);
    }

    private void Add(DwellLabel label)
    {
        label.Attach(ContentPanel);
    }
```

- [ ] **Step 3: Update RebuildContent to clear new controls**

In `RebuildContent()` (line 257), add clearing of `_activeItems` after the existing clear:

```csharp
    private void RebuildContent()
    {
        _viewModel.ClearControlTargets();
        _controlBorders.Clear();
        ContentPanel.Children.Clear();

        // Detach new-style controls (they self-unregister)
        foreach (var item in _activeItems)
        {
            if (item is DwellControl dc) dc.Detach(ContentPanel);
            if (item is IDwellComposite comp) comp.Detach(ContentPanel);
        }
        _activeItems.Clear();

        _pages = BuildPages();
        _totalPages = _pages.Count;
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _totalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }
```

Also update `RenderCurrentPage()` to clear `_activeItems`:

```csharp
    private void RenderCurrentPage()
    {
        _viewModel.ClearControlTargets();
        _controlBorders.Clear();
        ContentPanel.Children.Clear();

        // Clear new-style controls
        _activeItems.Clear();

        if (_currentPage < _pages.Count)
        {
            foreach (var builder in _pages[_currentPage])
                builder();
        }
    }
```

- [ ] **Step 4: Add position/visual update loop for new controls**

In `UpdateAllTargetPositions()` (line 1003), add at the end (before the closing `}`):

```csharp
        // Update new-style dwell controls
        foreach (var item in _activeItems)
        {
            if (item is DwellControl single)
            {
                single.UpdatePosition();
                single.UpdateVisual();
            }
            if (item is IDwellComposite comp)
            {
                foreach (var child in comp.Children)
                {
                    child.UpdatePosition();
                    child.UpdateVisual();
                }
            }
        }
```

- [ ] **Step 5: Migrate BuildZoomPages to use new controls**

Replace `BuildZoomPages()` (lines 463-498) with:

```csharp
    private List<List<Action>> BuildZoomPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "z_en", "Enable Zoom Mode",
                    () => _settings.Zoom.Enabled,
                    v => { _settings.Zoom.Enabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "z_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.Zoom.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.Zoom.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_rad", "Circle Radius (px)",
                    50, 300, 10, 0,
                    () => _settings.Zoom.CircleRadiusPixels,
                    v => { _settings.Zoom.CircleRadiusPixels = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_mag", "Magnification Level",
                    1.5, 5, 0.5, 1,
                    () => _settings.Zoom.MagnificationLevel,
                    v => { _settings.Zoom.MagnificationLevel = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "z_grace", "Grace Period (ms)",
                    0, 2000, 50, 0,
                    () => _settings.Zoom.ZoomGracePeriodMs,
                    v => { _settings.Zoom.ZoomGracePeriodMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "z_reset", "Reset Zoom Settings",
                    () => ShowModal("Reset Zoom settings to defaults?", () =>
                    {
                        var z = new ZoomSettings();
                        _settings.Zoom.Enabled = z.Enabled;
                        _settings.Zoom.Position = z.Position;
                        _settings.Zoom.CircleRadiusPixels = z.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = z.MagnificationLevel;
                        _settings.Zoom.ZoomGracePeriodMs = z.ZoomGracePeriodMs;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test --no-build -v normal`
Expected: Build succeeded, all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: migrate Zoom tab to new dwell controls (proof of concept)

Adds Add/ClearContent/UpdateLoop infrastructure and migrates
BuildZoomPages to use DwellToggle, DwellComboBox, DwellSlider,
DwellButton. Other tabs unchanged."
```

---

## Task 10: Migrate All Remaining Tabs and Delete Old Methods

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

After verifying the Zoom tab works correctly (manual test), migrate all remaining page builders and delete the old builder methods.

- [ ] **Step 1: Migrate BuildCursorPages (lines 362-407)**

Replace with new control instantiation following the same pattern as Zoom. Use `var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);` and `var cat = DwellCategory.Control;` at start of each method.

```csharp
    private List<List<Action>> BuildCursorPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "c_filter", "Filter Mode",
                    Enum.GetNames<GazeFilterMode>(),
                    () => _settings.Cursor.FilterMode.ToString(),
                    v => { _settings.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v); ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_smooth", "Smoothing Factor",
                    0, 1, 0.1, 1,
                    () => _settings.Cursor.SmoothingFactor,
                    v => { _settings.Cursor.SmoothingFactor = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_snap", "Snap Radius (px)",
                    10, 100, 5, 0,
                    () => _settings.Cursor.SnapRadiusPixels,
                    v => { _settings.Cursor.SnapRadiusPixels = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellColorPicker(_viewModel, "c_color", "Circle Color",
                    () => _settings.Cursor.CircleColor,
                    v => { _settings.Cursor.CircleColor = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
            ],
            [
                () => Add(new DwellSlider(_viewModel, "c_opacity", "Circle Opacity",
                    0.1, 1, 0.1, 1,
                    () => _settings.Cursor.CircleOpacity,
                    v => { _settings.Cursor.CircleOpacity = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "c_thick", "Circle Thickness",
                    1, 6, 0.5, 1,
                    () => _settings.Cursor.CircleThickness,
                    v => { _settings.Cursor.CircleThickness = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "c_hc", "High Contrast Mode",
                    () => _settings.Cursor.CircleHighContrast,
                    v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "c_reset", "Reset Cursor Settings",
                    () => ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var defaults = new CursorSettings();
                        _settings.Cursor.FilterMode = defaults.FilterMode;
                        _settings.Cursor.SmoothingFactor = defaults.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = defaults.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = defaults.CircleColor;
                        _settings.Cursor.CircleOpacity = defaults.CircleOpacity;
                        _settings.Cursor.CircleThickness = defaults.CircleThickness;
                        _settings.Cursor.CircleHighContrast = defaults.CircleHighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 2: Migrate BuildDwellPages (lines 409-461)**

```csharp
    private List<List<Action>> BuildDwellPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellSlider(_viewModel, "d_fix", "Fixation Detection (ms)",
                    50, 500, 25, 0,
                    () => _settings.Dwell.FixationDetectionMs,
                    v => { _settings.Dwell.FixationDetectionMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_act", "Activation Button (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ActivationButtonDwellMs,
                    v => { _settings.Dwell.ActivationButtonDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_menu", "Action Menu (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ActionMenuDwellMs,
                    v => { _settings.Dwell.ActionMenuDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_zoom", "Zoom Action Menu (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ZoomActionMenuDwellMs,
                    v => { _settings.Dwell.ZoomActionMenuDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
            ],
            [
                () => Add(new DwellSlider(_viewModel, "d_tool", "Toolbar / Settings (ms)",
                    200, 1500, 50, 0,
                    () => _settings.Dwell.ToolbarButtonDwellMs,
                    v => { _settings.Dwell.ToolbarButtonDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_key", "Keyboard Key (ms)",
                    100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardKeyDwellMs,
                    v => { _settings.Dwell.KeyboardKeyDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_pred", "Prediction (ms)",
                    100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardPredictionDwellMs,
                    v => { _settings.Dwell.KeyboardPredictionDwellMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_rad", "Fixation Radius (px)",
                    20, 120, 5, 0,
                    () => _settings.Dwell.FixationRadiusPixels,
                    v => { _settings.Dwell.FixationRadiusPixels = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "d_cooldown", "Dwell Cooldown (ms)",
                    0, 1000, 50, 0,
                    () => _settings.Dwell.DwellCooldownMs,
                    v => { _settings.Dwell.DwellCooldownMs = (int)v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "d_reset", "Reset Dwell Settings",
                    () => ShowModal("Reset Dwell settings to defaults?", () =>
                    {
                        var d = new DwellSettings();
                        _settings.Dwell.FixationDetectionMs = d.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.FixationRadiusPixels;
                        _settings.Dwell.DwellCooldownMs = d.DwellCooldownMs;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 3: Migrate BuildActionMenuPages (lines 500-519)**

```csharp
    private List<List<Action>> BuildActionMenuPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "am_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ActionMenu.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ActionMenu.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "am_reset", "Reset Action Menu Settings",
                    () => ShowModal("Reset Action Menu settings to defaults?", () =>
                    {
                        var a = new ActionMenuSettings();
                        _settings.ActionMenu.Position = a.Position;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 4: Migrate BuildKeyboardPages (lines 521-563)**

```csharp
    private List<List<Action>> BuildKeyboardPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "k_layout", "Active Layout",
                    ["QWERTY", "QWERTZ", "AZERTY"],
                    () => _settings.Keyboard.ActiveLayout,
                    v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "k_size", "Key Size Multiplier",
                    0.5, 2.0, 0.1, 1,
                    () => _settings.Keyboard.KeySizeMultiplier,
                    v => { _settings.Keyboard.KeySizeMultiplier = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "k_pred", "Word Prediction",
                    () => _settings.Keyboard.PredictionEnabled,
                    v => { _settings.Keyboard.PredictionEnabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "k_lang", "Dictionary Language",
                    displayOptions: ["English", "French", "Spanish", "German", "Polish"],
                    codeOptions:    ["en",      "fr",     "es",      "de",     "pl"],
                    getCode: () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                                   : _settings.Keyboard.LanguagePack,
                    setCode: v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "k_sound", "Click Sound",
                    () => _settings.Keyboard.ClickSoundEnabled,
                    v => { _settings.Keyboard.ClickSoundEnabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "k_edge", "Screen Edge",
                    ["Bottom", "Top"],
                    () => _settings.Keyboard.ScreenEdge,
                    v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "k_reset", "Reset Keyboard Settings",
                    () => ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var k = new KeyboardSettings();
                        _settings.Keyboard.ActiveLayout = k.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = k.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = k.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = "";
                        _settings.Keyboard.ScreenEdge = k.ScreenEdge;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 5: Migrate BuildToolbarPages (lines 565-592)**

```csharp
    private List<List<Action>> BuildToolbarPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "t_dock", "Dock to Screen Edge",
                    () => _settings.Toolbar.IsDocked,
                    v => { _settings.Toolbar.IsDocked = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellComboBox(_viewModel, "t_edge", "Docked Edge",
                    ["Top", "Bottom", "Left", "Right"],
                    () => _settings.Toolbar.DockedEdge,
                    v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "t_size", "Button Size",
                    32, 120, 4, 0,
                    () => _settings.Toolbar.Size,
                    v => { _settings.Toolbar.Size = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "t_reset", "Reset Toolbar Settings",
                    () => ShowModal("Reset Toolbar settings to defaults?", () =>
                    {
                        var t = new ToolbarSettings();
                        _settings.Toolbar.IsDocked = t.IsDocked;
                        _settings.Toolbar.DockedEdge = t.DockedEdge;
                        _settings.Toolbar.Size = t.Size;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 6: Migrate BuildThemePages (lines 594-621)**

```csharp
    private List<List<Action>> BuildThemePages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "th_mode", "Theme Mode",
                    ["System", "Light", "Dark"],
                    () => _settings.Theme.Mode,
                    v => { _settings.Theme.Mode = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellSlider(_viewModel, "th_op", "Overlay Opacity",
                    0.3, 1.0, 0.1, 1,
                    () => _settings.Theme.OverlayOpacity,
                    v => { _settings.Theme.OverlayOpacity = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "th_hc", "High Contrast",
                    () => _settings.Theme.HighContrast,
                    v => { _settings.Theme.HighContrast = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "th_reset", "Reset Theme Settings",
                    () => ShowModal("Reset Theme settings to defaults?", () =>
                    {
                        var th = new ThemeSettings();
                        _settings.Theme.Mode = th.Mode;
                        _settings.Theme.OverlayOpacity = th.OverlayOpacity;
                        _settings.Theme.HighContrast = th.HighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 7: Migrate BuildGeneralPages (lines 623-683)**

```csharp
    private List<List<Action>> BuildGeneralPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellToggle(_viewModel, "g_boot", "Start on Windows Boot",
                    () => _settings.General.StartOnBoot,
                    v => { _settings.General.StartOnBoot = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellToggle(_viewModel, "g_pause", "Start Paused",
                    () => _settings.General.StartPaused,
                    v => { _settings.General.StartPaused = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "g_reset", "Reset General Settings",
                    () => ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new GeneralSettings();
                        _settings.General.StartOnBoot = g.StartOnBoot;
                        _settings.General.StartPaused = g.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
                () => Add(new DwellButton(_viewModel, "g_resetall", "\u26A0 Reset ALL Settings",
                    () => ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.Cursor.FilterMode = d.Cursor.FilterMode;
                        _settings.Cursor.SmoothingFactor = d.Cursor.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = d.Cursor.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = d.Cursor.CircleColor;
                        _settings.Cursor.CircleOpacity = d.Cursor.CircleOpacity;
                        _settings.Cursor.CircleThickness = d.Cursor.CircleThickness;
                        _settings.Cursor.CircleHighContrast = d.Cursor.CircleHighContrast;
                        _settings.Dwell.FixationDetectionMs = d.Dwell.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.Dwell.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.Dwell.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.Dwell.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.Dwell.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.Dwell.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.Dwell.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.Dwell.FixationRadiusPixels;
                        _settings.Zoom.Enabled = d.Zoom.Enabled;
                        _settings.Zoom.CircleRadiusPixels = d.Zoom.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = d.Zoom.MagnificationLevel;
                        _settings.Keyboard.ActiveLayout = d.Keyboard.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = d.Keyboard.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = d.Keyboard.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = "";
                        _settings.Keyboard.ScreenEdge = d.Keyboard.ScreenEdge;
                        _settings.Toolbar.IsDocked = d.Toolbar.IsDocked;
                        _settings.Toolbar.DockedEdge = d.Toolbar.DockedEdge;
                        _settings.Toolbar.Size = d.Toolbar.Size;
                        _settings.Theme.Mode = d.Theme.Mode;
                        _settings.Theme.OverlayOpacity = d.Theme.OverlayOpacity;
                        _settings.Theme.HighContrast = d.Theme.HighContrast;
                        _settings.General.StartOnBoot = d.General.StartOnBoot;
                        _settings.General.StartPaused = d.General.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 8: Delete old builder methods**

Delete the following methods from SettingsWindow.xaml.cs:

- `AddSlider` (lines 687-722)
- `AddHorizontalList` overload 1 (lines 724-726)
- `AddHorizontalList` overload 2 (lines 728-809)
- `AddToggle` (lines 811-859)
- `ColorPresets` array (lines 861-866)
- `AddColorPicker` (lines 868-918)
- `AddResetButton` (lines 920-948)
- `AddLabel` (lines 952-961)
- `MakeControlButton` (lines 963-989)
- `FormatVal` (lines 991-992)
- `UpdateControlVisuals` (lines 1072-1081) — replaced by `DwellControl.UpdateVisual`

Also remove the `_controlBorders` field (line 52) — controls now self-manage.

Remove the `ControlProgress` case from `OnViewModelPropertyChanged` (line 133-134) — control visual updates are now handled by the new-style update loop.

**Keep** the following — tabs, pagination, and modal are NOT converted to new-style controls:
- `UpdateTabVisuals`, `UpdateActiveTabHighlight` — tab bar visual feedback
- `UpdatePaginationVisuals` — pagination button feedback
- `UpdateModalVisuals`, `UpdateModalVisibility`, `MakeModalButton` — modal overlay
- `MakePagButton` — pagination button creation
- `UpdateButtonPositions` — used by tabs, pagination, modal
- `Blend` — used by the above methods
- `_tabButtons`, `_pagButtons`, `_modalButtons` dictionaries

- [ ] **Step 9: Build and run all tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test --no-build -v normal`
Expected: Build succeeded, all tests pass

- [ ] **Step 10: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: migrate all settings tabs to shared dwell controls

Replaces AddSlider/AddToggle/AddColorPicker/AddHorizontalList/
AddResetButton/MakeControlButton with DwellSlider/DwellToggle/
DwellColorPicker/DwellComboBox/DwellButton from Controls library.
Deletes ~380 lines of builder and visual update methods."
```

---

## Task 11: Remove Legacy ViewModel Methods

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (update any remaining callers)
- Modify: `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` (remove old API tests)

After all tabs are migrated, the legacy wrapper methods on SettingsWindowViewModel can be removed.

- [ ] **Step 1: Check for remaining callers of legacy methods**

Search `SettingsWindow.xaml.cs` for:
- `AddTabTarget` — used in `BuildTabButtons` (line 232, 236)
- `UpdateTabButtonPosition` — used in `UpdateAllTargetPositions` (line 1010, 1018)
- `AddPaginationTarget` — used in `MakePagButton` (line 348)
- `UpdatePaginationButtonPosition` — used in `UpdateAllTargetPositions` (line 1024)
- `AddModalTarget` — used in `UpdateModalVisibility` (line 1129-1130)
- `UpdateModalButtonPosition` — used in `UpdateAllTargetPositions` (line 1029)
- `ClearControlTargets` — used in `RebuildContent` and `RenderCurrentPage`
- `SimulateModalButtonActivated` — used in `MakeModalButton` (line 1184)

Update each call site to use the IDwellRegistrar methods directly:
- `_viewModel.AddTabTarget(id, radius, dwellMs)` → `_viewModel.Register(id, DwellCategory.Tab, radius, dwellMs, () => { })`
- `_viewModel.UpdateTabButtonPosition(id, pos)` → `_viewModel.UpdatePosition(id, DwellCategory.Tab, pos)`
- `_viewModel.AddPaginationTarget(id, radius, dwellMs)` → `_viewModel.Register(id, DwellCategory.Pagination, radius, dwellMs, () => { })`
- `_viewModel.UpdatePaginationButtonPosition(id, pos)` → `_viewModel.UpdatePosition(id, DwellCategory.Pagination, pos)`
- `_viewModel.AddModalTarget(id, radius, dwellMs)` → `_viewModel.Register(id, DwellCategory.Modal, radius, dwellMs, () => { })`
- `_viewModel.UpdateModalButtonPosition(id, pos)` → `_viewModel.UpdatePosition(id, DwellCategory.Modal, pos)`
- `_viewModel.ClearControlTargets()` → `_viewModel.ClearCategory(DwellCategory.Control)`
- `_viewModel.SimulateModalButtonActivated(id)` → `_viewModel.SimulateActivated(id, DwellCategory.Modal)`

- [ ] **Step 2: Update test file to use new API only**

In `SettingsWindowViewModelTests.cs`, update all old-API tests to use the new methods:
- Replace `vm.AddTabTarget(...)` with `vm.Register(..., DwellCategory.Tab, ...)`
- Replace `vm.UpdateTabButtonPosition(...)` with `vm.UpdatePosition(..., DwellCategory.Tab, ...)`
- Replace `vm.AddPaginationTarget(...)` with `vm.Register(..., DwellCategory.Pagination, ...)`
- Replace `vm.UpdatePaginationButtonPosition(...)` with `vm.UpdatePosition(..., DwellCategory.Pagination, ...)`
- Replace `vm.RegisterControlAction(...)` + `vm.AddControlTarget(...)` with single `vm.Register(..., DwellCategory.Control, ...)`
- Replace `vm.UpdateControlPosition(...)` with `vm.UpdatePosition(..., DwellCategory.Control, ...)`
- Replace `vm.SimulateControlActivated(...)` with `vm.SimulateActivated(..., DwellCategory.Control)`
- Replace `vm.SimulateModalButtonActivated(...)` with `vm.SimulateActivated(..., DwellCategory.Modal)`
- Replace `vm.AddModalTarget(...)` with `vm.Register(..., DwellCategory.Modal, ...)`
- Replace `vm.UpdateModalButtonPosition(...)` with `vm.UpdatePosition(..., DwellCategory.Modal, ...)`
- Replace `vm.ClearControlTargets()` with `vm.ClearCategory(DwellCategory.Control)`

- [ ] **Step 3: Delete legacy methods from SettingsWindowViewModel**

Remove the entire "Legacy API" section from the ViewModel:
- `AddTabTarget`
- `UpdateTabButtonPosition`
- `AddPaginationTarget`
- `UpdatePaginationButtonPosition`
- `RegisterControlAction`
- `AddControlTarget`
- `UpdateControlPosition`
- `SimulateControlActivated`
- `ClearControlTargets`
- `AddModalTarget`
- `UpdateModalButtonPosition`
- `SimulateModalButtonActivated`

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test --no-build -v normal`
Expected: Build succeeded, all tests pass

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsWindowViewModel.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs
git commit -m "refactor: remove legacy dwell registration methods from ViewModel

All callers now use IDwellRegistrar API directly.
Removes 12 legacy wrapper methods."
```

---

## Verification Checklist

After all tasks are complete:

- [ ] `dotnet build EyeCtrl.slnx` — no errors, no warnings
- [ ] `dotnet test` — all tests pass
- [ ] Manual test: open Settings window, verify each tab's controls work (dwell activation, value changes, reset buttons, modal confirmations)
- [ ] Manual test: verify pagination still works on Cursor and Dwell tabs (2 pages each)
- [ ] Manual test: verify close button works
- [ ] Verify SettingsWindow.xaml.cs is ~900 lines or less (tabs/pagination/modal visual methods kept)
- [ ] Verify no old builder methods remain (`AddSlider`, `AddToggle`, `AddColorPicker`, `AddHorizontalList`, `AddResetButton`, `MakeControlButton`)
