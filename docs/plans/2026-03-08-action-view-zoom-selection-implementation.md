# Action View & Zoom Selection Mode Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign Action Menu into an Action View with screen preview, rename Zoom Mode to Zoom Selection Mode, and add side buttons (close/re-capture) to the Zoom View.

**Architecture:** Evolve existing `ActionMenuWindow` to include a GDI screen capture circle in its center. Add `DwellDetector`-driven side buttons to `ZoomOverlayWindow`. Change `OnZoomBack()` from `ZoomSelectionActionMenuVisible` to return to `ZoomSelectionActive` instead of `FreeLook`. Hide cursor circle during Action View and Zoom Selection states.

**Tech Stack:** C# / .NET 8 / WPF / CommunityToolkit.Mvvm / GDI screen capture / xUnit

**Design doc:** `docs/plans/2026-03-08-action-view-zoom-selection-design.md`

---

### Task 1: Rename InteractionState enum values

Pure rename — no behavior changes. Updates the enum and all references.

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionState.cs`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`
- Modify: `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs`
- Modify: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

**Step 1: Update the enum**

In `src/EyeCtrl.Core/Models/InteractionState.cs`, rename:
- `ZoomActive` → `ZoomSelectionActive`
- `ZoomFixation` → `ZoomSelectionFixation`
- `ZoomActionMenuVisible` → `ZoomSelectionActionMenuVisible`

Also remove `ActivationButtonVisible` (dead value, never used in transitions).

```csharp
namespace EyeCtrl.Core.Models;
public enum InteractionState { FreeLook, Fixation, ActionMenuVisible, ZoomSelectionActive, ZoomSelectionFixation, ZoomSelectionActionMenuVisible, Paused }
```

**Step 2: Fix all compilation errors**

Find-and-replace across the solution:
- `InteractionState.ZoomActive` → `InteractionState.ZoomSelectionActive`
- `InteractionState.ZoomFixation` → `InteractionState.ZoomSelectionFixation`
- `InteractionState.ZoomActionMenuVisible` → `InteractionState.ZoomSelectionActionMenuVisible`
- `InteractionState.ActivationButtonVisible` → remove any references (should be none in logic, only in enum)

Files affected:
- `InteractionManager.cs`: `OnActivationButtonDwelled` (line ~53), `OnZoomFixation` (line ~61), `OnActionSelected` (lines ~70-76), `OnZoomBack` (lines ~87-95)
- `GazeEngineAdapter.cs`: `OnShowActionMenu` showBack check (~line 75), `OnActionRequested` isFromZoom check (~line 122)
- `CursorCircleViewModel.cs`: no changes yet (only checks `Paused`)
- `ZoomOverlayViewModel.cs`: no direct enum references (driven by messages)

**Step 3: Run all tests**

Run: `dotnet test`
Expected: All 30 tests pass (pure rename, no behavior change).

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: rename Zoom states to ZoomSelection, remove dead ActivationButtonVisible"
```

---

### Task 2: Add PositionIndicatorStyle enum and setting

**Files:**
- Create: `src/EyeCtrl.Core/Models/PositionIndicatorStyle.cs`
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs`

**Step 1: Write the failing test**

In `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs`, add:

```csharp
[Fact]
public void CursorSettings_PositionIndicatorStyle_DefaultIsCrosshair()
{
    var settings = new CursorSettings();
    Assert.Equal(PositionIndicatorStyle.Crosshair, settings.PositionIndicatorStyle);
}

[Fact]
public void CursorSettings_PositionIndicatorStyle_RoundTrips()
{
    var settings = new CursorSettings { PositionIndicatorStyle = PositionIndicatorStyle.Pin };
    var json = JsonSerializer.Serialize(settings);
    var deserialized = JsonSerializer.Deserialize<CursorSettings>(json)!;
    Assert.Equal(PositionIndicatorStyle.Pin, deserialized.PositionIndicatorStyle);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "PositionIndicatorStyle"`
Expected: FAIL — `PositionIndicatorStyle` type not found.

**Step 3: Create the enum**

Create `src/EyeCtrl.Core/Models/PositionIndicatorStyle.cs`:

```csharp
namespace EyeCtrl.Core.Models;

public enum PositionIndicatorStyle
{
    Crosshair,
    Pin
}
```

**Step 4: Add property to CursorSettings**

In `src/EyeCtrl.Core/Settings/CursorSettings.cs`, add after `_circleHighContrast`:

```csharp
[ObservableProperty] private PositionIndicatorStyle _positionIndicatorStyle = PositionIndicatorStyle.Crosshair;
```

Add `using EyeCtrl.Core.Models;` if not already present.

**Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "PositionIndicatorStyle"`
Expected: PASS

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: add PositionIndicatorStyle setting (Crosshair/Pin)"
```

---

### Task 3: Hide cursor circle during Action View and Zoom Selection

**Files:**
- Modify: `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/CursorCircleViewModelTests.cs`

**Step 1: Write the failing tests**

In `tests/EyeCtrl.ViewModels.Tests/CursorCircleViewModelTests.cs`, add:

```csharp
[Theory]
[InlineData(InteractionState.ActionMenuVisible)]
[InlineData(InteractionState.ZoomSelectionActive)]
[InlineData(InteractionState.ZoomSelectionActionMenuVisible)]
public void HidesDuringOverlayStates(InteractionState state)
{
    var settings = new CursorSettings();
    var messenger = new WeakReferenceMessenger();
    var vm = new CursorCircleViewModel(settings, messenger);

    messenger.Send(new StateChangedMessage(state));

    Assert.False(vm.IsVisible);
}

[Fact]
public void ShowsAgain_WhenReturningToFreeLook_FromActionMenu()
{
    var settings = new CursorSettings();
    var messenger = new WeakReferenceMessenger();
    var vm = new CursorCircleViewModel(settings, messenger);

    messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
    Assert.False(vm.IsVisible);

    messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
    Assert.True(vm.IsVisible);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "HidesDuringOverlayStates|ShowsAgain_WhenReturningToFreeLook_FromActionMenu"`
Expected: FAIL — cursor stays visible during ActionMenuVisible / ZoomSelectionActive / ZoomSelectionActionMenuVisible.

**Step 3: Update CursorCircleViewModel**

In `src/EyeCtrl.ViewModels/CursorCircleViewModel.cs`, change the `Receive(StateChangedMessage)` method. Currently it's approximately:

```csharp
public void Receive(StateChangedMessage message)
{
    IsVisible = message.NewState != InteractionState.Paused;
}
```

Replace with:

```csharp
public void Receive(StateChangedMessage message)
{
    IsVisible = message.NewState is InteractionState.FreeLook or InteractionState.Fixation;
}
```

This hides the cursor for all overlay states (ActionMenuVisible, ZoomSelectionActive, ZoomSelectionActionMenuVisible, Paused) and only shows it during FreeLook and Fixation.

**Step 4: Run all tests**

Run: `dotnet test`
Expected: All tests pass. Check that the existing `HidesWhenPaused_ShowsWhenResumed` test still passes.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: hide cursor circle during Action View and Zoom Selection states"
```

---

### Task 4: Change ZoomBack to return to ZoomSelectionActive from Action View

Currently `OnZoomBack()` always goes to FreeLook. The new behavior:
- From `ZoomSelectionActionMenuVisible` → `ZoomSelectionActive` (back to zoom view)
- From `ZoomSelectionActive` → `FreeLook` (close button)

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

**Step 1: Write the failing test**

In `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`, add:

```csharp
[Fact]
public void ZoomBack_FromActionMenu_ReturnsToZoomSelectionActive()
{
    var im = new InteractionManager { ZoomEnabled = true };
    var fixPoint = new ScreenPoint(500, 500);
    var zoomPoint = new ScreenPoint(510, 510);
    bool startZoomFired = false;
    bool hideActionMenuFired = false;
    bool endZoomFired = false;

    im.StartZoom += _ => startZoomFired = true;
    im.HideActionMenu += () => hideActionMenuFired = true;
    im.EndZoom += () => endZoomFired = true;

    im.OnFixationStarted(fixPoint);
    im.OnActivationButtonDwelled();
    Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);

    im.OnZoomFixation(zoomPoint);
    Assert.Equal(InteractionState.ZoomSelectionActionMenuVisible, im.CurrentState);

    // Back from action menu → returns to zoom, NOT FreeLook
    startZoomFired = false;
    im.OnZoomBack();
    Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);
    Assert.True(hideActionMenuFired);
    Assert.True(startZoomFired); // re-fires StartZoom to re-show zoom overlay
    Assert.False(endZoomFired);  // zoom NOT ended
}

