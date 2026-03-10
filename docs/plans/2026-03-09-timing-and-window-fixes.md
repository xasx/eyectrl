# Timing & Window Management Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix 5 UX issues from VM testing: rapid re-triggering after dwell, instant zoom fixation, zoom visible behind action menu, cursor circle in screenshots, and defensive window visibility guards.

**Architecture:** All fixes are contained changes. Fix 1 (cooldown) modifies `DwellDetector` + `DwellSettings`. Fix 2 (grace period) modifies `ZoomSettings` + `App.xaml.cs` wiring. Fixes 3-5 modify `InteractionManager` events and `App.xaml.cs` wiring/overlay windows.

**Tech Stack:** C# / .NET 8 / WPF / CommunityToolkit.Mvvm / xUnit

---

### Task 1: Add DwellCooldownMs setting

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs:15`

**Step 1: Add the setting**

In `DwellSettings.cs`, add after line 15 (`_velocityThresholdPxPerSec`):

```csharp
[ObservableProperty] private int _dwellCooldownMs = 400;
```

**Step 2: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/DwellSettings.cs
git commit -m "feat(settings): add DwellCooldownMs setting (default 400ms)"
```

---

### Task 2: Add cooldown logic to DwellDetector — failing tests

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs`

**Step 1: Write failing test — cooldown blocks re-trigger**

Add to `DwellDetectorTests.cs`:

```csharp
[Fact]
public void Update_WithinCooldown_DoesNotRetrigger()
{
    var detector = new DwellDetector(cooldownMs: 400);
    var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
    detector.AddTarget(target);

    var activations = new List<string>();
    detector.TargetActivated += id => activations.Add(id);

    long time = 0;

    // First dwell: gaze enters, accumulate 200ms → activates
    for (int i = 0; i < 9; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000; // 30ms per frame
    }
    Assert.Single(activations);

    // Continue gazing within cooldown (400ms = 400_000us from activation)
    // Activation happened around time ~240_000. Cooldown ends at ~640_000.
    // We're now at time ~240_000. Send updates for another 300ms — should NOT re-trigger.
    for (int i = 0; i < 10; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000;
    }

    Assert.Single(activations); // still only one
}

[Fact]
public void Update_AfterCooldown_CanRetrigger()
{
    var detector = new DwellDetector(cooldownMs: 400);
    var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
    detector.AddTarget(target);

    var activations = new List<string>();
    detector.TargetActivated += id => activations.Add(id);

    long time = 0;

    // First dwell: accumulate 200ms → activates
    for (int i = 0; i < 9; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000;
    }
    Assert.Single(activations);

    // Jump past cooldown period (400ms beyond activation)
    time += 500_000; // 500ms jump — well past cooldown

    // Second dwell: accumulate another 200ms → should activate again
    for (int i = 0; i < 9; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000;
    }

    Assert.Equal(2, activations.Count);
}

