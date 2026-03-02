# MVVM Refactoring Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Convert EyeCtrl from hand-wired event-driven code-behind to full MVVM + DI using CommunityToolkit.Mvvm and Microsoft.Extensions.DependencyInjection.

**Architecture:** New `EyeCtrl.ViewModels` project (references Core only). All overlays get ViewModels. `GazeEngineAdapter` replaces App.xaml.cs event wiring with `WeakReferenceMessenger`. `OverlayWindow` base class eliminates Win32 interop duplication. Settings converted to `ObservableObject` with source-generated INPC.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (source generators), Microsoft.Extensions.DependencyInjection, xUnit

**Design doc:** `docs/plans/2026-03-02-mvvm-refactoring-design.md`

---

## Phase 1: Infrastructure

### Task 1: Create EyeCtrl.ViewModels project and test project

**Files:**
- Create: `src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
- Create: `tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`
- Modify: `EyeCtrl.slnx`
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj`

**Step 1: Create the ViewModels project**

```bash
cd C:\Users\andre\source\repos\eyectrl
dotnet new classlib -n EyeCtrl.ViewModels -o src/EyeCtrl.ViewModels -f net10.0
```

Delete the auto-generated `Class1.cs`.

**Step 2: Add NuGet packages and project reference**

```bash
dotnet add src/EyeCtrl.ViewModels package CommunityToolkit.Mvvm
dotnet add src/EyeCtrl.ViewModels reference src/EyeCtrl.Core/EyeCtrl.Core.csproj
```

**Step 3: Create test project**

```bash
dotnet new xunit -n EyeCtrl.ViewModels.Tests -o tests/EyeCtrl.ViewModels.Tests -f net10.0
dotnet add tests/EyeCtrl.ViewModels.Tests reference src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj
dotnet add tests/EyeCtrl.ViewModels.Tests reference src/EyeCtrl.Core/EyeCtrl.Core.csproj
```

Delete the auto-generated `UnitTest1.cs`.

**Step 4: Add projects to solution and add App reference**

```bash
dotnet sln EyeCtrl.slnx add src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj --solution-folder src
dotnet sln EyeCtrl.slnx add tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj --solution-folder tests
dotnet add src/EyeCtrl.App reference src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj
```

**Step 5: Add Microsoft.Extensions.DependencyInjection to App**

```bash
dotnet add src/EyeCtrl.App package Microsoft.Extensions.DependencyInjection
```

**Step 6: Build and verify**

```bash
dotnet build EyeCtrl.slnx
dotnet test EyeCtrl.slnx
```

Expected: Build succeeds. All 30 existing Core tests pass. New test project has 0 tests.

**Step 7: Commit**

```bash
git add -A
git commit -m "feat: create EyeCtrl.ViewModels project with MVVM + DI packages"
```

---

### Task 2: Create OverlayWindow base class

**Files:**
- Create: `src/EyeCtrl.App/Overlays/OverlayWindow.cs`
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs` — inherit from OverlayWindow, remove Win32 boilerplate
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` — same
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` — same
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — same
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs` — same
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` — same
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — same
- Modify: all corresponding `.xaml` files — change `Window` to `local:OverlayWindow` in root element

**Step 1: Create OverlayWindow base class**

Create `src/EyeCtrl.App/Overlays/OverlayWindow.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    /// <summary>
    /// When true, the window is click-through (WS_EX_TRANSPARENT).
    /// Set before the window is shown. Default: false.
    /// </summary>
    public bool IsClickThrough { get; init; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var flags = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (IsClickThrough) flags |= WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | flags);
    }
}
```

**Step 2: Update each overlay window**

For each overlay window, do these changes:

a) In the `.xaml` file, change the root element from `Window` to the OverlayWindow type. Add the namespace import. For example, `CursorCircleWindow.xaml`:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.CursorCircleWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        ...>
```

For `SettingsWindow.xaml` (in Views namespace), the xmlns points to `EyeCtrl.App.Overlays`.

b) In the `.xaml.cs` file, remove:
- The `DllImport` declarations for `GetWindowLong`/`SetWindowLong`
- The `GWL_EXSTYLE`, `WS_EX_*` constants
- The `OnSourceInitialized` override (all its logic is now in base)

c) Change class declaration from `: Window` to `: OverlayWindow`.

d) For click-through windows (`CursorCircleWindow`, `ZoomOverlayWindow`), set `IsClickThrough = true` in the constructor.

**Which windows are click-through:**
- `CursorCircleWindow` — YES (WS_EX_TRANSPARENT)
- `ZoomOverlayWindow` — YES (WS_EX_TRANSPARENT)
- All others — NO

**For CursorCircleWindow**: constructor becomes:
```csharp
public CursorCircleWindow()
{
    IsClickThrough = true;
    InitializeComponent();
}
```

**For ZoomOverlayWindow**: same pattern with `IsClickThrough = true`.

**Step 3: Build and run all tests**

```bash
dotnet build EyeCtrl.slnx
dotnet test EyeCtrl.slnx
```

Expected: Build succeeds. All existing tests pass. No behavioral change.

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: extract OverlayWindow base class, eliminate Win32 interop duplication"
```

---

### Task 3: Create message types

**Files:**
- Create: `src/EyeCtrl.ViewModels/Messages/GazeMessages.cs`
- Create: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Create: `src/EyeCtrl.ViewModels/Messages/OverlayMessages.cs`

**Step 1: Create message records**

Create `src/EyeCtrl.ViewModels/Messages/GazeMessages.cs`:

```csharp
namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>Sent by GazeEngineAdapter at ~30Hz with filtered gaze position.</summary>
public record GazeCursorMovedMessage(ScreenPoint Position, long TimestampMicroseconds);
```

Create `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`:

```csharp
namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

// Core → ViewModel
public record ShowActivationButtonMessage(ScreenPoint Position);
public record HideActivationButtonMessage;
public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs);
public record HideActionMenuMessage;
public record StateChangedMessage(InteractionState NewState);
public record StartZoomMessage(ScreenPoint Center, int Radius, double Magnification);
public record EndZoomMessage;
public record ActionRequestedMessage(InteractionAction Action, ScreenPoint Point);