[Fact]
public void ZoomClose_FromZoomView_ReturnsToFreeLook()
{
    var im = new InteractionManager { ZoomEnabled = true };
    var fixPoint = new ScreenPoint(500, 500);
    bool endZoomFired = false;

    im.EndZoom += () => endZoomFired = true;

    im.OnFixationStarted(fixPoint);
    im.OnActivationButtonDwelled();
    Assert.Equal(InteractionState.ZoomSelectionActive, im.CurrentState);

    im.OnZoomBack(); // close from zoom view → FreeLook
    Assert.Equal(InteractionState.FreeLook, im.CurrentState);
    Assert.True(endZoomFired);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ZoomBack_FromActionMenu_ReturnsToZoomSelectionActive|ZoomClose_FromZoomView_ReturnsToFreeLook"`
Expected: FAIL — `ZoomBack_FromActionMenu_ReturnsToZoomSelectionActive` fails because current `OnZoomBack()` always goes to FreeLook.

**Step 3: Update InteractionManager.OnZoomBack()**

Replace the current `OnZoomBack()` method. The current implementation is approximately:

```csharp
public void OnZoomBack()
{
    if (CurrentState is not (InteractionState.ZoomSelectionActive or InteractionState.ZoomSelectionActionMenuVisible))
        return;
    HideActionMenu?.Invoke();
    EndZoom?.Invoke();
    ResetToFreeLook();
}
```

Replace with:

```csharp
public void OnZoomBack()
{
    if (CurrentState == InteractionState.ZoomSelectionActionMenuVisible)
    {
        // Back from action view → return to zoom selection
        HideActionMenu?.Invoke();
        SetState(InteractionState.ZoomSelectionActive);
        StartZoom?.Invoke(FixationPoint!);
        ZoomFixationPoint = null;
    }
    else if (CurrentState == InteractionState.ZoomSelectionActive)
    {
        // Close from zoom view → return to free look
        EndZoom?.Invoke();
        ResetToFreeLook();
    }
}
```

**Step 4: Update existing test that may break**

The existing `ZoomFlow_BackButton_ReturnsToFreeLook` test likely expects `OnZoomBack()` from `ZoomSelectionActionMenuVisible` → `FreeLook`. Update it to reflect the new two-step behavior:

```csharp
[Fact]
public void ZoomFlow_BackButton_FromZoomView_ReturnsToFreeLook()
{
    var im = new InteractionManager { ZoomEnabled = true };
    im.OnFixationStarted(new ScreenPoint(500, 500));
    im.OnActivationButtonDwelled();
    // Now in ZoomSelectionActive — close goes to FreeLook
    im.OnZoomBack();
    Assert.Equal(InteractionState.FreeLook, im.CurrentState);
}
```

**Step 5: Run all tests**

Run: `dotnet test`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat: ZoomBack from action menu returns to zoom selection, not FreeLook"
```

---

### Task 5: Extract screen capture utility

The GDI screen capture code currently lives in `ZoomOverlayWindow.xaml.cs`. Extract it so both `ZoomOverlayWindow` and `ActionMenuWindow` can use it.

**Files:**
- Create: `src/EyeCtrl.App/Services/ScreenCaptureService.cs`
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`

**Step 1: Create ScreenCaptureService**

Create `src/EyeCtrl.App/Services/ScreenCaptureService.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;

public static class ScreenCaptureService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Captures a square region of the screen centered on the given point.
    /// Returns a BitmapSource suitable for WPF ImageBrush.
    /// </summary>
    public static BitmapSource? CaptureRegion(ScreenPoint center, int radius)
    {
        var captureSize = radius * 2;
        var screenW = (int)SystemParameters.PrimaryScreenWidth;
        var screenH = (int)SystemParameters.PrimaryScreenHeight;

        var left = Math.Clamp((int)center.X - radius, 0, screenW - 1);
        var top = Math.Clamp((int)center.Y - radius, 0, screenH - 1);
        var right = Math.Clamp((int)center.X + radius, 1, screenW);
        var bottom = Math.Clamp((int)center.Y + radius, 1, screenH);
        var width = right - left;
        var height = bottom - top;

        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
```

**Step 2: Refactor ZoomOverlayWindow to use ScreenCaptureService**

In `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`:
- Remove the `[DllImport("gdi32.dll")]` and `DeleteObject` declaration
- Replace the body of `CaptureAndDisplay(center, radius)` with:

```csharp
private void CaptureAndDisplay(ScreenPoint center, int radius)
{
    var source = ScreenCaptureService.CaptureRegion(center, radius);
    if (source != null)
        ZoomBrush.ImageSource = source;
}
```

**Step 3: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests pass. No behavior change — just extraction.

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: extract ScreenCaptureService from ZoomOverlayWindow"
```

---

### Task 6: Add screen capture preview to ActionMenuWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

**Step 1: Add screen capture circle to ActionMenuWindow.Show()**

The `Show()` method signature needs an additional parameter for the circle size. Update it:

```csharp
public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize)
```

At the beginning of `Show()`, after clearing the canvas and before creating buttons, add the screen capture preview:

```csharp
// Screen capture preview in the center
var previewRadius = (int)(previewCircleSize / 2);
var capturedImage = ScreenCaptureService.CaptureRegion(center, previewRadius);
if (capturedImage != null)
{
    var canvasCenter = new System.Windows.Point(Width / 2, Height / 2);

    // Clipped ellipse filled with screen capture
    var previewEllipse = new Ellipse
    {
        Width = previewCircleSize,
        Height = previewCircleSize,
        Fill = new ImageBrush(capturedImage) { Stretch = Stretch.Fill },
        Stroke = new SolidColorBrush((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(borderColor)),
        StrokeThickness = 2
    };
    Canvas.SetLeft(previewEllipse, canvasCenter.X - previewCircleSize / 2);
    Canvas.SetTop(previewEllipse, canvasCenter.Y - previewCircleSize / 2);
    MenuCanvas.Children.Add(previewEllipse);
}
```

The `borderColor` parameter should come from CursorSettings.CircleColor. Add it to the method signature or pass it through the message. Simplest: add `string borderColor` parameter.

Updated full signature:

```csharp
public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor)
```

**Step 2: Update App.xaml.cs call site**

In `App.xaml.cs`, the action menu bridge (~line 283–301) calls `actionMenu.Show(...)`. Update to pass `settings.Cursor.CircleSize` and `settings.Cursor.CircleColor`:

```csharp
actionMenu.Show(
    actionMenuVM.Center,
    actionMenuVM.Items.Where(i => i.Action.HasValue).Select(i => i.Action!.Value).ToList(),
    actionMenuVM.ShowBack,
    dwellTimeMs,
    actionMenuVM.EffectiveRingRadius,
    settings.Cursor.CircleSize,
    settings.Cursor.CircleColor);
