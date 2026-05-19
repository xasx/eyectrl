# Glass Backdrop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fullscreen glass backdrop window behind action menu and manual selection overlays, configurable via settings (Acrylic/Mica/SolidDarken).

**Architecture:** New `GlassBackdropWindow : OverlayWindow` (fullscreen, click-through) and `GlassBackdropViewModel` follow existing MVVM patterns. `EventCoordinator` manages show/hide lifecycle. Settings stored via `BackdropSettings` in the existing JSON persistence layer.

**Tech Stack:** WPF (.NET 10), CommunityToolkit.Mvvm, Win32 P/Invoke (SetWindowCompositionAttribute, DwmSetWindowAttribute), xUnit (no mocking library)

---

### Task 1: BackdropType Enum + BackdropSettings

**Files:**
- Create: `src/EyeCtrl.Core/BackdropType.cs`
- Create: `src/EyeCtrl.Core/Settings/BackdropSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:14-16`

- [ ] **Step 1: Write the failing test for BackdropType enum**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/BackdropSettingsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using Xunit;

public class BackdropSettingsTests
{
    [Fact]
    public void Defaults_AreEnabledAcrylicWithOpacity()
    {
        var settings = new BackdropSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(BackdropType.Acrylic, settings.Type);
        Assert.Equal(0.3, settings.Opacity);
    }