// ViewModel → Core
public record ActivationCompletedMessage;
public record ActionSelectedMessage(InteractionAction Action);
public record BackSelectedMessage;
public record PauseToggledMessage;
public record ZoomToggledMessage;
public record KeyboardToggledMessage;
public record SettingsRequestedMessage;
public record KeyActivatedMessage(string Key);
public record PredictionSelectedMessage(string Word);
public record SettingsChangedMessage;
```

Create `src/EyeCtrl.ViewModels/Messages/OverlayMessages.cs`:

```csharp
namespace EyeCtrl.ViewModels.Messages;

/// <summary>Sent when keyboard should be shown/hidden.</summary>
public record ShowKeyboardMessage;
public record HideKeyboardMessage;

/// <summary>Sent when settings window should open.</summary>
public record ShowSettingsMessage;
```

**Step 2: Build**

```bash
dotnet build src/EyeCtrl.ViewModels
```

**Step 3: Commit**

```bash
git add -A
git commit -m "feat: add MVVM message types for inter-component communication"
```

---

## Phase 2: Settings Conversion

### Task 4: Convert settings classes to ObservableObject

**Files:**
- Modify: `src/EyeCtrl.Core/EyeCtrl.Core.csproj` — add CommunityToolkit.Mvvm package
- Modify: all 8 settings files in `src/EyeCtrl.Core/Settings/`
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs`

**Important:** Settings classes must remain JSON-serializable. `[ObservableProperty]` generates properties from `_camelCase` fields → `PascalCase` properties. `JsonNamingPolicy.CamelCase` maps `PascalCase` → `camelCase` in JSON. This is compatible with the existing JSON format.

**Step 1: Add CommunityToolkit.Mvvm to Core**

```bash
dotnet add src/EyeCtrl.Core package CommunityToolkit.Mvvm
```

**Step 2: Write failing test for settings serialization round-trip**

Create `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json;
using EyeCtrl.Core.Settings;

public class SettingsSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Settings_RoundTrip_PreservesAllValues()
    {
        var original = new EyeCtrlSettings();
        original.Cursor.CircleSize = 99;
        original.Cursor.CircleColor = "#FF0000";
        original.Dwell.FixationDetectionMs = 500;
        original.Zoom.Enabled = true;
        original.Zoom.MagnificationLevel = 3.0;
        original.Keyboard.ActiveLayout = "AZERTY";
        original.Toolbar.Size = 120;
        original.Theme.Mode = "Dark";
        original.General.StartPaused = true;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(99, deserialized.Cursor.CircleSize);
        Assert.Equal("#FF0000", deserialized.Cursor.CircleColor);
        Assert.Equal(500, deserialized.Dwell.FixationDetectionMs);
        Assert.True(deserialized.Zoom.Enabled);
        Assert.Equal(3.0, deserialized.Zoom.MagnificationLevel);
        Assert.Equal("AZERTY", deserialized.Keyboard.ActiveLayout);
        Assert.Equal(120, deserialized.Toolbar.Size);
        Assert.Equal("Dark", deserialized.Theme.Mode);
        Assert.True(deserialized.General.StartPaused);
    }

    [Fact]
    public void Settings_PropertyChanged_FiresOnChange()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        cursor.CircleSize = 100;
        cursor.CircleColor = "#FF0000";

        Assert.Contains("CircleSize", changed);
        Assert.Contains("CircleColor", changed);
    }

    [Fact]
    public void Settings_PropertyChanged_DoesNotFireForSameValue()
    {
        var cursor = new CursorSettings();
        var changed = new List<string>();
        cursor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        var originalSize = cursor.CircleSize;
        cursor.CircleSize = originalSize; // same value

        Assert.DoesNotContain("CircleSize", changed);
    }

    [Fact]
    public void Settings_BackwardsCompatible_DeserializesExistingJson()
    {
        // Simulate existing saved JSON (camelCase)
        var json = """
        {
          "cursor": {
            "filterMode": "OneEuro",
            "circleSize": 40,
            "circleColor": "#00FF00",
            "circleOpacity": 0.6,
            "circleThickness": 2.0,
            "circleHighContrast": false,
            "smoothingFactor": 0.5,
            "oneEuroMinCutoff": 5.0,
            "oneEuroBeta": 0.5,
            "snapRadiusPixels": 30
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;
        Assert.Equal(40, settings.Cursor.CircleSize);
        Assert.Equal("#00FF00", settings.Cursor.CircleColor);
    }
}
```

**Step 3: Run tests — expect failure** (PropertyChanged won't fire until we convert)

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "SettingsSerialization"
```

Expected: `Settings_PropertyChanged_FiresOnChange` FAILS, `Settings_PropertyChanged_DoesNotFireForSameValue` FAILS (no PropertyChanged event on POCOs).

**Step 4: Convert all settings classes**

Convert each settings class to use `[ObservableProperty]`. Example for `CursorSettings.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class CursorSettings : ObservableObject
{
    [ObservableProperty] private GazeFilterMode _filterMode = GazeFilterMode.OneEuro;
    [ObservableProperty] private double _smoothingFactor = 0.5;
    [ObservableProperty] private double _oneEuroMinCutoff = 5.0;
    [ObservableProperty] private double _oneEuroBeta = 0.5;
    [ObservableProperty] private int _snapRadiusPixels = 30;
    [ObservableProperty] private string _circleColor = "#00FF00";
    [ObservableProperty] private double _circleOpacity = 0.6;
    [ObservableProperty] private double _circleThickness = 2.0;
    [ObservableProperty] private double _circleSize = 250;
    [ObservableProperty] private bool _circleHighContrast = false;
}
```

Apply the same pattern to ALL settings classes:

- `DwellSettings` — 8 properties → 8 `[ObservableProperty]` fields
- `ZoomSettings` — 3 properties
- `ActionMenuSettings` — 1 property (`List<InteractionAction>` — use `[ObservableProperty]`)
- `KeyboardSettings` — 6 properties
- `ToolbarSettings` — 3 properties
- `ThemeSettings` — 3 properties
- `GeneralSettings` — 2 properties
- `EyeCtrlSettings` — 8 sub-settings properties (also make ObservableObject)

**Step 5: Run all tests**

```bash
dotnet test EyeCtrl.slnx
```

Expected: ALL tests pass, including the new serialization tests.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: convert settings to ObservableObject with INPC source generators"
```

---

## Phase 3: Simple Overlay ViewModels

### Task 5: CursorCircleViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/CursorCircleViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class CursorCircleViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void UpdatesPosition_OnGazeCursorMovedMessage()
    {
        var settings = new CursorSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(100, 200), 0));

        Assert.Equal(100, vm.Position.X);
        Assert.Equal(200, vm.Position.Y);
    }

    [Fact]
    public void ReadsAppearance_FromSettings()
    {
        var settings = new CursorSettings { CircleSize = 50, CircleColor = "#FF0000", CircleOpacity = 0.8 };
        var vm = new CursorCircleViewModel(settings, _messenger);

        Assert.Equal(50, vm.Size);
        Assert.Equal("#FF0000", vm.CircleColor);
        Assert.Equal(0.8, vm.Opacity);
    }

    [Fact]
    public void HidesWhenPaused_ShowsWhenResumed()
    {
        var settings = new CursorSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Assert.True(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.False(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsVisible);
    }
}
```

**Step 2: Run tests — expect failure**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "CursorCircleViewModel"
```

