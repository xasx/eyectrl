# Bugfixes & Polish Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix five issues: multi-trigger dwell, circle visible when paused, cursor undershoot, keyboard focus stealing, undersized controls.

**Architecture:** All fixes are isolated. Tasks 1-2 are Core changes with TDD. Tasks 3-4 are App-layer wiring/constant changes (no unit tests — verified by build + manual testing).

**Tech Stack:** C# / .NET 8 / WPF / xUnit

---

### Task 1: DwellDetector — Nearest Target Only

**Problem:** `DwellDetector.Update()` accumulates dwell on ALL targets whose hit circle contains the gaze point. Overlapping circles cause multiple buttons to trigger.

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs`

**Step 1: Write the failing test**

Add this test to `DwellDetectorTests.cs`:

```csharp
[Fact]
public void Update_OverlappingTargets_OnlyNearestAccumulatesDwell()
{
    var detector = new DwellDetector();
    // Two targets at (100,100) and (130,100) with radius 40 — they overlap
    var targetA = new DwellTarget("A", new ScreenPoint(100, 100), radius: 40, dwellTimeMs: 200);
    var targetB = new DwellTarget("B", new ScreenPoint(130, 100), radius: 40, dwellTimeMs: 200);
    detector.AddTarget(targetA);
    detector.AddTarget(targetB);

    var progressIds = new List<string>();
    detector.TargetProgress += (id, progress) =>
    {
        if (progress > 0) progressIds.Add(id);
    };

    // Gaze at (110, 100) — inside both circles, but closer to A (dist=10) than B (dist=20)
    long time = 0;
    for (int i = 0; i < 5; i++)
    {
        detector.Update(new ScreenPoint(110, 100), time);
        time += 30_000;
    }

    // Only A should have received positive progress
    Assert.Contains("A", progressIds);
    Assert.DoesNotContain("B", progressIds);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "OverlappingTargets" -v n`
Expected: FAIL — both A and B receive progress (current behavior).

**Step 3: Implement nearest-target-only logic**

In `DwellDetector.cs`, replace the body of `Update()` (lines 25-73) with:

```csharp
public void Update(ScreenPoint gazePoint, long timestampMicroseconds)
{
    _lastTimestamp = timestampMicroseconds;

    // ── Find the single nearest target within hit range ──
    string? nearestId = null;
    double nearestDistSq = double.MaxValue;

    foreach (var target in _targets.Values)
    {
        if (target.HitTest(gazePoint))
        {
            double dx = gazePoint.X - target.Center.X;
            double dy = gazePoint.Y - target.Center.Y;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestId = target.Id;
            }
        }
    }

    // ── Accumulate dwell on nearest only; reset all others ──
    string? activatedId = null;

    foreach (var target in _targets.Values)
    {
        bool isNearest = target.Id == nearestId;

        if (isNearest)
        {
            if (!target.IsGazeInside)
            {
                target.IsGazeInside = true;
                target.EnteredTimestamp = timestampMicroseconds;
            }
            else if (target.EnteredTimestamp.HasValue)
            {
                long elapsed = timestampMicroseconds - target.EnteredTimestamp.Value;
                double progress = Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
                TargetProgress?.Invoke(target.Id, progress);

                if (elapsed >= target.DwellTimeMicroseconds)
                {
                    activatedId = target.Id;
                    target.EnteredTimestamp = null;
                    target.IsGazeInside = false;
                }
            }
        }
        else
        {
            if (target.IsGazeInside)
                TargetProgress?.Invoke(target.Id, 0);
            target.IsGazeInside = false;
            target.EnteredTimestamp = null;
        }
    }

    if (activatedId != null)
    {
        TargetProgress?.Invoke(activatedId, 0);
        TargetActivated?.Invoke(activatedId);
    }
}
```

**Step 4: Run all tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: ALL pass (including existing 3 DwellDetector tests + new one).

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs
git commit -m "fix: DwellDetector only accumulates dwell on nearest target"
```

---

### Task 2: OneEuroFilter

**Problem:** SmoothedFilter (EMA, alpha=0.5) causes cursor to stop short of gaze target. Replace with adaptive OneEuroFilter.