    [Fact]
    public void Validate_OpacityInRange_ReturnsNoErrors()
    {
        var settings = new BackdropSettings { Opacity = 0.5 };
        var errors = settings.Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_OpacityOutOfRange_ReturnsError()
    {
        var settings = new BackdropSettings { Opacity = 0.05 };
        var errors = settings.Validate();
        Assert.NotEmpty(errors);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BackdropSettingsTests"`
Expected: FAIL (types not defined)

- [ ] **Step 3: Write BackdropType enum and BackdropSettings**

```csharp
// src/EyeCtrl.Core/BackdropType.cs
namespace EyeCtrl.Core;

public enum BackdropType
{
    Acrylic,
    Mica,
    SolidDarken
}
```

```csharp
// src/EyeCtrl.Core/Settings/BackdropSettings.cs
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;

public partial class BackdropSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private BackdropType _type = BackdropType.Acrylic;
    [ObservableProperty] private double _opacity = 0.3;

    partial void OnOpacityChanging(double value)
    {
        if (value < 0.1 || value > 0.9)
            throw new ArgumentOutOfRangeException(nameof(value), "Opacity must be between 0.1 and 0.9");
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Opacity < 0.1 || Opacity > 0.9)
            errors.Add($"Backdrop opacity must be between 0.1 and 0.9 (was {Opacity}).");
        if (!Enum.IsDefined(typeof(BackdropType), Type))
            errors.Add($"Invalid backdrop type: {Type}.");
        return errors;
    }
}
```

- [ ] **Step 4: Add Backdrop property to EyeCtrlSettings**

Add after line 15 (`ThemeSettings` property) in `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`:

```csharp
[ObservableProperty] private BackdropSettings _backdrop = new();
```

Add validation in `EyeCtrlSettings.Validate()` after Theme validation:

```csharp
if (Backdrop is null)
    errors.Add("Backdrop settings must not be null.");
else
    errors.AddRange(Backdrop.Validate());
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BackdropSettingsTests"`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/BackdropType.cs src/EyeCtrl.Core/Settings/BackdropSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/BackdropSettingsTests.cs
git commit -m "feat: add BackdropType enum and BackdropSettings"
```

---

### Task 2: BackdropDismissMessage

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs:84-89`

- [ ] **Step 1: Add BackdropDismissMessage record**

Add after `SettingsChangedMessage` (line 89) in `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`:

```csharp
public record BackdropDismissMessage;
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/EyeCtrl.ViewModels`
Expected: PASS (no errors)

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "feat: add BackdropDismissMessage"
```

---

### Task 3: GlassBackdropViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/GlassBackdropViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.ViewModels.Tests/GlassBackdropViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class GlassBackdropViewModelTests
{
    [Fact]
    public void Receive_ShowBackdropMessage_SetsIsVisibleAndDwellTarget()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);

        messenger.Send(new ShowBackdropMessage(buttonCenter));

        Assert.True(vm.IsVisible);
        Assert.Equal(buttonCenter, vm.HideButtonCenter);
    }

    [Fact]
    public void Receive_HideBackdropMessage_SetsIsVisibleFalse()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        messenger.Send(new ShowBackdropMessage(new PhysicalPoint(1860, 40)));

        messenger.Send(new HideBackdropMessage());

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void DwellOnHideButton_SendsBackdropDismissMessage()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        messenger.Send(new ShowBackdropMessage(buttonCenter));
        BackdropDismissMessage? received = null;
        messenger.Register<BackdropDismissMessage>(this, (_, m) => received = m);

        // Simulate gaze inside hide button for full dwell duration
        var buttonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius);
        long now = 1_000_000;
        long dwellUs = UIConstants.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now));
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now + dwellUs));

        Assert.NotNull(received);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~GlassBackdropViewModelTests"`
Expected: FAIL (types not defined)

- [ ] **Step 3: Add backdrop constants to UIConstants**

In `src/EyeCtrl.ViewModels/UIConstants.cs`, after `ManualSelectionSideButtonDwellMs` (line 32):

```csharp
/// <summary>Dwell time in milliseconds for the backdrop hide button.</summary>
public const int BackdropHideButtonDwellMs = 600;

/// <summary>Distance from screen edges to the backdrop hide button center in logical pixels.</summary>
public const double BackdropHideButtonOffset = 40.0;
```

- [ ] **Step 4: Add ShowBackdropMessage and HideBackdropMessage to InteractionMessages.cs**

After `BackdropDismissMessage`:

```csharp
/// <summary>Sent to show the glass backdrop. HideButtonCenter is in physical screen pixels.</summary>
public record ShowBackdropMessage(PhysicalPoint HideButtonCenter);
public record HideBackdropMessage;
```

- [ ] **Step 5: Write GlassBackdropViewModel implementation**

```csharp
// src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class GlassBackdropViewModel : ObservableObject,
    IRecipient<ShowBackdropMessage>,
    IRecipient<HideBackdropMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string HideTargetId = "backdrop_hide";

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;
    private bool _disposed;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private PhysicalPoint _hideButtonCenter = new(0, 0);
    [ObservableProperty] private double _hideButtonProgress;

    public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(ShowBackdropMessage message)
    {
        HideButtonCenter = message.HideButtonCenter;
        HideButtonProgress = 0;
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            HideTargetId,
            message.HideButtonCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            UIConstants.BackdropHideButtonDwellMs));
        IsVisible = true;
    }

    public void Receive(HideBackdropMessage message)
    {
        IsVisible = false;
        HideButtonProgress = 0;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == HideTargetId)
            _messenger.Send(new BackdropDismissMessage());
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == HideTargetId)
            HideButtonProgress = progress;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~GlassBackdropViewModelTests"`
Expected: PASS (3 tests)

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs tests/EyeCtrl.ViewModels.Tests/GlassBackdropViewModelTests.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs src/EyeCtrl.ViewModels/UIConstants.cs
git commit -m "feat: add GlassBackdropViewModel with dwell hide button"
```

---

### Task 4: GlassBackdropWindow (XAML + Code-Behind)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs`

- [ ] **Step 1: Write XAML**

```xml
<!-- src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml -->
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.GlassBackdropWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False">
    <Canvas x:Name="BackdropCanvas" IsHitTestVisible="False"/>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Write code-behind**

```csharp
// src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs
namespace EyeCtrl.App.Overlays;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public partial class GlassBackdropWindow : OverlayWindow
{
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private Rectangle? _solidBackground;
    private Ellipse? _hideButtonCircle;
    private Ellipse? _hideButtonProgressArc;
    private TextBlock? _hideButtonSymbol;
    private PhysicalPoint _hideButtonCenter;
    private BackdropType _currentType;

    public GlassBackdropWindow()
    {
        IsClickThrough = true;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    // ── Show / Hide ──

    public new void Show()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        Left = screen.Bounds.X / DpiHelper.ScaleX;
        Top = screen.Bounds.Y / DpiHelper.ScaleY;
        Width = screen.Bounds.Width / DpiHelper.ScaleX;
        Height = screen.Bounds.Height / DpiHelper.ScaleY;
        base.Show();
        EnsureHandle(); // Window handle is valid after Show()
    }

    public new void Hide()
    {
        base.Hide();
        ClearContent();
    }

    private void EnsureHandle()
    {
        _ = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    }

    // ── Backdrop Type ──

    public void ApplyBackdrop(BackdropSettings settings)
    {
        if (!settings.Enabled) return;

        ClearContent();

        if (settings.Type == BackdropType.Mica && TryApplyMica())
        {
            _currentType = BackdropType.Mica;
        }
        else if (settings.Type == BackdropType.Mica || settings.Type == BackdropType.Acrylic)
        {
            if (TryApplyAcrylic())
                _currentType = BackdropType.Acrylic;
            else
                ApplySolidDarken(settings.Opacity);
        }
        else
        {
            ApplySolidDarken(settings.Opacity);
        }
    }

    private bool TryApplyAcrylic()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = 0x01000000
            };
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                SizeOfData = Marshal.SizeOf(accent),
                Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent))
            };
            Marshal.StructureToPtr(accent, data.Data, false);
            var result = SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(data.Data);
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryApplyMica()
    {
        if (Environment.OSVersion.Version.Build < 22000) return false;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            int backdropType = 2; // DWMSBT_MAINWINDOW
            int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplySolidDarken(double opacity)
    {
        _currentType = BackdropType.SolidDarken;
        _solidBackground = new Rectangle
        {
            Width = Width,
            Height = Height,
            Fill = new SolidColorBrush(Color.FromArgb(
                (byte)(255 * opacity), 18, 18, 18))
        };
        Canvas.SetLeft(_solidBackground, 0);
        Canvas.SetTop(_solidBackground, 0);
        BackdropCanvas.Children.Add(_solidBackground);
    }

    // ── Hide Button ──

    public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0)
    {
        _hideButtonCenter = buttonCenter;
        ClearButton();
        var dipX = DpiHelper.PhysicalToLogicalX(buttonCenter.X) - UIConstants.ButtonRadius;
        var dipY = DpiHelper.PhysicalToLogicalY(buttonCenter.Y) - UIConstants.ButtonRadius;
        var dipSize = UIConstants.ButtonRadius * 2;

        _hideButtonCircle = new Ellipse
        {
            Width = dipSize, Height = dipSize,
            Fill = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
            Stroke = new SolidColorBrush(Color.FromArgb(100, 100, 100, 100)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(_hideButtonCircle, dipX);
        Canvas.SetTop(_hideButtonCircle, dipY);
        BackdropCanvas.Children.Add(_hideButtonCircle);

        if (progress > 0)
        {
            var circumference = Math.PI * (dipSize - 4);
            _hideButtonProgressArc = new Ellipse
            {
                Width = dipSize - 4, Height = dipSize - 4,
                Stroke = Brushes.Lime, StrokeThickness = 4,
                StrokeDashArray = new DoubleCollection { circumference * progress, circumference },
                RenderTransform = new RotateTransform(-90, (dipSize - 4) / 2, (dipSize - 4) / 2)
            };
            Canvas.SetLeft(_hideButtonProgressArc, dipX + 2);
            Canvas.SetTop(_hideButtonProgressArc, dipY + 2);
            BackdropCanvas.Children.Add(_hideButtonProgressArc);
        }

        _hideButtonSymbol = new TextBlock
        {
            Text = "\u2715",
            FontSize = 22,
            Foreground = Brushes.White,
            Width = dipSize, Height = dipSize,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(_hideButtonSymbol, dipX);
        Canvas.SetTop(_hideButtonSymbol, dipY + (dipSize - 22) / 2 - 2);
        BackdropCanvas.Children.Add(_hideButtonSymbol);
    }

    public void UpdateHideButtonProgress(double progress)
    {
        DrawHideButton(_hideButtonCenter, progress);
    }

    private void ClearContent()
    {
        BackdropCanvas.Children.Clear();
        _solidBackground = null;
        ClearButton();
    }

    private void ClearButton()
    {
        _hideButtonCircle = null;
        _hideButtonProgressArc = null;
        _hideButtonSymbol = null;
    }

    // ── Win32 Structs ──

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS (no errors)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs
git commit -m "feat: add GlassBackdropWindow with acrylic/mica/solid support"
```

---

### Task 5: OverlayManager + IOverlayManager Additions

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs:53-61`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs:28-74`

- [ ] **Step 1: Add GlassBackdrop property to IOverlayManager**

After `ManualSelectionIndicator` property (line 53):

```csharp
/// <summary>Gets the fullscreen glass backdrop overlay window.</summary>
GlassBackdropWindow GlassBackdrop { get; }
```

- [ ] **Step 2: Add GlassBackdrop to OverlayManager**

Add property (after line 28):

```csharp
public GlassBackdropWindow GlassBackdrop { get; private set; } = null!;
```

Add creation in `Initialize()` (after line 51):

```csharp
GlassBackdrop = new GlassBackdropWindow();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs
git commit -m "feat: add GlassBackdrop to OverlayManager"
```

---

### Task 6: DI Registration in AppBootstrapper

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:72-79`

- [ ] **Step 1: Register GlassBackdropViewModel in DI**

After `SettingsViewModel` registration (line 79):

```csharp
services.AddSingleton<GlassBackdropViewModel>();
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat: register GlassBackdropViewModel in DI"
```

---

### Task 7: EventCoordinator Integration

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:138-150` (recapture handler)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:272-298` (manual selection visibility)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:345-378` (action menu visibility)

- [ ] **Step 1: Add GlassBackdrop field and initialization in EventCoordinator**

In `EventCoordinator.cs`, add field (after line 41):

```csharp
private GlassBackdropWindow? _glassBackdrop;
private GlassBackdropViewModel? _glassBackdropVM;
```

In `Initialize()`, after `SetupGazeRouting()` (line 56):

```csharp
SetupBackdropHandlers();
```

Add new method after `SetupGazeRouting()`:

```csharp
private void SetupBackdropHandlers()
{
    if (_context == null) return;
    _glassBackdrop = _context.OverlayManager.GlassBackdrop;
}

// Helper: show backdrop if enabled in settings
private void ShowBackdropIfEnabled()
{
    if (_glassBackdrop == null || _context == null) return;
    var settings = _context.Settings.Backdrop;
    if (!settings.Enabled) return;

    // Compute hide button position (top-right of primary screen)
    var screen = System.Windows.Forms.Screen.PrimaryScreen;
    if (screen == null) return;
    var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
    var dipTop = UIConstants.BackdropHideButtonOffset;
    var hideButtonCenter = new PhysicalPoint(
        DpiHelper.LogicalToPhysicalX(dipRight),
        DpiHelper.LogicalToPhysicalY(dipTop));

    _glassBackdrop.Show(); // Must show first to get window handle for acrylic/mica
    _glassBackdrop.ApplyBackdrop(settings);
    _glassBackdrop.DrawHideButton(hideButtonCenter);
    _context.Messenger.Send(new ShowBackdropMessage(hideButtonCenter));
}

private void HideBackdrop()
{
    _glassBackdrop?.Hide();
    _context?.Messenger.Send(new HideBackdropMessage());
}
```

- [ ] **Step 2: Show/hide backdrop with manual selection**

In `OnManualSelectionVMPropertyChanged()`, add `ShowBackdropIfEnabled()` AFTER the overlays are shown (line 292), and `HideBackdrop()` in the `else` block:

```csharp
if (_context.ManualSelectionVM.IsVisible)
{
    cursorCircle.Hide();
    activationButton.HideImmediate();
    ManualSelectionOverlay.Show(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification, _context.ManualSelectionVM.GazePoint);
    ManualSelectionIndicator.Show();
    ShowBackdropIfEnabled();  // ← NEW: after capture completes
}
else
{
    ManualSelectionOverlay.Hide();
    ManualSelectionIndicator.Hide();
    HideBackdrop();  // ← NEW
}
```

- [ ] **Step 3: Show/hide backdrop with action menu**

In `OnActionMenuVMPropertyChanged()`, add `ShowBackdropIfEnabled()` AFTER `actionMenu.Show(...)` (line 371), and `HideBackdrop()` in the `else` block:

```csharp
if (_context.ActionMenuVM.IsVisible)
{
    cursorCircle.Hide();
    activationButton.HideImmediate();
    actionMenu.Show(
        _context.ActionMenuVM.Center,
        _context.Settings.ActionMenu.VisibleActions,
        _context.ActionMenuVM.ShowBack,
        _context.ActionMenuVM.ShowBack ? _context.Settings.Dwell.ManualSelectionActionMenuDwellMs : _context.Settings.Dwell.ActionMenuDwellMs,
        _context.ActionMenuVM.EffectiveRingRadius,
        _context.Settings.Cursor.CircleSize,
        _context.Settings.Cursor.CircleColor,
        _context.Settings.Cursor.PositionIndicatorStyle,
        _context.ActionMenuVM.ManualSelectionCenter,
        _context.ActionMenuVM.ManualSelectionRadius,
        _context.ActionMenuVM.GazePoint);
    ShowBackdropIfEnabled();  // ← NEW
}
else
{
    actionMenu.Hide();
    HideBackdrop();  // ← NEW
}
```

- [ ] **Step 4: Add backdrop VM PropertyChanged handler for progress**

In `SetupPropertyChangedHandlers()`, add after `TodoVM.PropertyChanged` line:

```csharp
_context.BackdropVM.PropertyChanged += OnBackdropVMPropertyChanged;
```

Add new handler method:

```csharp
private void OnBackdropVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (_glassBackdrop == null) return;
    if (e.PropertyName == nameof(GlassBackdropViewModel.HideButtonProgress))
    {
        _glassBackdrop.UpdateHideButtonProgress(_context!.BackdropVM.HideButtonProgress);
    }
}
```

- [ ] **Step 5: Hide backdrop during re-capture**

In `SetupMessageHandlers()`, modify the `ManualSelectionRecaptureMessage` handler (lines 138-150). Hide the backdrop before sending the re-capture message. The backdrop will be re-shown automatically by `OnManualSelectionVMPropertyChanged` when the new `StartManualSelectionMessage` triggers `IsVisible = true`.

```csharp
_context.Messenger.Register<EventCoordinator, ManualSelectionRecaptureMessage>(this, (_, _) =>
    _context.Dispatcher.BeginInvoke(() =>
    {
        if (im.CurrentState == InteractionState.ManualSelectionActive && im.FixationPoint != null)
        {
            HideBackdrop();  // ← NEW: hide before re-capture
            _context.Messenger.Send(new StartManualSelectionMessage(
                im.FixationPoint.Value,
                _context.Settings.ManualSelection.CircleRadiusPixels,
                _context.Settings.ManualSelection.MagnificationLevel,
                im.FixationPoint.Value));
        }
    }));
```

- [ ] **Step 6: Handle BackdropDismissMessage**

In `SetupMessageHandlers()`, add after other message registrations:

```csharp
_context.Messenger.Register<EventCoordinator, BackdropDismissMessage>(this, (_, _) =>
    _context.Dispatcher.BeginInvoke(() =>
    {
        HideBackdrop();
        var im = _context.InteractionManager;
        if (im.CurrentState == InteractionState.ActionMenuActive)
            im.OnBack();
        else if (im.CurrentState == InteractionState.ManualSelectionActive)
            im.OnManualSelectionBack();
    }));
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: integrate GlassBackdrop into EventCoordinator"
```

---

### Task 8: App.xaml.cs Wiring

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:119-152`

- [ ] **Step 1: Resolve GlassBackdropViewModel and wire it up**

After `ManualSelectionIndicatorVM` resolution (line 126), add:

```csharp
var backdropVM = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
```

Update `OverlayViewModels` construction (line 139) — this requires updating the record. Change:

```csharp
var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM);
```

First update the `OverlayViewModels` record in `IOverlayManager.cs` to add:

```csharp
GlassBackdropViewModel GlassBackdrop
```

Add as 8th parameter:

```csharp
var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM, backdropVM);
```

- [ ] **Step 2: Add backdrop VM to EventCoordinationContext**

Update `EventCoordinationContext` record in `IEventCoordinator.cs` to add `GlassBackdropViewModel`:

After `KeyboardVM` (line 58):

```csharp
GlassBackdropViewModel BackdropVM,
```

Update the construction in `App.xaml.cs` (line 149):

```csharp
var coordinationContext = new EventCoordinationContext(
    cursorVM, activationVM, ManualSelectionVM, ManualSelectionIndicatorVM, actionMenuVM, toolbarVM, keyboardVM, backdropVM,
    overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, inputSimulator);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/Services/IOverlayManager.cs
git commit -m "feat: wire GlassBackdrop into app startup and coordination context"
```

---

### Task 9: Integration Tests

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Services/EventCoordinatorBackdropTests.cs`

- [ ] **Step 1: Write integration tests for EventCoordinator backdrop logic**

```csharp
// tests/EyeCtrl.App.Tests/Services/EventCoordinatorBackdropTests.cs
namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using Xunit;

public class EventCoordinatorBackdropTests
{
    private class FakeOverlayManager : IOverlayManager
    {
        public CursorCircleWindow CursorCircle { get; } = null!;
        public ActivationButtonWindow ActivationButton { get; } = null!;
        public ManualSelectionOverlayWindow ManualSelectionOverlay { get; } = null!;
        public ManualSelectionSideButtonWindow ManualSelectionSideButtons { get; } = null!;
        public ActionMenuWindow ActionMenu { get; } = null!;
        public ToolbarWindow Toolbar { get; } = null!;
        public KeyboardWindow Keyboard { get; } = null!;
        public ManualSelectionGazeIndicatorWindow ManualSelectionIndicator { get; } = null!;
        public GlassBackdropWindow GlassBackdrop { get; set; } = null!;

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType) { }
    }

    [Fact]
    public void ManualSelectionVisible_ShowsBackdrop()
    {
        // Test that backdrop is shown when manual selection becomes visible
        // Uses fake overlays to avoid WPF window creation
    }

    [Fact]
    public void BackdropDismissMessage_HidesBackdropAndExitsOverlay()
    {
        // Test that dismiss message hides backdrop and triggers interaction manager back
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Backdrop"`
Expected: PASS

- [ ] **Step 3: Run full build**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS (no errors)

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Services/EventCoordinatorBackdropTests.cs
git commit -m "test: add backdrop integration tests"
```