```

**Step 3: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests pass.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: add screen capture preview circle to Action View center"
```

---

### Task 7: Add position indicator (crosshair / pin) to Action View

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

**Step 1: Add indicator drawing after the preview ellipse**

In `ActionMenuWindow.Show()`, after adding the preview ellipse to the canvas, draw the position indicator:

```csharp
// Position indicator at center of preview
var indicatorSize = Math.Min(previewCircleSize * 0.15, 30); // scale with circle, max 30px
if (positionIndicatorStyle == PositionIndicatorStyle.Crosshair)
{
    // Horizontal line
    var hLine = new Line
    {
        X1 = canvasCenter.X - indicatorSize, Y1 = canvasCenter.Y,
        X2 = canvasCenter.X + indicatorSize, Y2 = canvasCenter.Y,
        Stroke = Brushes.Red, StrokeThickness = 1.5
    };
    // Vertical line
    var vLine = new Line
    {
        X1 = canvasCenter.X, Y1 = canvasCenter.Y - indicatorSize,
        X2 = canvasCenter.X, Y2 = canvasCenter.Y + indicatorSize,
        Stroke = Brushes.Red, StrokeThickness = 1.5
    };
    MenuCanvas.Children.Add(hLine);
    MenuCanvas.Children.Add(vLine);
}
else // Pin
{
    // Pin icon: small circle with a line pointing down
    var pinHead = new Ellipse
    {
        Width = indicatorSize, Height = indicatorSize,
        Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 1
    };
    Canvas.SetLeft(pinHead, canvasCenter.X - indicatorSize / 2);
    Canvas.SetTop(pinHead, canvasCenter.Y - indicatorSize);
    var pinLine = new Line
    {
        X1 = canvasCenter.X, Y1 = canvasCenter.Y - indicatorSize / 2,
        X2 = canvasCenter.X, Y2 = canvasCenter.Y,
        Stroke = Brushes.Red, StrokeThickness = 2
    };
    MenuCanvas.Children.Add(pinHead);
    MenuCanvas.Children.Add(pinLine);
}
```