**Files:**
- Create: `src/EyeCtrl.Core/Processing/OneEuroFilter.cs`
- Create: `tests/EyeCtrl.Core.Tests/Processing/OneEuroFilterTests.cs`
- Modify: `src/EyeCtrl.Core/Models/GazeFilterMode.cs`
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs`
- Modify: `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs`

**Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Processing/OneEuroFilterTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class OneEuroFilterTests
{
    [Fact]
    public void Filter_FirstPoint_ReturnedDirectly()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        var result = filter.Filter(new GazePoint(500.0, 300.0, 0, true));
        Assert.Equal(500.0, result.X, precision: 1);
        Assert.Equal(300.0, result.Y, precision: 1);
    }

    [Fact]
    public void Filter_StationaryGaze_ConvergesToTarget()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        // First point
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        // Jump to (500, 500) and hold for 10 frames at 30Hz (~33ms = 33000us apart)
        ScreenPoint result = new(0, 0);
        long timestamp = 33_000;
        for (int i = 0; i < 10; i++)
        {
            result = filter.Filter(new GazePoint(500.0, 500.0, timestamp, true));
            timestamp += 33_000;
        }

        // After 10 frames of stationary gaze, should be within 5px of target
        Assert.InRange(result.X, 495.0, 505.0);
        Assert.InRange(result.Y, 495.0, 505.0);
    }

    [Fact]
    public void Filter_HighSpeedMovement_LessSmoothing()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        // Large jump — should track quickly due to high speed
        var result = filter.Filter(new GazePoint(800.0, 800.0, 33_000, true));

        // Should be much closer to 800 than to 100 (unlike EMA alpha=0.5 which gives 450)
        Assert.True(result.X > 500.0, $"Expected X > 500 but got {result.X}");
        Assert.True(result.Y > 500.0, $"Expected Y > 500 but got {result.Y}");
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastFiltered()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(200.0, 300.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 33_000, false));
        Assert.Equal(200.0, result.X, precision: 1);
        Assert.Equal(300.0, result.Y, precision: 1);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        // After reset, next point should be returned directly (no smoothing with old state)
        var result = filter.Filter(new GazePoint(800.0, 900.0, 100_000, true));
        Assert.Equal(800.0, result.X, precision: 1);
        Assert.Equal(900.0, result.Y, precision: 1);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "OneEuroFilter" -v n`
Expected: FAIL — `OneEuroFilter` class does not exist.

**Step 3: Implement OneEuroFilter**

Create `src/EyeCtrl.Core/Processing/OneEuroFilter.cs`:

```csharp
namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// 1€ Filter (Casiez et al., 2012) — adaptive low-pass filter.
/// Smooth when fixating, responsive during fast movement.
/// </summary>
public class OneEuroFilter : IGazeFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private LowPassFilter? _xFilter;
    private LowPassFilter? _yFilter;
    private LowPassFilter? _dxFilter;
    private LowPassFilter? _dyFilter;
    private long _prevTimestamp;
    private bool _initialized;
    private ScreenPoint _lastFiltered = new(0, 0);

    public OneEuroFilter(double minCutoff = 1.0, double beta = 0.07, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _initialized)
            return _lastFiltered;

        double x = rawGaze.X;
        double y = rawGaze.Y;

        if (!_initialized)
        {
            _xFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), x);
            _yFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), y);
            _dxFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _dyFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _prevTimestamp = rawGaze.TimestampMicroseconds;
            _initialized = true;
            _lastFiltered = new ScreenPoint(x, y);
            return _lastFiltered;
        }

        long dt_us = rawGaze.TimestampMicroseconds - _prevTimestamp;
        double dt = dt_us > 0 ? dt_us / 1_000_000.0 : 1.0 / 30;
        _prevTimestamp = rawGaze.TimestampMicroseconds;

        // Estimate derivatives
        double dx = (x - _xFilter!.Last) / dt;
        double dy = (y - _yFilter!.Last) / dt;

        // Filter derivatives
        double dAlpha = Alpha(_dCutoff, dt);
        double edx = _dxFilter!.Filter(dx, dAlpha);
        double edy = _dyFilter!.Filter(dy, dAlpha);

        // Compute adaptive cutoff based on speed
        double speed = Math.Sqrt(edx * edx + edy * edy);
        double cutoff = _minCutoff + _beta * speed;

        // Filter signal
        double alpha = Alpha(cutoff, dt);
        double fx = _xFilter.Filter(x, alpha);
        double fy = _yFilter.Filter(y, alpha);

        _lastFiltered = new ScreenPoint(fx, fy);
        return _lastFiltered;
    }

    public void Reset()
    {
        _initialized = false;
        _xFilter = null;
        _yFilter = null;
        _dxFilter = null;
        _dyFilter = null;
        _lastFiltered = new ScreenPoint(0, 0);
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }

    private class LowPassFilter
    {
        public double Last { get; private set; }

        public LowPassFilter(double alpha, double initVal)
        {
            Last = initVal;
        }

        public double Filter(double value, double alpha)
        {
            double result = alpha * value + (1 - alpha) * Last;
            Last = result;
            return result;
        }
    }
}
```