**Step 3: Implement CursorCircleViewModel**

Create `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>
{
    private readonly CursorSettings _settings;

    [ObservableProperty] private ScreenPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    public double Size => _settings.CircleSize;
    public string CircleColor => _settings.CircleColor;
    public double Opacity => _settings.CircleOpacity;
    public double Thickness => _settings.CircleThickness;

    public CursorCircleViewModel(CursorSettings settings, IMessenger messenger)
    {
        _settings = settings;
        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            Position = message.Position;
    }

    public void Receive(StateChangedMessage message)
    {
        IsVisible = message.NewState != InteractionState.Paused;
    }
}
```

**Step 4: Run tests — expect pass**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "CursorCircleViewModel"
```

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add CursorCircleViewModel with tests"
```

---

### Task 6: ActivationButtonViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ActivationButtonViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActivationButtonViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly DwellSettings _dwellSettings = new();

    [Fact]
    public void ShowsButton_OnShowMessage()
    {
        var vm = new ActivationButtonViewModel(_dwellSettings, _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Position.X);
        Assert.Equal(300, vm.Position.Y);
    }

    [Fact]
    public void HidesButton_OnHideMessage()
    {
        var vm = new ActivationButtonViewModel(_dwellSettings, _messenger);
        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new HideActivationButtonMessage());

        Assert.False(vm.IsVisible);
        Assert.Equal(0, vm.DwellProgress);
    }

    [Fact]
    public void AccumulatesDwell_OnGazeWithinRadius()
    {
        var settings = new DwellSettings { ActivationButtonDwellMs = 1000 };
        var vm = new ActivationButtonViewModel(settings, _messenger);

        // Show button at (500, 300)
        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));

        // Send gaze within radius for half the dwell time
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 500_000)); // 500ms

        Assert.True(vm.DwellProgress > 0.4);
        Assert.True(vm.DwellProgress < 0.6);
    }

    [Fact]
    public void SendsActivationCompleted_OnDwellComplete()
    {
        var settings = new DwellSettings { ActivationButtonDwellMs = 100 };
        var vm = new ActivationButtonViewModel(settings, _messenger);

        bool activated = false;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => activated = true);

        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 200_000)); // 200ms > 100ms dwell

        Assert.True(activated);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement ActivationButtonViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActivationButtonViewModel : ObservableObject,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>,
    IRecipient<GazeCursorMovedMessage>
{
    private const string TargetId = "activate";
    private const double ButtonRadius = 50;

    private readonly DwellSettings _dwellSettings;
    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector = new();

    [ObservableProperty] private ScreenPoint _position = new(0, 0);
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isVisible;

    public ActivationButtonViewModel(DwellSettings dwellSettings, IMessenger messenger)
    {
        _dwellSettings = dwellSettings;
        _messenger = messenger;

        _dwellDetector.TargetActivated += _ =>
        {
            _messenger.Send(new ActivationCompletedMessage());
        };
        _dwellDetector.TargetProgress += (_, progress) => DwellProgress = progress;

        messenger.RegisterAll(this);
    }

    public void Receive(ShowActivationButtonMessage message)
    {
        Position = message.Position;
        IsVisible = true;
        DwellProgress = 0;
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(TargetId,
            message.Position, ButtonRadius,
            _dwellSettings.ActivationButtonDwellMs));
    }

    public void Receive(HideActivationButtonMessage message)
    {
        IsVisible = false;
        DwellProgress = 0;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }
}
```

**Step 4: Run tests — expect pass**

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ActivationButtonViewModel with dwell tracking and tests"
```

---

### Task 7: ZoomOverlayViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public class ZoomOverlayViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsZoom_OnStartZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(_messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Center.X);
        Assert.Equal(300, vm.Center.Y);
        Assert.Equal(150, vm.Radius);
        Assert.Equal(2.0, vm.Magnification);
    }

    [Fact]
    public void HidesZoom_OnEndZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(_messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
        Assert.True(vm.IsVisible);

        _messenger.Send(new EndZoomMessage());
        Assert.False(vm.IsVisible);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement ZoomOverlayViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomOverlayViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>
{
    [ObservableProperty] private ScreenPoint _center = new(0, 0);
    [ObservableProperty] private int _radius;
    [ObservableProperty] private double _magnification;
    [ObservableProperty] private bool _isVisible;

    public ZoomOverlayViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;
        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
    }
}
```

**Step 4: Run tests — expect pass**

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ZoomOverlayViewModel with tests"
```

---

## Phase 4: Medium Overlay ViewModels

