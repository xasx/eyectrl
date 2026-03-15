# DPI Awareness Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix coordinate space mismatch between physical-pixel gaze input and WPF's device-independent pixel (DIP) window positioning at high-DPI scaling (e.g. 200%).

**Architecture:** .NET 10 WPF enables Per-Monitor V2 DPI awareness by default. `GetCursorPos` and `PointToScreen` return physical screen pixels, but `Window.Left/Top` and `SystemParameters` use DIPs (96-DPI logical units). The fix adds a `DpiHelper` that converts at boundaries. The Core pipeline stays entirely in physical pixels.

**Tech Stack:** C# / .NET 10 / WPF, xUnit

**Coordinate space conventions after this change:**
- `ScreenPoint` throughout Core/Platform/ViewModels = **physical pixels**
- `Window.Left/Top`, `Width/Height`, `SystemParameters` = **WPF DIPs**
- Conversion happens at the App layer boundary (overlay windows, screen capture, gaze clamping)
- DwellTarget centers = **physical pixels** (from `PointToScreen` or computed with scaled offsets)
- DwellTarget radii = **physical pixels** (DIP radii scaled at creation time)

---

## Chunk 1: DpiHelper + Initialization

### Task 1: Create DpiHelper

**Files:**
- Create: `src/EyeCtrl.Core/Services/DpiHelper.cs`
- Create: `tests/EyeCtrl.Core.Tests/Services/DpiHelperTests.cs`

DpiHelper lives in Core (pure math, no WPF dependency). Initialization is called from App.xaml.cs with the scale factors obtained from WPF.

- [ ] **Step 1: Write failing tests for DpiHelper**

Create `tests/EyeCtrl.Core.Tests/Services/DpiHelperTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Services;
using Xunit;

public class DpiHelperTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PhysicalToLogical_DividesByScale(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        Assert.Equal(100.0, DpiHelper.PhysicalToLogicalX(100.0 * scale), 5);
        Assert.Equal(100.0, DpiHelper.PhysicalToLogicalY(100.0 * scale), 5);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void LogicalToPhysical_MultipliesByScale(double scale)
    {
        DpiHelper.Initialize(scale, scale);
        Assert.Equal(100.0 * scale, DpiHelper.LogicalToPhysicalX(100.0), 5);
        Assert.Equal(100.0 * scale, DpiHelper.LogicalToPhysicalY(100.0), 5);
    }

    [Fact]
    public void DefaultScale_IsOne()
    {
        DpiHelper.Initialize(1.0, 1.0);
        Assert.Equal(500.0, DpiHelper.PhysicalToLogicalX(500.0));
        Assert.Equal(500.0, DpiHelper.LogicalToPhysicalX(500.0));
    }

    [Fact]
    public void AsymmetricScale_UsesCorrectAxis()
    {
        DpiHelper.Initialize(2.0, 1.5);
        Assert.Equal(50.0, DpiHelper.PhysicalToLogicalX(100.0));
        Assert.Equal(100.0 / 1.5, DpiHelper.PhysicalToLogicalY(100.0), 5);
        Assert.Equal(200.0, DpiHelper.LogicalToPhysicalX(100.0));
        Assert.Equal(150.0, DpiHelper.LogicalToPhysicalY(100.0));
    }
}
```

- [ ] **Step 2: Run tests — expect compilation failure**

```
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FullyQualifiedName~DpiHelperTests"
```

Expected: Build error — `DpiHelper` doesn't exist.

- [ ] **Step 3: Implement DpiHelper**

Create `src/EyeCtrl.Core/Services/DpiHelper.cs`:

```csharp
namespace EyeCtrl.Core.Services;

/// <summary>
/// Provides DPI scale factor and coordinate conversion between
/// physical screen pixels and WPF device-independent pixels (DIPs).
/// Initialize() must be called once at startup before any conversions.
/// All conversion methods are thread-safe after initialization.
/// </summary>
public static class DpiHelper
{
    /// <summary>Horizontal scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleX { get; private set; } = 1.0;

    /// <summary>Vertical scale factor (e.g. 2.0 for 200% DPI).</summary>
    public static double ScaleY { get; private set; } = 1.0;

    /// <summary>
    /// Set the DPI scale factors. Call once at app startup.
    /// From WPF: var m = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
    ///           DpiHelper.Initialize(m.M11, m.M22);
    /// </summary>
    public static void Initialize(double scaleX, double scaleY)
    {
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    // --- Physical screen pixels → WPF DIPs ---

    public static double PhysicalToLogicalX(double physicalX) => physicalX / ScaleX;
    public static double PhysicalToLogicalY(double physicalY) => physicalY / ScaleY;

    // --- WPF DIPs → Physical screen pixels ---

    public static double LogicalToPhysicalX(double logicalX) => logicalX * ScaleX;
    public static double LogicalToPhysicalY(double logicalY) => logicalY * ScaleY;
}
```