**Step 4: Run OneEuroFilter tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "OneEuroFilter" -v n`
Expected: ALL 5 pass.

**Step 5: Update GazeFilterMode enum**

In `src/EyeCtrl.Core/Models/GazeFilterMode.cs`, change:
```csharp
public enum GazeFilterMode { Direct, Smoothed, SmoothedWithSnapping }
```
to:
```csharp
public enum GazeFilterMode { Direct, Smoothed, SmoothedWithSnapping, OneEuro }
```

**Step 6: Update CursorSettings defaults**

In `src/EyeCtrl.Core/Settings/CursorSettings.cs`, change:
```csharp
public GazeFilterMode FilterMode { get; set; } = GazeFilterMode.Smoothed;
```
to:
```csharp
public GazeFilterMode FilterMode { get; set; } = GazeFilterMode.OneEuro;
```

And add two new properties after `SmoothingFactor`:
```csharp
public double OneEuroMinCutoff { get; set; } = 1.0;
public double OneEuroBeta { get; set; } = 0.07;
```

**Step 7: Update GazeFilterFactory**

In `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs`, add the OneEuro case:
```csharp
public static IGazeFilter Create(CursorSettings settings)
{
    return settings.FilterMode switch
    {
        GazeFilterMode.Direct => new DirectFilter(),
        GazeFilterMode.Smoothed => new SmoothedFilter(settings.SmoothingFactor),
        GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
        GazeFilterMode.OneEuro => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta),
        _ => new OneEuroFilter(settings.OneEuroMinCutoff, settings.OneEuroBeta)
    };
}
```

Note: the default (`_`) case also changes from `DirectFilter` to `OneEuroFilter`.

**Step 8: Run ALL tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: ALL pass (existing tests unaffected — SmoothedFilter still exists and its tests still pass).

**Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Processing/OneEuroFilter.cs src/EyeCtrl.Core/Models/GazeFilterMode.cs src/EyeCtrl.Core/Settings/CursorSettings.cs src/EyeCtrl.Core/Processing/GazeFilterFactory.cs tests/EyeCtrl.Core.Tests/Processing/OneEuroFilterTests.cs
git commit -m "feat: add OneEuroFilter for adaptive gaze smoothing, set as default"
```

---

### Task 3: Hide Circle on Pause + WS_EX_NOACTIVATE on All Overlays

**Problem A:** Cursor circle stays visible and follows gaze in pause mode.
**Problem B:** Keyboard window steals focus from target applications.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (lines 159-162, 200-201)
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (line 87)
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` (OnSourceInitialized)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (OnSourceInitialized)
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs` (OnSourceInitialized)
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs` (OnSourceInitialized)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (needs OnSourceInitialized + DllImports)

**Step 1: Hide cursor circle on pause**

In `App.xaml.cs`, expand the `StateChanged` handler (lines 159-162):

```csharp
im.StateChanged += state => Dispatcher.BeginInvoke(() =>
{
    _toolbar.SetPausedState(state == InteractionState.Paused);
    if (state == InteractionState.Paused)
        _cursorCircle.Hide();
    else
        _cursorCircle.Show();
});
```

Also, in the `CursorMoved` handler (line 200-201), guard the circle update:

```csharp
// Cursor circle follows gaze only when not paused
if (im.CurrentState != InteractionState.Paused)
    _cursorCircle.UpdatePosition(point);