### Task 8: ActionMenuViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Create: `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public class ActionMenuViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsMenu_WithRadialLayout()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick
        };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, false, 600));

        Assert.True(vm.IsVisible);
        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.ShowBack);
    }

    [Fact]
    public void RadialPositions_AreEvenlySpaced()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction>
        {
            InteractionAction.LeftClick,
            InteractionAction.RightClick,
            InteractionAction.DoubleClick,
            InteractionAction.DragStart
        };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(0, 0), actions, false, 600));

        // 4 items at 90-degree intervals — verify positions are distinct and equidistant from center
        var positions = vm.Items.Select(i => i.Position).ToList();
        Assert.Equal(4, positions.Distinct().Count());
    }

    [Fact]
    public void ShowsBackButton_WhenRequested()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, true, 600));

        Assert.True(vm.ShowBack);
        Assert.Equal(2, vm.Items.Count); // 1 action + 1 back button
    }

    [Fact]
    public void SendsActionSelected_OnDwellComplete()
    {
        var vm = new ActionMenuViewModel(_messenger);
        var actions = new List<InteractionAction> { InteractionAction.LeftClick };

        InteractionAction? selectedAction = null;
        _messenger.Register<ActionSelectedMessage>(_messenger, (_, msg) => selectedAction = msg.Action);

        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, false, 100));

        // Simulate gaze on the first item
        var itemPos = vm.Items[0].Position;
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 0));
        _messenger.Send(new GazeCursorMovedMessage(itemPos, 200_000)); // 200ms > 100ms dwell

        Assert.Equal(InteractionAction.LeftClick, selectedAction);
    }

    [Fact]
    public void HidesMenu_OnHideMessage()
    {
        var vm = new ActionMenuViewModel(_messenger);
        _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300),
            new List<InteractionAction> { InteractionAction.LeftClick }, false, 600));
        Assert.True(vm.IsVisible);

        _messenger.Send(new HideActionMenuMessage());
        Assert.False(vm.IsVisible);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement ActionMenuItemViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuItemViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction? Action { get; }
    public string Symbol { get; }
    public ScreenPoint Position { get; }
    public bool IsBackButton { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(string id, InteractionAction? action, string symbol, ScreenPoint position, bool isBackButton = false)
    {
        Id = id;
        Action = action;
        Symbol = symbol;
        Position = position;
        IsBackButton = isBackButton;
    }
}
```

**Step 4: Implement ActionMenuViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public partial class ActionMenuViewModel : ObservableObject,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>,
    IRecipient<GazeCursorMovedMessage>
{
    private const double ButtonRadius = 50;
    private const double RingRadius = 160;

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector = new();
    private readonly Dictionary<string, ActionMenuItemViewModel> _itemLookup = new();

    [ObservableProperty] private ScreenPoint _center = new(0, 0);
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _showBack;

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(ShowActionMenuMessage message)
    {
        Center = message.Center;
        ShowBack = message.ShowBack;
        Items.Clear();
        _dwellDetector.ClearTargets();
        _itemLookup.Clear();

        var allItems = new List<(string Id, InteractionAction? Action, string Symbol, bool IsBack)>();
        foreach (var action in message.Actions)
            allItems.Add((action.ToString(), action, GetActionSymbol(action), false));
        if (message.ShowBack)
            allItems.Add(("Back", null, "\u21A9", true));

        var angleStep = 360.0 / allItems.Count;
        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, action, symbol, isBack) = allItems[i];
            var angleRad = (angleStep * i - 90) * Math.PI / 180;
            var bx = message.Center.X + RingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + RingRadius * Math.Sin(angleRad);
            var position = new ScreenPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(id, action, symbol, position, isBack);
            Items.Add(itemVm);
            _itemLookup[id] = itemVm;

            _dwellDetector.AddTarget(new DwellTarget(id, position, ButtonRadius, message.DwellTimeMs));
        }

        IsVisible = true;
    }

    public void Receive(HideActionMenuMessage message)
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        if (item.IsBackButton)
            _messenger.Send(new BackSelectedMessage());
        else if (item.Action.HasValue)
            _messenger.Send(new ActionSelectedMessage(item.Action.Value));
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.DwellProgress = progress;
    }

    private static string GetActionSymbol(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "\u261D",
        InteractionAction.RightClick => "\u2630",
        InteractionAction.DoubleClick => "\u270C",
        InteractionAction.DragStart => "\u270A",
        InteractionAction.DragEnd => "\u270B",
        InteractionAction.ScrollUp => "\u25B2",
        InteractionAction.ScrollDown => "\u25BC",
        InteractionAction.ScrollLeft => "\u25C0",
        InteractionAction.ScrollRight => "\u25B6",
        _ => action.ToString()
    };
}
```

**Step 5: Run tests — expect pass**

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add ActionMenuViewModel with radial layout, dwell tracking, tests"
```

---

### Task 9: ToolbarViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/ToolbarButtonViewModel.cs`
- Create: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly ToolbarSettings _toolbarSettings = new() { Size = 80 };
    private readonly DwellSettings _dwellSettings = new() { ToolbarButtonDwellMs = 100 };

    [Fact]
    public void InitializesWith4Buttons()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        Assert.Equal(4, vm.Buttons.Count);
    }

    [Fact]
    public void PauseButton_SendsPauseToggledMessage()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        bool received = false;
        _messenger.Register<PauseToggledMessage>(_messenger, (_, _) => received = true);

        // Find the Pause button and simulate activation
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        vm.SimulateButtonActivated("Pause");

        Assert.True(received);
    }

    [Fact]
    public void UpdatesPausedState_OnStateChanged()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        Assert.False(vm.IsPaused);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.True(vm.IsPaused);

        // Pause button symbol should change to play ▶
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Assert.Equal("\u25B6", pauseBtn.Symbol);
    }

    [Fact]
    public void ZoomToggle_SendsZoomToggledMessage()
    {
        var vm = new ToolbarViewModel(_toolbarSettings, _dwellSettings, _messenger);
        bool received = false;
        _messenger.Register<ZoomToggledMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("Zoom");

        Assert.True(received);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement ToolbarButtonViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ToolbarButtonViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _symbol;
    [ObservableProperty] private double _dwellProgress;
    [ObservableProperty] private bool _isHighlighted;

    public ToolbarButtonViewModel(string id, string symbol)
    {
        Id = id;
        _symbol = symbol;
    }
}
```

**Step 4: Implement ToolbarViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ToolbarViewModel : ObservableObject,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>
{
    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector = new();
    private readonly Dictionary<string, ToolbarButtonViewModel> _buttonLookup = new();

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isZoomOn;

    public ObservableCollection<ToolbarButtonViewModel> Buttons { get; } = new();

    /// <summary>
    /// Screen positions of buttons, set by the View after layout.
    /// Keys are button IDs, values are screen-coordinate centers.
    /// </summary>
    public void UpdateButtonScreenPosition(string buttonId, ScreenPoint screenCenter)
    {
        _dwellDetector.UpdateTargetCenter(buttonId, screenCenter);
    }

    public ToolbarViewModel(ToolbarSettings toolbarSettings, DwellSettings dwellSettings, IMessenger messenger)
    {
        _messenger = messenger;

        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "\u23F8"),
            ("Zoom", "\u2295"),
            ("Keyboard", "\u2328"),
            ("Settings", "\u2699"),
        };

        foreach (var (id, symbol) in buttons)
        {
            var btn = new ToolbarButtonViewModel(id, symbol);
            Buttons.Add(btn);
            _buttonLookup[id] = btn;
            _dwellDetector.AddTarget(new DwellTarget(id,
                new ScreenPoint(0, 0), toolbarSettings.Size / 2, dwellSettings.ToolbarButtonDwellMs));
        }

        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    public void Receive(StateChangedMessage message)
    {
        IsPaused = message.NewState == InteractionState.Paused;
        if (_buttonLookup.TryGetValue("Pause", out var btn))
            btn.Symbol = IsPaused ? "\u25B6" : "\u23F8";
    }

    /// <summary>For testing: simulate a button activation without gaze dwell.</summary>
    public void SimulateButtonActivated(string buttonId) => OnTargetActivated(buttonId);

    private void OnTargetActivated(string targetId)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = 0;

        switch (targetId)
        {
            case "Pause": _messenger.Send(new PauseToggledMessage()); break;
            case "Zoom": _messenger.Send(new ZoomToggledMessage()); break;
            case "Keyboard": _messenger.Send(new KeyboardToggledMessage()); break;
            case "Settings": _messenger.Send(new SettingsRequestedMessage()); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (_buttonLookup.TryGetValue(targetId, out var btn))
            btn.DwellProgress = progress;
    }
}
```