- [ ] **Step 4: Run tests — expect pass**

```
dotnet test tests/EyeCtrl.Core.Tests/ --filter "FullyQualifiedName~DpiHelperTests"
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Services/DpiHelper.cs tests/EyeCtrl.Core.Tests/Services/DpiHelperTests.cs
git commit -m "feat: add DpiHelper for physical↔DIP coordinate conversion"
```

---

### Task 2: Initialize DpiHelper at startup + fix gaze provider screen dimensions

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Timing is critical.** The gaze provider factory is registered with DI and resolved eagerly at line ~150 (`GetRequiredService<IGazeProvider>`). `DpiHelper.Initialize()` needs a shown WPF window to get `PresentationSource`. The splash window is shown at line 51 — **before** DI resolution. So:

1. Initialize DpiHelper from the splash window immediately after `splash.Show()`
2. For `--test-mode` (no splash), use the WinForms `Screen` class (the project has `UseWindowsForms=true`) or P/Invoke `GetDpiForSystem()`.

- [ ] **Step 1: Add DpiHelper initialization in App.xaml.cs**

After the splash window is shown (around line 51), add:

```csharp
// After: splash.Show();
var source = PresentationSource.FromVisual(splash);
if (source?.CompositionTarget != null)
{
    var m = source.CompositionTarget.TransformToDevice;
    DpiHelper.Initialize(m.M11, m.M22);
}
```

For test mode (no splash), add a fallback before the DI block:

```csharp
if (IsTestMode)
{
    // No splash window — use GetDpiForSystem P/Invoke
    var dpi = NativeMethods.GetDpiForSystem();
    var scale = dpi / 96.0;
    DpiHelper.Initialize(scale, scale);
}
```

Add the P/Invoke to NativeMethods (in Platform, or add locally):

```csharp
[DllImport("user32.dll")]
internal static extern uint GetDpiForSystem();
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Fix screen dimensions passed to gaze provider factory**

Change the factory lambda (around lines 115-117):

```csharp
// Before:
return factory.Create(settings.General.GazeProviderType,
    SystemParameters.PrimaryScreenWidth,
    SystemParameters.PrimaryScreenHeight);