```

**Step 2: Add WS_EX_NOACTIVATE to all overlay windows**

The constant value is `0x08000000`. Add it to each overlay's `OnSourceInitialized`:

**KeyboardWindow.xaml.cs** — change line 87:
```csharp
// Before:
SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW);
// After:
SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
```

Also add the constant near line 16:
```csharp
private const int WS_EX_NOACTIVATE = 0x08000000;
```

Apply the same pattern to these files (each already has `GWL_EXSTYLE`, `WS_EX_TOOLWINDOW`, `GetWindowLong`, `SetWindowLong`):

- **ActionMenuWindow.xaml.cs**: Add `WS_EX_NOACTIVATE` constant + add to `SetWindowLong` call
- **ToolbarWindow.xaml.cs**: Same
- **ActivationButtonWindow.xaml.cs**: Same
- **ZoomOverlayWindow.xaml.cs**: Same (already has `WS_EX_TRANSPARENT` too — add NOACTIVATE alongside)
- **CursorCircleWindow.xaml.cs**: Same (already has `WS_EX_TRANSPARENT` — add NOACTIVATE)

**SettingsWindow.xaml.cs** — this window does NOT have `OnSourceInitialized` or Win32 imports yet. Add them:

Add DllImport declarations and constants near the top of the class (after the existing constants):
```csharp
private const int GWL_EXSTYLE = -20;
private const int WS_EX_TOOLWINDOW = 0x00000080;
private const int WS_EX_NOACTIVATE = 0x08000000;

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern int GetWindowLong(IntPtr hwnd, int index);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
```

And add the override:
```csharp
protected override void OnSourceInitialized(EventArgs e)
{
    base.OnSourceInitialized(e);
    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
    SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
}
```

**Step 3: Build and verify**

Run: `dotnet build src/EyeCtrl.App -c Release`
Expected: 0 errors.

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: ALL pass (no Core changes, but verify nothing broke).

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "fix: hide circle on pause, add WS_EX_NOACTIVATE to all overlays"
```

---

### Task 4: Increase Control Sizes ~25%

**Problem:** Settings and keyboard controls are too small for comfortable eye-tracker targeting.

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (lines 14-17)
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (line 17)

**Step 1: Update SettingsWindow size constants**

In `SettingsWindow.xaml.cs`, change lines 14-17:

```csharp
// Before:
private const double ControlBtnRadius = 40;
private const double TabBtnRadius = 70;
private const double PagBtnRadius = 50;
private const double ModalBtnRadius = 60;

// After:
private const double ControlBtnRadius = 50;
private const double TabBtnRadius = 85;
private const double PagBtnRadius = 60;
private const double ModalBtnRadius = 70;
```

Also need to update the visual sizes of controls built in code-behind. Search for these patterns and increase them:

- Tab buttons: `80` width/height → `100`
- Control buttons (sliders, combos, toggles): `80` width → `100`, `64` height → `80`
- Pagination buttons: `80` width → `100`, `64` height → `80`
- Modal buttons: `140` width → `170`, `64` height → `80`
- Color swatches: `72` width → `90`, `60` height → `75`
- Reset buttons: `260` width → `300`, `64` height → `80`
- Font sizes: scale proportionally where button text appears too small

To find the exact lines, search for `Width = 80`, `Height = 64`, `Width = 72`, `Width = 140`, `Width = 260`, and `FontSize` in SettingsWindow.xaml.cs and update each occurrence.

**Step 2: Update KeyboardWindow base key size**

In `KeyboardWindow.xaml.cs`, change line 17:

```csharp
// Before:
private const double BaseKeySize = 56;

// After:
private const double BaseKeySize = 70;
```

**Step 3: Build and verify**

Run: `dotnet build src/EyeCtrl.App -c Release`
Expected: 0 errors.

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: ALL pass.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git commit -m "feat: increase settings and keyboard control sizes by ~25%"
```

---

### Task 5: Final Build + Publish

**Step 1: Full build verification**

Run: `dotnet build EyeCtrl.slnx -c Release`
Expected: 0 errors, 0 warnings (or only the .NET 10 preview warning).

**Step 2: Full test suite**

Run: `dotnet test EyeCtrl.slnx -v n`
Expected: ALL pass.

**Step 3: Publish**

Run: `dotnet publish src/EyeCtrl.App -r win-x64 --self-contained true -p:PublishSingleFile=true -c Release -o publish`
Expected: Success, outputs to `publish/EyeCtrl.App.exe`.