[Fact]
public void Update_ZeroCooldown_AllowsImmediateRetrigger()
{
    var detector = new DwellDetector(cooldownMs: 0);
    var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
    detector.AddTarget(target);

    var activations = new List<string>();
    detector.TargetActivated += id => activations.Add(id);

    long time = 0;

    // First dwell
    for (int i = 0; i < 9; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000;
    }
    Assert.Single(activations);

    // Immediately continue — with 0 cooldown, should re-trigger after another 200ms
    for (int i = 0; i < 9; i++)
    {
        detector.Update(new ScreenPoint(100, 100), time);
        time += 30_000;
    }

    Assert.Equal(2, activations.Count);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "DwellDetectorTests" -v minimal`
Expected: FAIL — `DwellDetector` constructor doesn't accept `cooldownMs` parameter

---

### Task 3: Implement DwellDetector cooldown

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`

**Step 1: Add cooldown constructor parameter and fields**

Replace the class declaration and fields (lines 4-7):

```csharp
public class DwellDetector
{
    private readonly Dictionary<string, DwellTarget> _targets = new();
    private readonly long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    public DwellDetector(int cooldownMs = 0)
    {
        _cooldownMicroseconds = cooldownMs * 1000L;
    }
```

**Step 2: Add cooldown check in Update method**

In the `Update` method, after `_lastTimestamp = timestampMicroseconds;` (line 28), add:

```csharp
// Cooldown: skip all dwell accumulation if within cooldown period
if (_lastActivationTimestamp.HasValue &&
    (timestampMicroseconds - _lastActivationTimestamp.Value) < _cooldownMicroseconds)
{
    // Reset all targets during cooldown (prevent partial accumulation)
    foreach (var t in _targets.Values)
    {
        if (t.IsGazeInside)
            TargetProgress?.Invoke(t.Id, 0);
        t.IsGazeInside = false;
        t.EnteredTimestamp = null;
    }
    return;
}
_lastActivationTimestamp = null; // cooldown elapsed, clear it
```

**Step 3: Set activation timestamp when target activates**

In the activation block (after line 73 `target.IsGazeInside = false;`), add:

```csharp
_lastActivationTimestamp = timestampMicroseconds;
```

**Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "DwellDetectorTests" -v minimal`
Expected: All tests PASS (including the 4 existing ones — they use the default constructor with 0 cooldown)

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs
git commit -m "feat(dwell): add configurable cooldown to DwellDetector

Prevents rapid re-triggering by blocking dwell accumulation for a
configurable duration after activation. Default 0ms (backward compat).
Adds 3 tests for cooldown behavior."
```

---

### Task 4: Wire cooldown into all DwellDetector instances

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActivationButtonViewModel.cs` — pass cooldownMs to DwellDetector constructor
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` — pass cooldownMs
- Modify: `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs` — pass cooldownMs
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` — pass cooldownMs

**Step 1: Find each DwellDetector instantiation**

Search for `new DwellDetector()` in the ViewModels. Each one needs to change to `new DwellDetector(dwellSettings.DwellCooldownMs)`.

The ViewModels already receive `DwellSettings` via constructor injection. Update each `new DwellDetector()` call to `new DwellDetector(dwellSettings.DwellCooldownMs)`.

If a ViewModel does NOT already receive `DwellSettings`, add it as a constructor parameter. Check each file before editing.

**Step 2: Verify build and tests pass**

Run: `dotnet build EyeCtrl.slnx && dotnet test tests/EyeCtrl.Core.Tests -v minimal`
Expected: Build succeeded, all tests pass

**Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/
git commit -m "feat(dwell): wire cooldown setting into all DwellDetector instances"
```

---

### Task 5: Add ZoomGracePeriodMs setting

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ZoomSettings.cs:9`

**Step 1: Add the setting**

In `ZoomSettings.cs`, add after line 9 (`_magnificationLevel`):

```csharp
[ObservableProperty] private int _zoomGracePeriodMs = 500;
```

**Step 2: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ZoomSettings.cs
git commit -m "feat(settings): add ZoomGracePeriodMs setting (default 500ms)"
```

---

### Task 6: Implement zoom grace period in App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:245-276` (zoom wiring section)

**Step 1: Add grace period logic**

In the zoom visibility handler (`zoomVM.PropertyChanged` at line 236), inside the `if (zoomVM.IsVisible)` branch, after the `FixationDetector` is created (line 256-258), track the zoom start time and filter gaze data.

Change the `zoomGazeHandler` lambda (lines 271-275) from:

```csharp
zoomGazeHandler = (pt, ts) =>
{
    if (im.CurrentState == InteractionState.ZoomSelectionActive)
        zoomFixation.Update(pt, ts);
};
```

To:

```csharp
var zoomStartTimestamp = 0L;
var gracePeriodUs = settings.Zoom.ZoomGracePeriodMs * 1000L;
var graceStarted = false;

zoomGazeHandler = (pt, ts) =>
{
    if (im.CurrentState != InteractionState.ZoomSelectionActive) return;

    // Record the first timestamp as zoom start
    if (!graceStarted)
    {
        zoomStartTimestamp = ts;
        graceStarted = true;
    }

    // Skip gaze data during grace period
    if ((ts - zoomStartTimestamp) < gracePeriodUs) return;

    zoomFixation.Update(pt, ts);
};
```

**Step 2: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(zoom): add grace period before zoom fixation detection starts

Delays zoom fixation detection by ZoomGracePeriodMs (default 500ms)
after zoom activates, giving the user time to orient in the magnified view."
```

---

### Task 7: Add HideZoom event and fire it on zoom fixation — failing test

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

**Step 1: Write failing test**

Add to `InteractionManagerTests.cs`:

```csharp
[Fact]
public void OnZoomFixation_FiresHideZoomBeforeShowActionMenu()
{
    var im = new InteractionManager { ZoomEnabled = true };
    var events = new List<string>();

    im.HideZoom += () => events.Add("HideZoom");
    im.ShowActionMenu += _ => events.Add("ShowActionMenu");

    im.OnFixationStarted(new ScreenPoint(100, 100));
    im.OnActivationButtonDwelled();
    im.OnZoomFixation(new ScreenPoint(110, 110));

    Assert.Equal(new[] { "HideZoom", "ShowActionMenu" }, events.TakeLast(2));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "OnZoomFixation_FiresHideZoomBeforeShowActionMenu" -v minimal`
Expected: FAIL — `HideZoom` event doesn't exist on `InteractionManager`

---

### Task 8: Implement HideZoom event on InteractionManager

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs:18`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs:60-66`

**Step 1: Add HideZoom event**

After line 18 (`public event Action? EndZoom;`), add:

```csharp
public event Action? HideZoom;
```

**Step 2: Fire HideZoom in OnZoomFixation**

In `OnZoomFixation` (lines 60-66), add `HideZoom?.Invoke();` before `ShowActionMenu`:

```csharp
public void OnZoomFixation(ScreenPoint precisePoint)
{
    if (CurrentState != InteractionState.ZoomSelectionActive) return;
    ZoomFixationPoint = precisePoint;
    HideZoom?.Invoke();
    ShowActionMenu?.Invoke(precisePoint);
    SetState(InteractionState.ZoomSelectionActionMenuVisible);
}
```

**Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "InteractionManagerTests" -v minimal`
Expected: All tests PASS

**Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Processing/InteractionManager.cs tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs
git commit -m "feat(zoom): add HideZoom event, fire before showing action menu in zoom mode"
```

---

### Task 9: Wire HideZoom in App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` — add HideZoom subscription near the EndZoom handling

**Step 1: Subscribe to HideZoom**

Find where `im.EndZoom` is subscribed (or where zoom events are handled). The EndZoom is implicitly handled by `zoomVM.IsVisible` changes. We need to add an explicit subscription.

After the zoom overlay `PropertyChanged` handler block (around line 291), add:

```csharp
im.HideZoom += () => Dispatcher.BeginInvoke(() =>
{
    zoomOverlay.Hide();
    zoomSideButtons.Hide();
});
```

**Step 2: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(zoom): hide zoom overlay when action menu appears in zoom mode"
```

---

### Task 10: Add render delay before screen captures

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:23`
- Modify: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs:18-21`

**Step 1: Make ActionMenuWindow.Show async with delay before capture**

The `Show` method calls `ScreenCaptureService.CaptureRegion` at line 47. We need to delay this capture. Since `Show` is called from a `PropertyChanged` handler (not awaited), we use a fire-and-forget `Dispatcher.BeginInvoke` approach.

Restructure `ActionMenuWindow.Show`: move the capture + preview creation into an async helper that runs after a short delay. The window positions and shows immediately, but the preview circle renders after the delay.

Replace line 45-61 (the capture and preview block) with:

```csharp
// Delay capture to let WPF render overlay visibility changes
var captureCenter = center;
var captureRadius = previewRadius;
var captureCanvasCenter = canvasCenter;
var captureCircleSize = previewCircleSize;
var captureBorderColor = borderColor;
var captureStyle = positionIndicatorStyle;

_ = Dispatcher.InvokeAsync(async () =>
{
    await Task.Delay(100);
    var capturedImage = ScreenCaptureService.CaptureRegion(captureCenter, captureRadius);
    if (capturedImage == null) return;

    var previewEllipse = new Ellipse
    {
        Width = captureCircleSize,
        Height = captureCircleSize,
        Fill = new ImageBrush(capturedImage) { Stretch = Stretch.Fill },
        Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(captureBorderColor)),
        StrokeThickness = 2
    };
    Canvas.SetLeft(previewEllipse, captureCanvasCenter.X - captureCircleSize / 2);
    Canvas.SetTop(previewEllipse, captureCanvasCenter.Y - captureCircleSize / 2);
    MenuCanvas.Children.Insert(0, previewEllipse); // Insert at back so buttons stay on top
});
```

Also move the position indicator creation (lines 63-116) BEFORE this async block so it renders immediately.

**Step 2: Make ZoomOverlayWindow capture async**

In `ZoomOverlayWindow.xaml.cs`, change `CaptureAndDisplay` (lines 39-44) to be async with delay:

```csharp
private async void CaptureAndDisplay(ScreenPoint center, int radius)
{
    await Task.Delay(100);
    var source = ScreenCaptureService.CaptureRegion(center, radius);
    if (source != null)
        ZoomBrush.ImageSource = source;
}
```

**Step 3: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded (may have CS1998 warning for async without await — Task.Delay IS awaited so this is fine)

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs
git commit -m "fix(capture): add 100ms render delay before screen capture

Prevents cursor circle from appearing in screenshots by giving WPF
time to render visibility changes before CopyFromScreen runs."
```

---

### Task 11: Defensive window visibility guards

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` — add explicit hide calls in overlay show handlers

**Step 1: Add guards in the action menu show handler**

In `App.xaml.cs`, in the `actionMenuVM.PropertyChanged` handler (line 333-353), inside the `if (actionMenuVM.IsVisible)` branch, add before `actionMenu.Show(...)`:

```csharp
// Defensive: ensure conflicting overlays are hidden
cursorCircle.Hide();
activationButton.HideImmediate();
```

**Step 2: Add guards in the zoom overlay show handler**

In the `zoomVM.PropertyChanged` handler (line 236-291), inside the `if (zoomVM.IsVisible)` branch, add before `zoomOverlay.Show(...)`:

```csharp
// Defensive: ensure conflicting overlays are hidden
cursorCircle.Hide();
activationButton.HideImmediate();
```

**Step 3: Add guard in the activation button show handler**

In the `activationVM.PropertyChanged` handler (line 210-231), inside the `if (activationVM.IsVisible)` branch, add before `activationButton.Show()`:

```csharp
// Defensive: ensure action menu is hidden
actionMenu.Hide();
```

**Step 4: Verify build and run all tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test EyeCtrl.slnx -v minimal`
Expected: Build succeeded, all tests pass

**Step 5: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "fix(overlays): add defensive visibility guards to prevent double windows

Explicitly hides conflicting overlays before showing new ones,
catching edge cases from timing races or unexpected state transitions."
```

---

### Task 12: Final verification

**Step 1: Run full test suite**

Run: `dotnet test EyeCtrl.slnx -v minimal`
Expected: All tests pass (existing 30 + 4 new = 34)

**Step 2: Build release**

Run: `dotnet build EyeCtrl.slnx -c Release`
Expected: Build succeeded, no warnings

**Step 3: Manual smoke test**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

Verify:
- Toolbar buttons: after dwelling on a button, it doesn't immediately re-trigger (400ms cooldown)
- Zoom mode (if enabled in settings): entering zoom gives time to orient before fixation triggers
- Action menu in zoom mode: zoom overlay is hidden when action menu appears
- Screen capture preview in action menu: no cursor circle visible
- No double/stale windows during state transitions