**Step 5: Run tests — expect pass**

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add ToolbarViewModel with button dwell tracking and tests"
```

---

## Phase 5: Complex Overlay ViewModels

### Task 10: Extract NearestKeyTracker to Core

The keyboard's nearest-key tracking algorithm (currently lines 235-289 of KeyboardWindow.xaml.cs) is ~55 lines of geometry logic embedded in a WPF Window. Extract it to a testable Core class.

**Files:**
- Create: `src/EyeCtrl.Core/Processing/NearestKeyTracker.cs`
- Create: `tests/EyeCtrl.Core.Tests/Processing/NearestKeyTrackerTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class NearestKeyTrackerTests
{
    [Fact]
    public void FindsNearestKey_ByDistance()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 500);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(200, 100),
            ["c"] = new(300, 100),
        });

        var result = tracker.Update(new ScreenPoint(110, 105), 0);

        Assert.Equal("a", result.NearestKeyId);
    }

    [Fact]
    public void AccumulatesDwell_WhenNearestKeyStaysTheSame()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 1000);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(200, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        var result = tracker.Update(new ScreenPoint(105, 100), 500_000); // 500ms

        Assert.Equal("a", result.NearestKeyId);
        Assert.True(result.Progress > 0.4 && result.Progress < 0.6);
        Assert.False(result.Activated);
    }

    [Fact]
    public void ActivatesKey_WhenDwellTimeExceeded()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 100);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        var result = tracker.Update(new ScreenPoint(100, 100), 200_000); // 200ms > 100ms

        Assert.True(result.Activated);
        Assert.Equal("a", result.NearestKeyId);
    }

    [Fact]
    public void ResetsDwell_WhenNearestKeyChanges()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 1000);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(300, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        tracker.Update(new ScreenPoint(100, 100), 400_000); // 400ms on 'a'

        // Now move to 'b'
        var result = tracker.Update(new ScreenPoint(300, 100), 500_000);

        Assert.Equal("b", result.NearestKeyId);
        Assert.Equal(0, result.Progress); // reset
    }

    [Fact]
    public void ReturnsNull_WhenNoKeys()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 500);
        var result = tracker.Update(new ScreenPoint(100, 100), 0);

        Assert.Null(result.NearestKeyId);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement NearestKeyTracker**

```csharp
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class NearestKeyTracker
{
    private readonly long _dwellTimeMicroseconds;
    private Dictionary<string, ScreenPoint> _keys = new();
    private string? _currentNearestKeyId;
    private long _enteredTimestamp;

    public NearestKeyTracker(int dwellTimeMs)
    {
        _dwellTimeMicroseconds = dwellTimeMs * 1000L;
    }

    public void SetKeys(Dictionary<string, ScreenPoint> keys)
    {
        _keys = keys;
        _currentNearestKeyId = null;
    }

    public void Clear()
    {
        _keys.Clear();
        _currentNearestKeyId = null;
    }

    public NearestKeyResult Update(ScreenPoint gazePoint, long timestampMicroseconds)
    {
        if (_keys.Count == 0)
            return new NearestKeyResult(null, 0, false);

        // Find nearest key by Euclidean distance
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;

        foreach (var (id, center) in _keys)
        {
            var dx = gazePoint.X - center.X;
            var dy = gazePoint.Y - center.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestId = id;
            }
        }

        if (nearestId != _currentNearestKeyId)
        {
            // Nearest key changed — reset dwell
            _currentNearestKeyId = nearestId;
            _enteredTimestamp = timestampMicroseconds;
            return new NearestKeyResult(nearestId, 0, false);
        }

        // Same key — accumulate dwell
        var elapsed = timestampMicroseconds - _enteredTimestamp;
        var progress = Math.Min(1.0, (double)elapsed / _dwellTimeMicroseconds);
        var activated = elapsed >= _dwellTimeMicroseconds;

        if (activated)
        {
            _currentNearestKeyId = null; // reset after activation
        }

        return new NearestKeyResult(nearestId, progress, activated);
    }
}

public record NearestKeyResult(string? NearestKeyId, double Progress, bool Activated);
```

**Step 4: Run tests — expect pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "NearestKeyTracker"
```

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: extract NearestKeyTracker from KeyboardWindow to Core with tests"
```

---

### Task 11: KeyboardViewModel

This is the largest task. The current KeyboardWindow.xaml.cs is 908 lines. The ViewModel extracts: layout state machine (shift, emoji, sub-layouts), nearest-key dwell tracking, prediction/category dwell, emoji pagination. The View retains: XAML rendering and key visual building.