Add `PositionIndicatorStyle positionIndicatorStyle` to the method signature.

Updated full signature:

```csharp
public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle)
```

**Step 2: Update App.xaml.cs call site**

Pass `settings.Cursor.PositionIndicatorStyle` to the `Show()` call.

**Step 3: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat: add configurable position indicator (crosshair/pin) to Action View"
```

---

### Task 8: Compute EffectiveRingRadius dynamically

Replace the hardcoded `DefaultRingRadius = 160` with a computed value based on `CircleSize`.

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`

**Step 1: Write the failing test**

In `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`, update or add:

```csharp
[Fact]
public void EffectiveRingRadius_IsComputed_FromCircleSize()
{
    var messenger = new WeakReferenceMessenger();
    var vm = new ActionMenuViewModel(messenger);
    var actions = new List<InteractionAction> { InteractionAction.LeftClick };

    // CircleSize=250 → 250/2 + 50 + 20 = 195
    messenger.Send(new ShowActionMenuMessage(
        new ScreenPoint(500, 500), actions, false, 600, CircleSize: 250));

    Assert.Equal(195, vm.EffectiveRingRadius);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "EffectiveRingRadius_IsComputed_FromCircleSize"`
Expected: FAIL — `ShowActionMenuMessage` doesn't have `CircleSize` parameter yet.

**Step 3: Add CircleSize to ShowActionMenuMessage**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, update:

```csharp
public record ShowActionMenuMessage(
    ScreenPoint Center,
    IReadOnlyList<InteractionAction> Actions,
    bool ShowBack,
    int DwellTimeMs,
    int ZoomDisplayRadius = 0,
    double CircleSize = 250);
```

**Step 4: Update ActionMenuViewModel.Receive(ShowActionMenuMessage)**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, replace the `EffectiveRingRadius` calculation in `Receive(ShowActionMenuMessage)`:

```csharp
EffectiveRingRadius = message.CircleSize / 2 + ButtonRadius + ButtonOuterGap;
```

Remove the `DefaultRingRadius` constant (no longer needed). The zoom-vs-non-zoom distinction for ring radius is eliminated — both use the same formula based on CircleSize.

**Step 5: Update GazeEngineAdapter to pass CircleSize**

In `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`, in the `OnShowActionMenu` handler, pass `CircleSize` from the settings. The adapter constructor already receives `EyeCtrlSettings` — use `_settings.Cursor.CircleSize`:

```csharp
_messenger.Send(new ShowActionMenuMessage(
    clampedPoint, actions, showBack, dwellMs,
    zoomDisplayRadius, _settings.Cursor.CircleSize));
```

Also update the clamping margin calculation to use the new formula:
```csharp
var effectiveRingRadius = _settings.Cursor.CircleSize / 2 + buttonRadius + 20;
var margin = effectiveRingRadius + buttonRadius;
```

**Step 6: Update existing tests**

Update `EffectiveRingRadius_IsDefault_WhenNotZoom` test to expect 195 (not 160):

```csharp
[Fact]
public void EffectiveRingRadius_IsDefault_WhenNotZoom()
{
    // With default CircleSize=250: 250/2 + 50 + 20 = 195
    // ... send ShowActionMenuMessage with CircleSize=250
    Assert.Equal(195, vm.EffectiveRingRadius);
}
```

Update `EffectiveRingRadius_IsOutsideZoomCircle_WhenZoom` — this test used `ZoomDisplayRadius=300` to compute `300+50+20=370`. With the new formula, zoom uses the same `CircleSize` formula. Update the expected value accordingly.

**Step 7: Run all tests**

Run: `dotnet test`
Expected: All pass.

**Step 8: Commit**

```bash
git add -A
git commit -m "feat: compute EffectiveRingRadius from CircleSize, remove hardcoded 160"
```

---

### Task 9: Add side buttons to ZoomOverlayWindow

Add Close (✕) and Re-capture (↻) buttons on the right side of the zoom circle.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml`
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`
- Modify: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs`
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs`

**Step 1: Write the failing tests**

In `tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs`, add:

```csharp
[Fact]
public void CloseButton_Dwell_SendsZoomCloseMessage()
{
    var messenger = new WeakReferenceMessenger();
    var vm = new ZoomOverlayViewModel(messenger);
    bool closeSent = false;
    messenger.Register<ZoomCloseMessage>(this, (_, _) => closeSent = true);

    messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));

    // Simulate gaze on close button position for enough time
    var closePos = vm.CloseButtonCenter;
    for (int i = 0; i < 25; i++) // 25 × 33ms ≈ 825ms > 600ms dwell
        messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

    Assert.True(closeSent);
}

[Fact]
public void RecaptureButton_Dwell_SendsZoomRecaptureMessage()
{
    var messenger = new WeakReferenceMessenger();
    var vm = new ZoomOverlayViewModel(messenger);
    bool recaptureSent = false;
    messenger.Register<ZoomRecaptureMessage>(this, (_, _) => recaptureSent = true);

    messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));

    var recapturePos = vm.RecaptureButtonCenter;
    for (int i = 0; i < 25; i++)
        messenger.Send(new GazeCursorMovedMessage(recapturePos, i * 33000));

    Assert.True(recaptureSent);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "CloseButton_Dwell|RecaptureButton_Dwell"`
Expected: FAIL — `ZoomCloseMessage`, `ZoomRecaptureMessage`, `CloseButtonCenter`, `RecaptureButtonCenter` don't exist.

**Step 3: Add new messages**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, add:

```csharp
public record ZoomCloseMessage;
public record ZoomRecaptureMessage;
```

**Step 4: Add DwellDetector and button positions to ZoomOverlayViewModel**

Transform `ZoomOverlayViewModel` from a pure state container into an active participant (like `ActivationButtonViewModel`). Add:

```csharp
public partial class ZoomOverlayViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<GazeCursorMovedMessage>  // NEW
{
    private const double SideButtonRadius = 50;
    private const double SideButtonGap = 20;
    private const string CloseTargetId = "zoom_close";
    private const string RecaptureTargetId = "zoom_recapture";

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector = new();

    // ... existing properties ...

    [ObservableProperty] private ScreenPoint _closeButtonCenter = new(0, 0);
    [ObservableProperty] private ScreenPoint _recaptureButtonCenter = new(0, 0);
    [ObservableProperty] private double _closeButtonProgress;
    [ObservableProperty] private double _recaptureButtonProgress;

    public ZoomOverlayViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;

        var displayRadius = message.Radius * message.Magnification;
        var buttonX = message.Center.X + displayRadius + SideButtonGap + SideButtonRadius;
        var topButtonY = message.Center.Y - SideButtonRadius - SideButtonGap / 2;
        var bottomButtonY = message.Center.Y + SideButtonRadius + SideButtonGap / 2;

        CloseButtonCenter = new ScreenPoint(buttonX, topButtonY);
        RecaptureButtonCenter = new ScreenPoint(buttonX, bottomButtonY);

        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, CloseButtonCenter, SideButtonRadius, 600));
        _dwellDetector.AddTarget(new DwellTarget(
            RecaptureTargetId, RecaptureButtonCenter, SideButtonRadius, 600));

        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
        _dwellDetector.ClearTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == CloseTargetId)
            _messenger.Send(new ZoomCloseMessage());
        else if (targetId == RecaptureTargetId)
            _messenger.Send(new ZoomRecaptureMessage());
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == CloseTargetId)
            CloseButtonProgress = progress;
        else if (targetId == RecaptureTargetId)
            RecaptureButtonProgress = progress;
    }
}
```

**Step 5: Run tests**

Run: `dotnet test`
Expected: All pass.

**Step 6: Add side button visuals to ZoomOverlayWindow**

In `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`, add a method to create and position the side buttons. These are separate top-level elements or a small side panel. Since the zoom window itself is click-through, the side buttons should be drawn as part of the window but the dwell is driven by the VM (same pattern as ActionMenuWindow — buttons are visual only, dwell is in VM via `GazeCursorMovedMessage`).

Add to `ZoomOverlayWindow.xaml.cs`:

```csharp
private Canvas? _sideButtonCanvas;
private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc)> _sideButtons = new();

public void ShowSideButtons(ScreenPoint closeCenter, ScreenPoint recaptureCenter)
{
    // Create side button canvas if needed, positioned absolutely
    // Draw two buttons: close (✕) and recapture (↻)
    // Same visual style as action menu buttons: dark ellipse + progress arc + symbol
}

public void UpdateSideButtonProgress(string buttonId, double progress)
{
    // Same arc update logic as ActionMenuWindow.UpdateItemProgress
}

public void HideSideButtons()
{
    // Remove side button visuals
}
```