// After:
return factory.Create(settings.General.GazeProviderType,
    SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
    SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
```

This gives TobiiInteractionLibProvider the physical screen resolution for `CoordinateTransformAddOrUpdateDisplayArea`, ensuring Tobii outputs gaze in physical pixel coordinates.

- [ ] **Step 3: Build and run all tests**

```
dotnet build src/EyeCtrl.App/
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: initialize DpiHelper at startup, pass physical screen dims to gaze provider"
```

---

## Chunk 2: Overlay Window Positioning

All overlay windows set `Left`/`Top` from `ScreenPoint` values (physical pixels) but WPF expects DIPs. The fix is the same pattern everywhere: convert the physical gaze point to DIPs before setting `Left`/`Top`. Screen bounds from `SystemParameters` are already in DIPs and don't need conversion for WPF positioning.

### Task 3: Fix CursorCircleWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs` (line 18-19)

- [ ] **Step 1: Convert gaze to DIPs in UpdatePosition**

```csharp
// Before:
public void UpdatePosition(ScreenPoint point)
{
    Left = point.X - Width / 2;
    Top = point.Y - Height / 2;
}

// After:
public void UpdatePosition(ScreenPoint point)
{
    Left = DpiHelper.PhysicalToLogicalX(point.X) - Width / 2;
    Top = DpiHelper.PhysicalToLogicalY(point.Y) - Height / 2;
}
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs
git commit -m "fix: convert physical gaze coords to DIPs for cursor circle positioning"
```

---

### Task 4: Fix ActivationButtonWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` (lines 19-31)

`ShowAt` receives `position` (physical pixels) and `offset` (DIPs, from `CircleSize/2 + 55`). The quadrant check uses `SystemParameters` (DIPs) and the positioning uses `Width`/`Height` (DIPs). Convert position to DIPs first, then everything is in DIP space.

- [ ] **Step 1: Convert position to DIPs in ShowAt**

```csharp
public void ShowAt(ScreenPoint position, double offset)
{
    var screenW = SystemParameters.PrimaryScreenWidth;
    var screenH = SystemParameters.PrimaryScreenHeight;

    // Convert physical gaze point to DIPs for WPF positioning
    var dipX = DpiHelper.PhysicalToLogicalX(position.X);
    var dipY = DpiHelper.PhysicalToLogicalY(position.Y);

    var dx = dipX < screenW / 2 ? 1.0 : -1.0;
    var dy = dipY < screenH / 2 ? 1.0 : -1.0;

    var buttonCenterX = dipX + dx * offset / Math.Sqrt(2);
    var buttonCenterY = dipY + dy * offset / Math.Sqrt(2);

    Left = Math.Clamp(buttonCenterX - Width / 2, 0, screenW - Width);
    Top  = Math.Clamp(buttonCenterY - Height / 2, 0, screenH - Height);

    UpdateDwellProgress(0);

    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
    BeginAnimation(OpacityProperty, fadeIn);
}
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs
git commit -m "fix: DPI-correct activation button positioning"
```

---

### Task 5: Fix ActionMenuWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

Find where `Left` and `Top` are set from a center `ScreenPoint`. Convert to DIPs.

- [ ] **Step 1: Convert center to DIPs**

```csharp
// Before:
Left = center.X - Width / 2;
Top = center.Y - Height / 2;

// After:
Left = DpiHelper.PhysicalToLogicalX(center.X) - Width / 2;
Top = DpiHelper.PhysicalToLogicalY(center.Y) - Height / 2;
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs
git commit -m "fix: DPI-correct action menu positioning"
```

---

### Task 6: Fix ZoomOverlayWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`

Two fixes:
1. `UpdateCenter`: convert physical center to DIPs for `Left`/`Top`
2. `GetRealScreenPoint`: convert WPF DIP window coords back to physical for the math

- [ ] **Step 1: Fix UpdateCenter**

```csharp
// In UpdateCenter, change positioning:
Left = DpiHelper.PhysicalToLogicalX(center.X) - displayDiameter / 2.0;
Top = DpiHelper.PhysicalToLogicalY(center.Y) - displayDiameter / 2.0;
```

- [ ] **Step 2: Fix GetRealScreenPoint**

The method reads `Left + Width/2` (DIPs) and subtracts from `gazeOnScreen.X` (physical). Convert WPF coords to physical:

```csharp
public ScreenPoint GetRealScreenPoint(ScreenPoint gazeOnScreen)
{
    // Window Left/Width are in DIPs — convert to physical for consistent math
    var windowCenterX = DpiHelper.LogicalToPhysicalX(Left + Width / 2);
    var windowCenterY = DpiHelper.LogicalToPhysicalY(Top + Height / 2);

    var offsetX = gazeOnScreen.X - windowCenterX;
    var offsetY = gazeOnScreen.Y - windowCenterY;

    var realX = _originalCenter.X + offsetX / _magnification;
    var realY = _originalCenter.Y + offsetY / _magnification;

    return new ScreenPoint(realX, realY);
}
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 3: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs
git commit -m "fix: DPI-correct zoom overlay positioning and reverse mapping"
```

---

### Task 7: Fix ZoomSideButtonWindow and ZoomGazeIndicatorWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs`

**ZoomSideButtonWindow critical detail:** `ShowAt` receives physical-pixel centers, then computes a bounding box for `Width`/`Height`/`Left`/`Top` AND internal canvas positions. ALL of these are WPF DIP values. The fix must convert both centers to DIPs **at the top** of the method, then the entire bounding box + canvas layout naturally works in DIP space.

- [ ] **Step 1: Fix ZoomSideButtonWindow.ShowAt — convert to DIPs at boundary**

Replace the coordinate logic at the top of `ShowAt`:

```csharp
public void ShowAt(ScreenPoint closeCenter, ScreenPoint recaptureCenter)
{
    _buttonVisuals.Clear();
    ButtonCanvas.Children.Clear();

    // Convert physical → DIP at the boundary — all subsequent math is in DIP space
    var closeDipX = DpiHelper.PhysicalToLogicalX(closeCenter.X);
    var closeDipY = DpiHelper.PhysicalToLogicalY(closeCenter.Y);
    var recapDipX = DpiHelper.PhysicalToLogicalX(recaptureCenter.X);
    var recapDipY = DpiHelper.PhysicalToLogicalY(recaptureCenter.Y);

    var minX = Math.Min(closeDipX, recapDipX) - ButtonRadius - 10;
    var minY = Math.Min(closeDipY, recapDipY) - ButtonRadius - 10;
    var maxX = Math.Max(closeDipX, recapDipX) + ButtonRadius + 10;
    var maxY = Math.Max(closeDipY, recapDipY) + ButtonRadius + 10;

    Width = maxX - minX;
    Height = maxY - minY;
    Left = minX;
    Top = minY;

    DrawButton("zoom_close", "\u2715", closeDipX - minX, closeDipY - minY);
    DrawButton("zoom_recapture", "\u21BB", recapDipX - minX, recapDipY - minY);

    Show();
}
```

- [ ] **Step 2: Fix ZoomGazeIndicatorWindow**

Find where `Left = screenX - _maxDiameter / 2` etc. `screenX`/`screenY` are physical, `_maxDiameter` is DIPs. Convert:

```csharp
Left = DpiHelper.PhysicalToLogicalX(screenX) - _maxDiameter / 2;
Top = DpiHelper.PhysicalToLogicalY(screenY) - _maxDiameter / 2;
```

Add using to both: `using EyeCtrl.Core.Services;`

- [ ] **Step 3: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml.cs src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs
git commit -m "fix: DPI-correct zoom side button and gaze indicator positioning"
```

---

## Chunk 3: ScreenCapture, Clamping, Dwell Targets

### Task 8: Fix ScreenCaptureService screen bounds

**Files:**
- Modify: `src/EyeCtrl.App/Services/ScreenCaptureService.cs` (lines 28-31)

`CaptureRegion` receives a physical-pixel `center`. `CopyFromScreen` works in physical pixels. But `SystemParameters` returns DIPs. Convert screen bounds to physical.

- [ ] **Step 1: Use physical screen bounds**

```csharp
// Before:
var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
var screenHeight = (int)SystemParameters.PrimaryScreenHeight;

// After:
var screenWidth = (int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
var screenHeight = (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight);
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Build**

```
dotnet build src/EyeCtrl.App/
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/ScreenCaptureService.cs
git commit -m "fix: use physical screen bounds in ScreenCaptureService"
```

---

### Task 9: Fix GazeEngineAdapter action menu clamping

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` (lines 88-97)

The clamping uses `SystemParameters` (DIPs) against `point.X/Y` (physical pixels). Also, the margin is computed from DIP-conceptual constants but compared against physical coords. Fix: convert screen bounds to physical AND scale the margin to physical.

**Important:** `EffectiveRingRadius` is also sent in the `ShowActionMenuMessage` and used by `ActionMenuViewModel` for button layout. Keep the DIP value for the message. Only scale for the clamping math.

- [ ] **Step 1: Fix clamping**

```csharp
// Before:
const double buttonRadius = 50;
var circleSize = _settings.Cursor.CircleSize;
var effectiveRingRadius = circleSize / 2 + buttonRadius + 20;
var margin = effectiveRingRadius + buttonRadius;

var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
var clampedPoint = new ScreenPoint(
    Math.Clamp(point.X, margin, screenW - margin),
    Math.Clamp(point.Y, margin, screenH - margin));

// After:
const double buttonRadius = 50;
var circleSize = _settings.Cursor.CircleSize;
var effectiveRingRadius = circleSize / 2 + buttonRadius + 20;

// Scale margin to physical pixels for clamping against physical gaze coords
var physicalMargin = DpiHelper.LogicalToPhysicalX(effectiveRingRadius + buttonRadius);
var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);
var clampedPoint = new ScreenPoint(
    Math.Clamp(point.X, physicalMargin, physicalScreenW - physicalMargin),
    Math.Clamp(point.Y, physicalMargin, physicalScreenH - physicalMargin));
```

Note: `effectiveRingRadius` (DIP value) is still passed in the `ShowActionMenuMessage` for the ActionMenuViewModel layout — do NOT change the message.

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Build and run all tests**

```
dotnet build src/EyeCtrl.App/
dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
git commit -m "fix: use physical pixel space for action menu clamping"
```

---

### Task 10: Fix ViewModel dwell target positions and radii

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` (lines 53-68)
- Modify: `src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs` (lines 47-49)
- Modify: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs` (lines 51-68)
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (line 51-52)

**The problem:** DwellTarget centers and radii must be in physical pixels (to match physical-pixel gaze data). Currently:
- Toolbar/Keyboard: centers from `PointToScreen` (physical ✓), radii in DIPs (✗)
- ActionMenu: center computed as `physicalCenter + DIP_offset` (✗), radius in DIPs (✗)
- ActivationButton: center = physical (✓), radius in DIPs (✗)
- ZoomOverlay: center computed as `physicalCenter + DIP_offset` (✗), radius in DIPs (✗)

**The fix:** Scale DIP radii and offsets to physical using `DpiHelper`.

Add using to all ViewModel files: `using EyeCtrl.Core.Services;`

- [ ] **Step 1: Fix ActionMenuViewModel**

In `Receive(ShowActionMenuMessage)` (lines 53-68):

```csharp
// Scale ring radius and button radius to physical pixels
var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(ButtonRadius);

var angleStep = 360.0 / allItems.Count;
for (int i = 0; i < allItems.Count; i++)
{
    var (id, action, symbol, isBack) = allItems[i];
    var angleRad = (angleStep * i - 90) * Math.PI / 180;
    // Center is physical, offset is now physical → result is physical ✓
    var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
    var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
    var position = new ScreenPoint(bx, by);

    var itemVm = new ActionMenuItemViewModel(id, action, symbol, position, isBack);
    Items.Add(itemVm);
    _itemLookup[id] = itemVm;

    _dwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
}
```

- [ ] **Step 2: Fix ActivationButtonViewModel**

In `Receive(ShowActivationButtonMessage)` (lines 46-49):

```csharp
_dwellDetector.ClearTargets();
_dwellDetector.AddTarget(new DwellTarget(TargetId,
    message.Position,
    DpiHelper.LogicalToPhysicalX(ButtonRadius),   // Scale radius to physical
    _dwellSettings.ActivationButtonDwellMs));
```

- [ ] **Step 3: Fix ZoomOverlayViewModel**

In `Receive(StartZoomMessage)` (lines 51-68):

```csharp
// Scale DIP offsets to physical for computing positions relative to physical center
var physicalDisplayRadius = DpiHelper.LogicalToPhysicalX(message.Radius * message.Magnification);
var physicalGap = DpiHelper.LogicalToPhysicalX(SideButtonGap);
var physicalBtnRadius = DpiHelper.LogicalToPhysicalX(SideButtonRadius);

var buttonX = message.Center.X + physicalDisplayRadius + physicalGap + physicalBtnRadius;
var topButtonY = message.Center.Y - physicalBtnRadius - physicalGap / 2;
var bottomButtonY = message.Center.Y + physicalBtnRadius + physicalGap / 2;

CloseButtonCenter = new ScreenPoint(buttonX, topButtonY);
RecaptureButtonCenter = new ScreenPoint(buttonX, bottomButtonY);

// ...

_dwellDetector.AddTarget(new DwellTarget(
    CloseTargetId, CloseButtonCenter, physicalBtnRadius, SideButtonDwellMs));
_dwellDetector.AddTarget(new DwellTarget(
    RecaptureTargetId, RecaptureButtonCenter, physicalBtnRadius, SideButtonDwellMs));
```

- [ ] **Step 4: Fix ToolbarViewModel**

In the constructor (line 51-52):

```csharp
_dwellDetector.AddTarget(new DwellTarget(id,
    new ScreenPoint(0, 0),
    DpiHelper.LogicalToPhysicalX(toolbarSettings.Size / 2),  // Scale radius to physical
    dwellSettings.ToolbarButtonDwellMs));
```

- [ ] **Step 5: Build and run all tests**

```
dotnet build
dotnet test
```

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs src/EyeCtrl.ViewModels/ToolbarViewModel.cs
git commit -m "fix: scale dwell target radii and offsets to physical pixels for DPI correctness"
```

---

### Task 11: Fix KeyboardWindow and SettingsWindow dwell target radii

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (dwell target creation sites)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (dwell target creation sites)

Same pattern: wherever a `DwellTarget` is created with a hardcoded radius (40, 20, etc.), scale it to physical.

- [ ] **Step 1: Fix KeyboardWindow dwell radii**

Search for `new DwellTarget(` in KeyboardWindow.xaml.cs. For each, wrap the radius with `DpiHelper.LogicalToPhysicalX()`:

```csharp
// Example — find each occurrence and change the radius argument:
// Before: new DwellTarget(id, position, 40, dwellMs)
// After:  new DwellTarget(id, position, DpiHelper.LogicalToPhysicalX(40), dwellMs)
```

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 2: Fix SettingsWindow dwell radii**

Same pattern — search for `new DwellTarget(` in SettingsWindow.xaml.cs and wrap radii.

Add using: `using EyeCtrl.Core.Services;`

- [ ] **Step 3: Build and run all tests**

```
dotnet build
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "fix: scale keyboard and settings dwell target radii to physical pixels"
```

---

## Chunk 4: Verification

### Task 12: Manual Verification

- [ ] **Step 1: Build and publish**

```
dotnet publish src/EyeCtrl.App/ -r win-x64 --self-contained true -p:PublishSingleFile=true
```

- [ ] **Step 2: Test at 200% DPI with mouse emulation**

1. Launch with mouse emulation mode
2. Verify:
   - **Cursor circle** follows the mouse accurately (no offset toward any corner)
   - **Activation button** appears at fixation point (correct quadrant, correct offset)
   - **Action menu** opens centered on fixation (buttons evenly distributed)
   - **Dwell targets** activate when cursor is over the visual button (not offset)
   - **Zoom mode**: overlay appears at gaze, side buttons are positioned correctly
   - **Toolbar** buttons are dwelled correctly
   - **Keyboard** keys register dwell correctly
   - **Click actions** land where the gaze cursor points

- [ ] **Step 3: Test at 100% DPI (regression)**

Set display scaling to 100% and repeat. `DpiHelper.ScaleX/Y` = 1.0, so all behavior must be identical to before.

- [ ] **Step 4: Commit any fixes**

```bash
git add -A
git commit -m "fix: DPI awareness tweaks from manual testing"
```

---

## Files Changed Summary

| File | Change |
|------|--------|
| `src/EyeCtrl.Core/Services/DpiHelper.cs` | **New** — scale factor + conversion methods |
| `tests/EyeCtrl.Core.Tests/Services/DpiHelperTests.cs` | **New** — unit tests |
| `src/EyeCtrl.App/App.xaml.cs` | Initialize DpiHelper from splash/P/Invoke, physical dims to gaze factory |
| `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs` | Physical→DIPs in UpdatePosition |
| `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` | Physical→DIPs in ShowAt |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Physical→DIPs in positioning |
| `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs` | Physical→DIPs + fix GetRealScreenPoint |
| `src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml.cs` | Physical→DIPs in positioning |
| `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs` | Physical→DIPs in positioning |
| `src/EyeCtrl.App/Services/ScreenCaptureService.cs` | Physical screen bounds for clamping |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | Physical screen bounds + margin for clamping |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Scale ring offset + dwell radius to physical |
| `src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs` | Scale dwell radius to physical |
| `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs` | Scale offsets + dwell radii to physical |
| `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` | Scale dwell radius to physical |
| `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` | Scale dwell radii to physical |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Scale dwell radii to physical |

## What Stays Unchanged

- **EyeCtrl.Core processing** (filters, dwell detector, fixation) — operates on physical pixel distances, untouched
- **Win32InputSimulator** — `GetSystemMetrics` and `SendInput` both physical, already consistent
- **MouseGazeProvider** — `GetCursorPos` returns physical in Per-Monitor V2, no change
- **Toolbar/Keyboard docking** (`SystemParameters.WorkArea` → `Window.Left/Top`) — both DIPs, already consistent
- **DwellTarget center positions from PointToScreen** (Toolbar, Keyboard) — returns physical, matches gaze
- **All XAML sizes** — WPF auto-scales DIPs to physical; a 40-DIP circle renders as 80 physical pixels at 200%

## Known Limitation

- **Zoom magnification accuracy at non-100% DPI:** The `CircleRadiusPixels` setting is used both as DIP (window sizing) and passed to `CopyFromScreen` (physical pixels). At 200%, the effective magnification may differ from the configured value. This is a separate issue from coordinate correctness and can be addressed in a follow-up.