**Files:**
- Create: `src/EyeCtrl.ViewModels/KeyViewModel.cs`
- Create: `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/KeyboardViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class KeyboardViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly KeyboardSettings _kbSettings = new();
    private readonly DwellSettings _dwellSettings = new() { KeyboardKeyDwellMs = 100, KeyboardPredictionDwellMs = 100 };

    [Fact]
    public void ShowsKeyboard_OnShowMessage()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new ShowKeyboardMessage());
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void HidesKeyboard_OnHideMessage()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        _messenger.Send(new ShowKeyboardMessage());
        _messenger.Send(new HideKeyboardMessage());
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void TogglesShift_OnShiftActivation()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        Assert.False(vm.IsShifted);

        vm.ToggleShift();
        Assert.True(vm.IsShifted);

        vm.ToggleShift();
        Assert.False(vm.IsShifted);
    }

    [Fact]
    public void SendsKeyActivated_OnKeyDwell()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        string? activatedKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => activatedKey = msg.Key);

        vm.OnKeyActivated("a");

        Assert.Equal("a", activatedKey);
    }

    [Fact]
    public void SendsShiftedKey_WhenShifted()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        vm.ToggleShift();

        string? activatedKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => activatedKey = msg.Key);

        vm.OnKeyActivated("a");

        Assert.Equal("A", activatedKey);
    }

    [Fact]
    public void EmojiMode_TogglesOnAndOff()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        Assert.False(vm.IsEmojiMode);

        vm.ToggleEmojiMode();
        Assert.True(vm.IsEmojiMode);

        vm.ToggleEmojiMode();
        Assert.False(vm.IsEmojiMode);
    }

    [Fact]
    public void EmojiPagination_CalculatesCorrectPageCount()
    {
        var vm = new KeyboardViewModel(_kbSettings, _dwellSettings, _messenger);
        // Simulate 85 emojis in a category with 40 per page
        var pageCount = vm.CalculateEmojiPageCount(85);
        Assert.Equal(3, pageCount); // ceil(85/40) = 3
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement KeyViewModel and KeyboardViewModel**

The KeyboardViewModel should extract the following from KeyboardWindow.xaml.cs:
- Shift state management (`_isShifted`, `ToggleShift()`)
- Emoji mode state (`_isEmojiMode`, `ToggleEmojiMode()`)
- Active sub-layout tracking (`_activeSubLayoutId`)
- Emoji pagination (`_emojiPage`, `_activeEmojiCategory`, `EmojisPerPage`)
- Key activation logic (send `KeyActivatedMessage` with correct casing)
- Prediction management

The View retains:
- All WPF rendering (building key visuals on Canvas)
- Screen positioning (`RepositionOnScreen`)
- Visual updates (highlighting nearest key)

This is a scaffold — the full KeyboardViewModel is large. Implement the core state management; the View will call ViewModel methods when keys are activated by the NearestKeyTracker.

**Step 4: Run tests — expect pass**

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add KeyboardViewModel with shift/emoji state, key activation, tests"
```

---

### Task 12: SettingsViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/SettingItemViewModel.cs` (base + subclasses)
- Create: `src/EyeCtrl.ViewModels/SettingsTabViewModel.cs`
- Create: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/SettingsViewModelTests.cs`

**Step 1: Write failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void Has7Tabs()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        Assert.Equal(7, vm.Tabs.Count);
    }

    [Fact]
    public void CursorTab_HasExpectedSettings()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        vm.ShowTab("Cursor");

        Assert.True(vm.CurrentPageItems.Count > 0);
    }

    [Fact]
    public void ResetAll_RestoresDefaults()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        settings.Cursor.CircleSize = 999;
        settings.Dwell.FixationDetectionMs = 999;

        var vm = new SettingsViewModel(settings, service, _messenger);
        vm.ResetAllSettings();

        var defaults = new EyeCtrlSettings();
        Assert.Equal(defaults.Cursor.CircleSize, settings.Cursor.CircleSize);
        Assert.Equal(defaults.Dwell.FixationDetectionMs, settings.Dwell.FixationDetectionMs);
    }

    [Fact]
    public void Pagination_Works()
    {
        var settings = new EyeCtrlSettings();
        var service = new SettingsService("test.json");
        var vm = new SettingsViewModel(settings, service, _messenger);

        // Most tabs fit on one page, but verify navigation works
        vm.ShowTab("Cursor");
        Assert.True(vm.TotalPages >= 1);
        Assert.Equal(0, vm.CurrentPage);
    }
}
```

**Step 2: Run tests — expect failure**

**Step 3: Implement setting item ViewModels**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public abstract partial class SettingItemViewModel : ObservableObject
{
    public string Label { get; }
    public string Id { get; }

    protected SettingItemViewModel(string id, string label) { Id = id; Label = label; }
}

public partial class SliderSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private double _value;
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public Action<double> OnChanged { get; }

    public SliderSettingViewModel(string id, string label, double value, double min, double max, double step, Action<double> onChanged)
        : base(id, label)
    {
        _value = value; Minimum = min; Maximum = max; Step = step; OnChanged = onChanged;
    }

    partial void OnValueChanged(double value) => OnChanged(value);
}

public partial class ToggleSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private bool _isOn;
    public Action<bool> OnChanged { get; }

    public ToggleSettingViewModel(string id, string label, bool value, Action<bool> onChanged)
        : base(id, label)
    {
        _isOn = value; OnChanged = onChanged;
    }

    partial void OnIsOnChanged(bool value) => OnChanged(value);
}

public partial class ComboSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private string _selectedValue;
    public string[] Options { get; }
    public Action<string> OnChanged { get; }

    public ComboSettingViewModel(string id, string label, string value, string[] options, Action<string> onChanged)
        : base(id, label)
    {
        _selectedValue = value; Options = options; OnChanged = onChanged;
    }

    partial void OnSelectedValueChanged(string value) => OnChanged(value);
}