Since the zoom window is sized to the zoom circle, the side buttons need to be either:
- A separate small overlay window (simpler positioning), or
- The zoom window is widened to accommodate buttons on the right

Recommended: Widen the zoom window. In `UpdateCenter()`, increase `Width` by `gap + buttonDiameter` on the right side, and offset the zoom ellipse to the left within the window. This keeps everything in one window.

Alternatively, create a small `ZoomSideButtonWindow` overlay. Given the existing pattern of one-overlay-per-concern, this is cleaner.

**Decision: Create a small `ZoomSideButtonWindow`** that the zoom VM positions and shows/hides alongside the zoom overlay. Wire it in `App.xaml.cs` alongside the zoom bridge.

Create `src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml`:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.ZoomSideButtonWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="120" Height="240">
    <Canvas x:Name="ButtonCanvas"/>
</overlays:OverlayWindow>
```

Code-behind creates two buttons procedurally (same pattern as ActionMenuWindow button creation).

**Step 7: Wire in App.xaml.cs**

In the zoom overlay bridge section (~lines 232–281), add:
- Create `ZoomSideButtonWindow`
- On `zoomVM.IsVisible` → show/hide side button window
- On `zoomVM.CloseButtonCenter` / `RecaptureButtonCenter` changed → position side button window
- On `zoomVM.CloseButtonProgress` / `RecaptureButtonProgress` changed → update progress arcs
- Register `ZoomCloseMessage` → `im.OnZoomBack()` (close from zoom view)
- Register `ZoomRecaptureMessage` → `zoomOverlay.Recapture()` (call CaptureAndDisplay again)

Add a `Recapture()` method to `ZoomOverlayWindow`:

```csharp
public void Recapture()
{
    CaptureAndDisplay(_originalCenter, (int)(Width / _magnification / 2));
}
```

**Step 8: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

**Step 9: Commit**

```bash
git add -A
git commit -m "feat: add close and re-capture side buttons to Zoom Selection view"
```

---

### Task 10: Wire ZoomClose and ZoomRecapture in GazeEngineAdapter

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`

**Step 1: Register new messages**

In the `GazeEngineAdapter` constructor, add message registrations:

```csharp
messenger.Register<ZoomCloseMessage>(this, (_, _) =>
{
    im.OnZoomBack(); // from ZoomSelectionActive → FreeLook
});

messenger.Register<ZoomRecaptureMessage>(this, (_, _) =>
{
    // No state change needed — just a visual refresh
    // The ZoomOverlayWindow.Recapture() is handled in App.xaml.cs
});
```

Note: `ZoomRecaptureMessage` doesn't need to touch `InteractionManager` — it's purely visual. The `App.xaml.cs` wiring handles calling `zoomOverlay.Recapture()`.

**Step 2: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

**Step 3: Commit**

```bash
git add -A
git commit -m "feat: wire ZoomClose message to InteractionManager in GazeEngineAdapter"
```

---

### Task 11: Update App.xaml.cs wiring for complete flow

Final integration task — update the central wiring to support the full new flow.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Cursor circle show/hide**

The `CursorCircleViewModel` now handles its own visibility via `StateChangedMessage` (Task 3). Verify the existing bridge in App.xaml.cs already handles `IsVisible` → `Show()`/`Hide()`. No changes needed here — already wired at lines 193–206.

**Step 2: Action View bridge — pass new parameters**

Update the action menu bridge (~lines 283–301) to pass `CircleSize`, `CircleColor`, and `PositionIndicatorStyle` to `actionMenu.Show()`:

```csharp
if (actionMenuVM.IsVisible)
{
    var actions = actionMenuVM.Items
        .Where(i => i.Action.HasValue)
        .Select(i => i.Action!.Value).ToList();
    actionMenu.Show(
        actionMenuVM.Center, actions, actionMenuVM.ShowBack,
        /* dwellTimeMs */ ...,
        actionMenuVM.EffectiveRingRadius,
        settings.Cursor.CircleSize,
        settings.Cursor.CircleColor,
        settings.Cursor.PositionIndicatorStyle);
}
```

**Step 3: Zoom side button window creation and wiring**

After creating `zoomOverlay` (~line 170–183), create the side button window:

```csharp
var zoomSideButtons = new ZoomSideButtonWindow();
```

Wire `zoomVM.PropertyChanged` to show/hide/update side buttons:

```csharp
zoomVM.PropertyChanged += (_, e) =>
{
    switch (e.PropertyName)
    {
        case nameof(ZoomOverlayViewModel.IsVisible):
            if (zoomVM.IsVisible)
                zoomSideButtons.Show();
            else
                zoomSideButtons.Hide();
            break;
        case nameof(ZoomOverlayViewModel.CloseButtonCenter):
        case nameof(ZoomOverlayViewModel.RecaptureButtonCenter):
            zoomSideButtons.UpdatePositions(zoomVM.CloseButtonCenter, zoomVM.RecaptureButtonCenter);
            break;
        case nameof(ZoomOverlayViewModel.CloseButtonProgress):
            zoomSideButtons.UpdateProgress("zoom_close", zoomVM.CloseButtonProgress);
            break;
        case nameof(ZoomOverlayViewModel.RecaptureButtonProgress):
            zoomSideButtons.UpdateProgress("zoom_recapture", zoomVM.RecaptureButtonProgress);
            break;
    }
};
```

**Step 4: Recapture wiring**

Register `ZoomRecaptureMessage` in App.xaml.cs:

```csharp
messenger.Register<ZoomRecaptureMessage>(this, (_, _) =>
{
    Dispatcher.BeginInvoke(() => zoomOverlay.Recapture());
});
```

**Step 5: Back-to-zoom wiring**

When `BackSelectedMessage` is sent from `ZoomSelectionActionMenuVisible`, the `InteractionManager.OnZoomBack()` now transitions to `ZoomSelectionActive` and fires `StartZoom` again. The existing `StartZoomMessage` → `zoomVM.Receive(StartZoomMessage)` → `zoomOverlay.Show()` wiring should handle the re-show automatically. Verify this works by tracing the flow:

1. User dwells Back (↩) → `ActionMenuViewModel` sends `BackSelectedMessage`
2. `GazeEngineAdapter` calls `im.OnZoomBack()`
3. `InteractionManager`: `HideActionMenu` → `SetState(ZoomSelectionActive)` → `StartZoom(FixationPoint)`
4. `GazeEngineAdapter`: `HideActionMenu` → sends `HideActionMenuMessage`; `StartZoom` → sends `StartZoomMessage`
5. `ZoomOverlayViewModel.Receive(StartZoomMessage)` → re-computes everything, `IsVisible=true`
6. App.xaml.cs bridge → `zoomOverlay.Show(...)` (re-captures at original center)

This should work with existing wiring. The zoom fixation detector in App.xaml.cs (lines 247–274) needs to handle re-entry — verify it doesn't double-subscribe `gazeEngine.CursorMoved`. If it creates a new detector each time, the old subscription must be cleaned up. **This is the pre-existing leak mentioned in the exploration.** Fix it by storing the subscription and unsubscribing before re-subscribing.

**Step 6: Fix zoom fixation detector re-subscription leak**

Store the `CursorMoved` handler as a field and unsubscribe before creating a new one:

```csharp
EventHandler<ScreenPoint>? _zoomGazeHandler = null;

// In zoom start wiring:
if (_zoomGazeHandler != null)
    gazeEngine.CursorMoved -= _zoomGazeHandler;

var zoomFixation = new FixationDetector(...);
_zoomGazeHandler = (_, pt) => zoomFixation.Update(pt);
gazeEngine.CursorMoved += _zoomGazeHandler;
```

**Step 7: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

**Step 8: Manual smoke test checklist**

Test with mouse gaze provider:
- [ ] Non-zoom: fixate → activation → Action View shows screen preview + crosshair + buttons → click action → returns to FreeLook
- [ ] Non-zoom: cursor circle hides when Action View shows, reappears on dismiss
- [ ] Zoom: fixate → activation → Zoom View + side buttons → fixate precise point → Action View with back button
- [ ] Zoom: Back button returns to Zoom View (not FreeLook)
- [ ] Zoom: Close button returns to FreeLook
- [ ] Zoom: Re-capture button refreshes screenshot
- [ ] Cursor circle hidden during both Zoom View and Action View
- [ ] Screen-edge clamping still works
- [ ] Settings: change PositionIndicatorStyle to Pin, verify visual change
- [ ] Pause/unpause still works correctly

**Step 9: Commit**

```bash
git add -A
git commit -m "feat: complete Action View & Zoom Selection Mode wiring in App.xaml.cs"
```
