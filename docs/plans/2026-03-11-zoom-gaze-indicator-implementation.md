# Zoom Gaze Indicator Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a shrinking-circle gaze indicator to zoom view that shows where the user is looking and visualizes fixation progress.

**Architecture:** New `ZoomGazeIndicatorWindow` (separate overlay, consistent with `CursorCircleWindow` pattern) driven by a `ZoomGazeIndicatorViewModel`. The FixationDetector gets a new `GetProgress()` method to expose elapsed/total ratio. App.xaml.cs wires gaze data and fixation progress to the indicator during zoom mode.

**Tech Stack:** C# / .NET 8 / WPF, CommunityToolkit.Mvvm, xUnit

---

### Task 1: Add FixationDetector.GetProgress() Method

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/FixationDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/FixationDetectorTests.cs`

**Step 1: Write the failing tests**

Add these tests to the existing `FixationDetectorTests.cs`:

```csharp
[Fact]
public void GetProgress_BeforeAnyUpdate_ReturnsZero()
{
    var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
    Assert.Equal(0.0, detector.GetProgress());
}

[Fact]
public void GetProgress_DuringFixationTracking_ReturnsElapsedRatio()
{
    var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

    detector.Update(new ScreenPoint(100, 100), 0);
    detector.Update(new ScreenPoint(100, 100), 100_000); // 100ms of 200ms = 0.5

    // Not yet fixated (100ms < 200ms), but progress should be ~0.5
    Assert.False(detector.IsFixating);
    Assert.Equal(0.5, detector.GetProgress(), precision: 1);
}

[Fact]
public void GetProgress_AfterGazeMoves_ReturnsZero()
{
    var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

    detector.Update(new ScreenPoint(100, 100), 0);
    detector.Update(new ScreenPoint(100, 100), 100_000); // 100ms in
    detector.Update(new ScreenPoint(500, 500), 130_000); // gaze moves away

    Assert.Equal(0.0, detector.GetProgress());
}