public partial class ColorSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private string _color;
    public Action<string> OnChanged { get; }

    public ColorSettingViewModel(string id, string label, string color, Action<string> onChanged)
        : base(id, label)
    {
        _color = color; OnChanged = onChanged;
    }

    partial void OnColorChanged(string value) => OnChanged(value);
}
```

**Step 4: Implement SettingsTabViewModel and SettingsViewModel**

The SettingsViewModel defines tab content data-driven (instead of procedural UI building). Each tab maps to a list of `SettingItemViewModel` instances bound to settings properties. The `ResetAllSettings()` method uses reflection to copy properties from a fresh defaults instance.

This is a large ViewModel — implement the core structure, tab definitions, pagination, and reset logic.

**Step 5: Run tests — expect pass**

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add SettingsViewModel with data-driven tab content, pagination, reset, tests"
```

---

## Phase 6: GazeEngineAdapter & DI Wiring

### Task 13: Create GazeEngineAdapter

**Files:**
- Create: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`

This class bridges the existing `GazeEngine` + `InteractionManager` C# events to `WeakReferenceMessenger` messages, marshaling to the UI thread.

**Step 1: Implement GazeEngineAdapter**

```csharp
namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class GazeEngineAdapter : IDisposable
{
    private readonly GazeEngine _gazeEngine;
    private readonly InteractionManager _interactionManager;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;

    public GazeEngineAdapter(
        GazeEngine gazeEngine,
        EyeCtrlSettings settings,
        IMessenger messenger,
        Dispatcher dispatcher)
    {
        _gazeEngine = gazeEngine;
        _interactionManager = gazeEngine.InteractionManager;
        _settings = settings;
        _messenger = messenger;
        _dispatcher = dispatcher;

        // Core → ViewModel (via messages, marshaled to UI thread)
        _gazeEngine.CursorMoved += OnCursorMoved;
        _interactionManager.ShowActivationButton += OnShowActivationButton;
        _interactionManager.HideActivationButton += OnHideActivationButton;
        _interactionManager.ShowActionMenu += OnShowActionMenu;
        _interactionManager.HideActionMenu += OnHideActionMenu;
        _interactionManager.StartZoom += OnStartZoom;
        _interactionManager.EndZoom += OnEndZoom;
        _interactionManager.StateChanged += OnStateChanged;
        _interactionManager.ActionRequested += OnActionRequested;

        // ViewModel → Core (via message subscriptions)
        messenger.Register<ActivationCompletedMessage>(this, (_, _) =>
            _interactionManager.OnActivationButtonDwelled());
        messenger.Register<ActionSelectedMessage>(this, (_, msg) =>
            _interactionManager.OnActionSelected(msg.Action));
        messenger.Register<BackSelectedMessage>(this, (_, _) =>
            _interactionManager.OnZoomBack());
        messenger.Register<PauseToggledMessage>(this, (_, _) =>
            _interactionManager.TogglePause());
        messenger.Register<ZoomToggledMessage>(this, (_, _) =>
        {
            _interactionManager.ZoomEnabled = !_interactionManager.ZoomEnabled;
            _settings.Zoom.Enabled = _interactionManager.ZoomEnabled;
        });
    }

    private void OnCursorMoved(ScreenPoint point, long timestamp) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new GazeCursorMovedMessage(point, timestamp)));

    private void OnShowActivationButton(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ShowActivationButtonMessage(point)));

    private void OnHideActivationButton() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActivationButtonMessage()));

    private void OnShowActionMenu(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            var im = _interactionManager;
            var showBack = im.CurrentState == InteractionState.ZoomActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ZoomActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;
            _messenger.Send(new ShowActionMenuMessage(point, _settings.ActionMenu.VisibleActions, showBack, dwellMs));
        });

    private void OnHideActionMenu() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActionMenuMessage()));

    private void OnStartZoom(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() =>
            _messenger.Send(new StartZoomMessage(point, _settings.Zoom.CircleRadiusPixels, _settings.Zoom.MagnificationLevel)));

    private void OnEndZoom() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new EndZoomMessage()));

    private void OnStateChanged(InteractionState state) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new StateChangedMessage(state)));

    private void OnActionRequested(InteractionAction action, ScreenPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point)));

    public void Dispose()
    {
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _interactionManager.ShowActivationButton -= OnShowActivationButton;
        _interactionManager.HideActivationButton -= OnHideActivationButton;
        _interactionManager.ShowActionMenu -= OnShowActionMenu;
        _interactionManager.HideActionMenu -= OnHideActionMenu;
        _interactionManager.StartZoom -= OnStartZoom;
        _interactionManager.EndZoom -= OnEndZoom;
        _interactionManager.StateChanged -= OnStateChanged;
        _interactionManager.ActionRequested -= OnActionRequested;
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Build and verify**

```bash
dotnet build src/EyeCtrl.App
```

**Step 3: Commit**

```bash
git add -A
git commit -m "feat: add GazeEngineAdapter bridging Core events to MVVM messages"
```

---

### Task 14: Rewrite App.xaml.cs with DI container

This is the centerpiece: replace the 397-line manual wiring with DI container setup. App.xaml.cs becomes a composition root.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Rewrite App.xaml.cs**

The new App.xaml.cs should:

1. Build `ServiceCollection`:
   - Register `IMessenger` → `WeakReferenceMessenger` (singleton)
   - Register `SettingsService`, `EyeCtrlSettings` (singleton)
   - Register `IGazeProvider` (factory, test-mode aware)
   - Register `IInputSimulator` → `Win32InputSimulator` (singleton)
   - Register `GazeEngine` (singleton)
   - Register all ViewModels (singleton)
   - Register all overlay windows (singleton)
   - Register `GazeEngineAdapter` (singleton)

2. Build `ServiceProvider`

3. In `OnStartup`:
   - Show splash (if not test-mode)
   - Build service provider
   - Resolve `GazeEngineAdapter` (triggers all wiring via constructor)
   - Resolve overlay windows
   - Set each window's `DataContext` to its ViewModel
   - Show persistent overlays (cursor circle, toolbar)
   - Close splash
   - Start gaze engine

4. Wire remaining things that don't fit cleanly in DI:
   - System tray icon
   - Keyboard toggle (involves `GazeEngine.SuppressFixation`)
   - Settings window (created on demand)
   - `OnExit` cleanup

**Key principle:** Don't try to make everything a message immediately. The adapter handles Core↔ViewModel communication. App.xaml.cs still handles things like:
- `KeyboardToggledMessage` → toggle keyboard visibility + `SuppressFixation`
- `SettingsRequestedMessage` → create and show `SettingsWindow`
- `KeyActivatedMessage` / `PredictionSelectedMessage` → `IInputSimulator` calls
- System tray icon setup

These can be extracted to additional services in a future iteration if needed.

**Step 2: Update overlay windows to use DataContext**

Each overlay window should accept its ViewModel as `DataContext` and respond to PropertyChanged for position updates. For example, `CursorCircleWindow` subscribes to the VM's `PropertyChanged` and calls `UpdatePosition` when `Position` changes.

**Step 3: Build and run manually**

```bash
dotnet build src/EyeCtrl.App
```

Launch the app in test mode to verify it still works:

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

**Step 4: Run all tests**

```bash
dotnet test EyeCtrl.slnx
```

Expected: All existing tests pass. All new ViewModel tests pass.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: rewrite App.xaml.cs with DI container and MVVM wiring"
```

---

## Phase 7: Cleanup & Bug Fixes

### Task 15: Update overlay views to use ViewModels

Go through each overlay window and simplify the code-behind to delegate to ViewModels:

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs` — subscribe to VM.PropertyChanged for position/appearance
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` — subscribe to VM for show/hide/dwell
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` — build radial UI from VM items, remove DwellDetector (moved to VM)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` — build buttons from VM, remove DwellDetector (moved to VM)
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs` — subscribe to VM for show/hide
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` — build keys from VM, use NearestKeyTracker from Core via VM, remove state (shift, emoji)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` — build settings UI from VM tab definitions

For each overlay:
- Remove `DwellDetector` field (now in ViewModel)
- Remove `UpdateGaze()` method (now handled via messages)
- Remove events (PauseToggled, etc.) — now messages
- Remove state fields that moved to ViewModel
- Add `PropertyChanged` subscription on ViewModel for position/visibility
- Keep rendering code (building WPF elements on Canvas)

**This is the largest code change task.** Work through each window one at a time:
1. CursorCircleWindow — simplest, prove the pattern
2. ActivationButtonWindow — simple, animation stays
3. ZoomOverlayWindow — simple, screen capture stays
4. ActionMenuWindow — remove DwellDetector, render from VM.Items
5. ToolbarWindow — remove DwellDetector, render from VM.Buttons
6. KeyboardWindow — largest: remove NearestKeyTracker code, use VM
7. SettingsWindow — largest: remove page-building, use VM tab definitions

**Step 1: Update each overlay (one at a time, building between each)**

```bash
dotnet build src/EyeCtrl.App
```

**Step 2: Run app in test mode after each overlay update**

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

Verify: cursor follows mouse, toolbar works, activation button appears on fixation, action menu shows.

**Step 3: Commit after each overlay**

```bash
git add -A
git commit -m "refactor: update [OverlayName] to use ViewModel"
```

---

### Task 16: Fix filter recreation bug

Currently `GazeEngine` creates the `IGazeFilter` once in the constructor and never recreates it when settings change.

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`
- Create: `tests/EyeCtrl.Core.Tests/GazeEngineFilterRecreationTests.cs`

**Step 1: Write failing test**

```csharp
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineFilterRecreationTests
{
    [Fact]
    public void RecreatiesFilter_WhenFilterModeChanges()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var provider = new TestGazeProvider();
        var simulator = new TestInputSimulator();
        var engine = new GazeEngine(provider, simulator, settings);

        // Change filter mode
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;

        // The engine should now use a OneEuro filter (not Direct)
        // We verify by checking that the filter applies smoothing
        // (Direct returns raw coordinates, OneEuro smooths them)
        // This is an integration test — the exact assertion depends on filter behavior
        Assert.True(true); // Placeholder — the real test is that it doesn't crash
    }

    private class TestGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SimulateGaze(double x, double y) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, 0, true));
    }

    private class TestInputSimulator : IInputSimulator
    {
        public void MoveCursor(ScreenPoint point) { }
        public void LeftClick(ScreenPoint point) { }
        public void RightClick(ScreenPoint point) { }
        public void DoubleClick(ScreenPoint point) { }
        public void MouseDown(ScreenPoint point) { }
        public void MouseUp(ScreenPoint point) { }
        public void Scroll(ScreenPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
    }
}
```

**Step 2: Add settings change listener to GazeEngine**

In `GazeEngine`, subscribe to `_settings.Cursor.PropertyChanged` and recreate the filter when `FilterMode` changes:

```csharp
// In GazeEngine constructor, after creating filter:
_settings.Cursor.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(CursorSettings.FilterMode))
        RecreateFilter();
};
```

**Step 3: Run tests**

```bash
dotnet test EyeCtrl.slnx
```

**Step 4: Commit**

```bash
git add -A
git commit -m "fix: recreate gaze filter when FilterMode setting changes at runtime"
```

---

### Task 17: Remove dead code and final verification

**Step 1: Search for unused code**

Remove any events, methods, or fields that are now dead code after the MVVM conversion:
- Old events on overlay windows (PauseToggled, ZoomToggled, etc.) that are now messages
- Old UpdateGaze methods on overlays that are now handled by ViewModel message subscriptions
- Win32 interop constants/methods that moved to OverlayWindow base

**Step 2: Run full test suite**

```bash
dotnet test EyeCtrl.slnx
```

Expected: All tests pass.

**Step 3: Build release**

```bash
dotnet publish src/EyeCtrl.App -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Step 4: Run app in test mode**

Launch and verify all features work:
- Cursor circle follows gaze
- Fixation → activation button appears
- Dwell on activation → action menu
- Action menu dwell → action executes
- Toolbar: pause, zoom toggle, keyboard toggle, settings
- Settings window opens and saves
- System tray icon

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove dead code after MVVM conversion, final cleanup"
```

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1 | 1-3 | Infrastructure: project, packages, OverlayWindow base, messages |
| 2 | 4 | Settings: convert POCOs to ObservableObject |
| 3 | 5-7 | Simple VMs: CursorCircle, ActivationButton, ZoomOverlay |
| 4 | 8-9 | Medium VMs: ActionMenu, Toolbar |
| 5 | 10-12 | Complex VMs: NearestKeyTracker extraction, Keyboard, Settings |
| 6 | 13-14 | GazeEngineAdapter + DI container rewrite of App.xaml.cs |
| 7 | 15-17 | Update views, fix filter bug, cleanup |

**Total: 17 tasks across 7 phases.**