[Fact]
public void GetProgress_ClampsAtOne()
{
    var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

    detector.Update(new ScreenPoint(100, 100), 0);
    detector.Update(new ScreenPoint(100, 100), 300_000); // 300ms > 200ms

    Assert.True(detector.IsFixating);
    Assert.Equal(1.0, detector.GetProgress());
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FixationDetectorTests" -v n`
Expected: FAIL — `GetProgress` method does not exist.

**Step 3: Implement GetProgress()**

In `FixationDetector.cs`, add a field to track the latest timestamp, and the `GetProgress()` method:

```csharp
// Add field:
private long _lastTimestamp;

// In Update(), after the first null check (line that sets _fixationStartTime), also set:
_lastTimestamp = timestampMicroseconds;

// At the top of the distance check branch (distance <= _radiusPixels), before the if(!_isFixating):
_lastTimestamp = timestampMicroseconds;

// In the else branch (gaze moved away), after resetting _fixationStartTime:
_lastTimestamp = timestampMicroseconds;

// Add this public method:
public double GetProgress()
{
    if (_fixationCenter == null || _fixationTimeMicroseconds <= 0)
        return 0.0;

    var elapsed = _lastTimestamp - _fixationStartTime;
    if (elapsed <= 0) return 0.0;

    return Math.Min(1.0, (double)elapsed / _fixationTimeMicroseconds);
}
```

The full updated `FixationDetector.cs` should be:

```csharp
namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class FixationDetector
{
    private readonly double _radiusPixels;
    private readonly long _fixationTimeMicroseconds;
    private ScreenPoint? _fixationCenter;
    private long _fixationStartTime;
    private long _lastTimestamp;
    private bool _isFixating;

    public event Action<ScreenPoint>? FixationStarted;
    public event Action? FixationEnded;
    public bool IsFixating => _isFixating;
    public ScreenPoint? FixationPoint => _isFixating ? _fixationCenter : null;

    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * 1000L;
    }

    public void Update(ScreenPoint currentPoint, long timestampMicroseconds)
    {
        _lastTimestamp = timestampMicroseconds;

        if (_fixationCenter == null)
        {
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
            return;
        }

        double distance = Math.Sqrt(
            Math.Pow(currentPoint.X - _fixationCenter.X, 2) +
            Math.Pow(currentPoint.Y - _fixationCenter.Y, 2));

        if (distance <= _radiusPixels)
        {
            if (!_isFixating && (timestampMicroseconds - _fixationStartTime) >= _fixationTimeMicroseconds)
            {
                _isFixating = true;
                FixationStarted?.Invoke(_fixationCenter);
            }
        }
        else
        {
            if (_isFixating)
            {
                _isFixating = false;
                FixationEnded?.Invoke();
            }
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
        }
    }

    /// <summary>
    /// Returns fixation progress as 0.0–1.0 ratio of elapsed time to required fixation time.
    /// Returns 0.0 if no fixation is being tracked.
    /// </summary>
    public double GetProgress()
    {
        if (_fixationCenter == null || _fixationTimeMicroseconds <= 0)
            return 0.0;

        var elapsed = _lastTimestamp - _fixationStartTime;
        if (elapsed <= 0) return 0.0;

        return Math.Min(1.0, (double)elapsed / _fixationTimeMicroseconds);
    }

    public void Reset()
    {
        _fixationCenter = null;
        _isFixating = false;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FixationDetectorTests" -v n`
Expected: All 7 tests PASS (3 existing + 4 new).

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/FixationDetector.cs tests/EyeCtrl.Core.Tests/Processing/FixationDetectorTests.cs
git commit -m "feat: add GetProgress() to FixationDetector for animation support"
```

---

### Task 2: Add ZoomGazeIndicator Settings

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ZoomSettings.cs`

**Step 1: Add indicator settings to ZoomSettings**

Add these properties to `ZoomSettings.cs`:

```csharp
[ObservableProperty] private double _gazeIndicatorOuterDiameter = 30.0;
[ObservableProperty] private double _gazeIndicatorCenterDotDiameter = 10.0;
[ObservableProperty] private double _gazeIndicatorStrokeThickness = 2.0;
[ObservableProperty] private double _gazeIndicatorOpacity = 0.7;
[ObservableProperty] private int _gazeIndicatorResetAnimationMs = 100;
```

**Step 2: Run all tests to verify nothing broke**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: All tests PASS.

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ZoomSettings.cs
git commit -m "feat: add zoom gaze indicator settings"
```

---

### Task 3: Create ZoomGazeIndicatorViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/ZoomGazeIndicatorViewModel.cs`

**Step 1: Create the ViewModel**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomGazeIndicatorViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>
{
    private readonly ZoomSettings _zoomSettings;

    // Position in screen coordinates (magnified space)
    [ObservableProperty] private double _indicatorX;
    [ObservableProperty] private double _indicatorY;

    // Outer circle diameter — animated from OuterDiameter → CenterDotDiameter during fixation
    [ObservableProperty] private double _outerCircleDiameter;

    // Whether the indicator is visible
    [ObservableProperty] private bool _isVisible;

    // Fixation progress 0.0 → 1.0 (raw, before easing)
    [ObservableProperty] private double _fixationProgress;

    // Zoom geometry needed for coordinate mapping
    private ScreenPoint _zoomOriginalCenter = new(0, 0);
    private ScreenPoint _zoomWindowCenter = new(0, 0);
    private double _magnification = 1.0;

    public double CenterDotDiameter => _zoomSettings.GazeIndicatorCenterDotDiameter;
    public double StrokeThickness => _zoomSettings.GazeIndicatorStrokeThickness;
    public double Opacity => _zoomSettings.GazeIndicatorOpacity;

    public ZoomGazeIndicatorViewModel(ZoomSettings zoomSettings, IMessenger messenger)
    {
        _zoomSettings = zoomSettings;
        _outerCircleDiameter = zoomSettings.GazeIndicatorOuterDiameter;
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        _zoomOriginalCenter = message.Center;
        _magnification = message.Magnification;

        // The zoom window is centered on message.Center on screen
        _zoomWindowCenter = message.Center;

        _outerCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
        _fixationProgress = 0.0;
        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
    }

    /// <summary>
    /// Called from App.xaml.cs with the current gaze screen point.
    /// Converts to magnified coordinates and updates position.
    /// </summary>
    public void UpdateGazePosition(ScreenPoint gazeScreenPoint)
    {
        // Convert screen gaze to magnified coordinates:
        // magnifiedPoint = zoomWindowCenter + (gazePoint - originalCenter) * magnification
        var magX = _zoomWindowCenter.X + (gazeScreenPoint.X - _zoomOriginalCenter.X) * _magnification;
        var magY = _zoomWindowCenter.Y + (gazeScreenPoint.Y - _zoomOriginalCenter.Y) * _magnification;

        IndicatorX = magX;
        IndicatorY = magY;
    }

    /// <summary>
    /// Called from App.xaml.cs with the fixation progress (0.0–1.0).
    /// Applies ease-in curve and updates outer circle diameter.
    /// </summary>
    public void UpdateFixationProgress(double progress)
    {
        FixationProgress = progress;

        // Apply quadratic ease-in: slow start, accelerates
        var eased = progress * progress;

        // Interpolate diameter: outerDiameter → centerDotDiameter
        var maxDiam = _zoomSettings.GazeIndicatorOuterDiameter;
        var minDiam = _zoomSettings.GazeIndicatorCenterDotDiameter;
        OuterCircleDiameter = maxDiam - (maxDiam - minDiam) * eased;
    }

    /// <summary>
    /// Called when fixation is aborted — triggers reset animation.
    /// The caller (window code-behind) uses ResetAnimationMs for timing.
    /// </summary>
    public void ResetFixation()
    {
        FixationProgress = 0.0;
        // OuterCircleDiameter will be animated back to full by the window code-behind
        // using the ResetAnimationMs duration. We set the target here.
        OuterCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
    }

    public int ResetAnimationMs => _zoomSettings.GazeIndicatorResetAnimationMs;
}
```

**Step 2: Verify the solution builds**

Run: `dotnet build src/EyeCtrl.ViewModels`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/ZoomGazeIndicatorViewModel.cs
git commit -m "feat: add ZoomGazeIndicatorViewModel with coordinate mapping and ease-in animation"
```

---

### Task 4: Create ZoomGazeIndicatorWindow

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs`

**Step 1: Create the XAML**

Create `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml`:

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.ZoomGazeIndicatorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        AutomationProperties.AutomationId="ZoomGazeIndicator_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="30" Height="30">
    <Canvas>
        <!-- Outer shrinking circle -->
        <Ellipse x:Name="OuterCircle"
                 Stroke="Lime" StrokeThickness="2" Fill="Transparent"
                 Width="30" Height="30"
                 Canvas.Left="0" Canvas.Top="0"/>
        <!-- Center dot (fixed size) -->
        <Ellipse x:Name="CenterDot"
                 Fill="Lime"
                 Width="10" Height="10"
                 Canvas.Left="10" Canvas.Top="10"/>
        <!-- UIA anchor -->
        <TextBlock AutomationProperties.AutomationId="ZoomGazeIndicator_Ellipse_Main"
                   Opacity="0" IsHitTestVisible="False"
                   Canvas.Left="0" Canvas.Top="0"/>
    </Canvas>
</overlays:OverlayWindow>
```

**Step 2: Create the code-behind**

Create `src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

public partial class ZoomGazeIndicatorWindow : OverlayWindow
{
    private double _maxDiameter = 30;

    public ZoomGazeIndicatorWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    /// <summary>
    /// Configure sizes and appearance. Call once after construction or when settings change.
    /// </summary>
    public void Configure(double outerDiameter, double centerDotDiameter, double strokeThickness, double opacity)
    {
        _maxDiameter = outerDiameter;

        Width = outerDiameter;
        Height = outerDiameter;

        OuterCircle.Width = outerDiameter;
        OuterCircle.Height = outerDiameter;
        OuterCircle.StrokeThickness = strokeThickness;
        OuterCircle.Opacity = opacity;

        CenterDot.Width = centerDotDiameter;
        CenterDot.Height = centerDotDiameter;
        CenterDot.Opacity = opacity;

        CenterOuterCircle();
        CenterDotElement();
    }

    /// <summary>
    /// Update indicator position (screen coordinates of indicator center).
    /// </summary>
    public void UpdatePosition(double screenX, double screenY)
    {
        Left = screenX - _maxDiameter / 2;
        Top = screenY - _maxDiameter / 2;
    }

    /// <summary>
    /// Update the outer circle diameter (called as fixation progresses).
    /// </summary>
    public void UpdateOuterDiameter(double diameter)
    {
        OuterCircle.Width = diameter;
        OuterCircle.Height = diameter;
        CenterOuterCircle();
    }

    /// <summary>
    /// Animate the outer circle back to full size over the given duration.
    /// </summary>
    public void AnimateReset(double targetDiameter, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = targetDiameter,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            OuterCircle.Width = targetDiameter;
            OuterCircle.Height = targetDiameter;
            CenterOuterCircle();
        };
        OuterCircle.BeginAnimation(FrameworkElement.WidthProperty, anim);
        OuterCircle.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }

    private void CenterOuterCircle()
    {
        System.Windows.Controls.Canvas.SetLeft(OuterCircle, (_maxDiameter - OuterCircle.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(OuterCircle, (_maxDiameter - OuterCircle.Height) / 2);
    }

    private void CenterDotElement()
    {
        System.Windows.Controls.Canvas.SetLeft(CenterDot, (_maxDiameter - CenterDot.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(CenterDot, (_maxDiameter - CenterDot.Height) / 2);
    }
}
```

**Step 3: Verify the solution builds**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs
git commit -m "feat: add ZoomGazeIndicatorWindow overlay with shrinking circle animation"
```

---

### Task 5: Wire Indicator into App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

This is the integration task. The indicator must be created, shown when zoom starts, fed gaze data, fed fixation progress, and hidden when zoom ends.

**Step 1: Instantiate the window and ViewModel**

Find the section in `App.xaml.cs` where other overlays are created (near where `zoomOverlay`, `zoomSideButtons`, `cursorCircle` are instantiated). Add:

```csharp
var zoomIndicatorVM = new ZoomGazeIndicatorViewModel(settings.Zoom, messenger);
var zoomIndicator = new ZoomGazeIndicatorWindow();
zoomIndicator.Configure(
    settings.Zoom.GazeIndicatorOuterDiameter,
    settings.Zoom.GazeIndicatorCenterDotDiameter,
    settings.Zoom.GazeIndicatorStrokeThickness,
    settings.Zoom.GazeIndicatorOpacity);
```

**Step 2: Show/hide indicator when zoom visibility changes**

Inside the existing `zoomVM.PropertyChanged` handler that manages `IsVisible`, add indicator logic:

When zoom becomes visible (inside the `if (zoomVM.IsVisible)` block), after showing the zoom overlay:

```csharp
zoomIndicator.Show();
```

When zoom becomes invisible (the `else` block with `zoomOverlay.Hide()`):

```csharp
zoomIndicator.Hide();
```

**Step 3: Feed gaze data to the indicator**

Inside the existing `zoomGazeHandler` lambda (which already receives `(pt, ts)` for the zoom fixation detector), add indicator position update. This goes **before** the grace period check so the indicator tracks gaze even during the grace period:

```csharp
// Update indicator position (even during grace period)
Dispatcher.BeginInvoke(() => {
    zoomIndicatorVM.UpdateGazePosition(pt);
    zoomIndicator.UpdatePosition(zoomIndicatorVM.IndicatorX, zoomIndicatorVM.IndicatorY);
});
```

**Step 4: Feed fixation progress to the indicator**

After the `zoomFixation.Update(pt, ts)` call (which is only reached after the grace period), add:

```csharp
// Update fixation progress on indicator
var progress = zoomFixation.GetProgress();
Dispatcher.BeginInvoke(() => {
    zoomIndicatorVM.UpdateFixationProgress(progress);
    zoomIndicator.UpdateOuterDiameter(zoomIndicatorVM.OuterCircleDiameter);
});
```

**Step 5: Handle fixation abort (reset animation)**

Subscribe to the zoom fixation detector's `FixationEnded` event (add this right after the `FixationStarted` subscription):

```csharp
zoomFixation.FixationEnded += () =>
{
    Dispatcher.BeginInvoke(() =>
    {
        zoomIndicatorVM.ResetFixation();
        zoomIndicator.AnimateReset(
            settings.Zoom.GazeIndicatorOuterDiameter,
            settings.Zoom.GazeIndicatorResetAnimationMs);
    });
};
```

**Step 6: Hide indicator on fixation complete**

Inside the existing `zoomFixation.FixationStarted` handler (which calls `im.OnZoomFixation(realPoint)`), add before or after the fixation call:

```csharp
zoomIndicator.Hide();
```

**Step 7: Verify the solution builds**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 8: Run all tests**

Run: `dotnet test -v n`
Expected: All tests PASS.

**Step 9: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire zoom gaze indicator into app lifecycle and gaze pipeline"
```

---

### Task 6: Manual Testing and Tuning

**Step 1: Build and run the app**

Run: `dotnet run --project src/EyeCtrl.App`

Or if using published build:
```bash
dotnet publish src/EyeCtrl.App -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Step 2: Test the zoom indicator**

1. Enter free-look mode — cursor circle should appear as normal
2. Fixate to trigger activation button → select Zoom action
3. **Verify:** Zoom opens, gaze indicator (two concentric circles) appears inside the magnified area
4. **Verify:** Indicator follows gaze smoothly during the grace period (no shrinking yet)
5. **Verify:** After grace period, holding gaze still causes the outer circle to shrink (ease-in)
6. **Verify:** Moving gaze away causes the outer circle to quickly expand back (~100ms animation)
7. **Verify:** Completing fixation hides the indicator and shows the action menu
8. **Verify:** Closing zoom (via side button) hides the indicator

**Step 3: Tune values if needed**

Adjust in `ZoomSettings.cs`:
- `GazeIndicatorOuterDiameter` (default 30) — try 25–40
- `GazeIndicatorCenterDotDiameter` (default 10) — try 6–12
- `GazeIndicatorOpacity` (default 0.7) — try 0.5–0.9
- `GazeIndicatorResetAnimationMs` (default 100) — try 80–150

**Step 4: Commit any tuning changes**

```bash
git add -A
git commit -m "tune: adjust zoom gaze indicator visual parameters"
```
