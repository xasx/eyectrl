# Zoom Mode Action Buttons Outside Circle Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** In Zoom Mode, action menu buttons must appear fully outside the zoom circle, not inside it.

**Architecture:** Add `ZoomDisplayRadius` to `ShowActionMenuMessage`. `GazeEngineAdapter` computes it from zoom settings when in zoom mode. `ActionMenuViewModel` derives `EffectiveRingRadius` from it. Both the VM (dwell hit positions) and `ActionMenuWindow` (rendering) use that radius. Screen-edge clamping margin updates accordingly.

**Tech Stack:** C# / .NET 8, CommunityToolkit.Mvvm, xUnit

---

### Task 1: Extend `ShowActionMenuMessage` and add `EffectiveRingRadius` to `ActionMenuViewModel`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`

**Background:** `ShowActionMenuMessage` is a C# record. We add an optional trailing field `int ZoomDisplayRadius = 0` — all existing call sites compile unchanged since it has a default. `ActionMenuViewModel` exposes `EffectiveRingRadius` (double, observable) computed as:
- `ZoomDisplayRadius > 0` → `ZoomDisplayRadius + ButtonRadius + 20` (buttons clear zoom circle by 20px)
- otherwise → `160` (existing default)

**Step 1: Write the failing tests**

In `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs`, add these two tests at the bottom of the class:

```csharp
[Fact]
public void EffectiveRingRadius_IsDefault_WhenNotZoom()
{
    var vm = new ActionMenuViewModel(_messenger);
    var actions = new List<InteractionAction> { InteractionAction.LeftClick };

    _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, false, 600));

    Assert.Equal(160.0, vm.EffectiveRingRadius);
}

[Fact]
public void EffectiveRingRadius_IsOutsideZoomCircle_WhenZoom()
{
    var vm = new ActionMenuViewModel(_messenger);
    var actions = new List<InteractionAction> { InteractionAction.LeftClick };

    // ZoomDisplayRadius = 300 (e.g., 150px * 2.0x magnification)
    _messenger.Send(new ShowActionMenuMessage(new ScreenPoint(500, 300), actions, true, 600, ZoomDisplayRadius: 300));

    // Expected: 300 + 50 (ButtonRadius) + 20 (gap) = 370
    Assert.Equal(370.0, vm.EffectiveRingRadius);
}
```

**Step 2: Run the tests to verify they fail**

```
dotnet test tests/EyeCtrl.ViewModels.Tests/ --filter "EffectiveRingRadius" -v minimal
```

Expected: compilation error — `EffectiveRingRadius` does not exist yet, and `ShowActionMenuMessage` doesn't have `ZoomDisplayRadius`.

**Step 3: Extend `ShowActionMenuMessage`**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, change line 8 from:

```csharp
public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs);
```

to:

```csharp
public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, int ZoomDisplayRadius = 0);
```

**Step 4: Add `EffectiveRingRadius` to `ActionMenuViewModel`**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`:

a) Add a new constant below the existing ones:

```csharp
private const double DefaultRingRadius = 160;
private const double ButtonOuterGap = 20;
```

b) Remove the existing `private const double RingRadius = 160;` line (it's being replaced).

c) Add an observable property alongside the others:

```csharp
[ObservableProperty] private double _effectiveRingRadius = DefaultRingRadius;
```

d) In `Receive(ShowActionMenuMessage message)`, add this line just before `var angleStep = ...`:

```csharp
EffectiveRingRadius = message.ZoomDisplayRadius > 0
    ? message.ZoomDisplayRadius + ButtonRadius + ButtonOuterGap
    : DefaultRingRadius;
```

e) In the same method, replace `RingRadius` with `EffectiveRingRadius` in the two position calculation lines:

```csharp
var bx = message.Center.X + EffectiveRingRadius * Math.Cos(angleRad);
var by = message.Center.Y + EffectiveRingRadius * Math.Sin(angleRad);
```

**Step 5: Run the tests to verify they pass**

```
dotnet test tests/EyeCtrl.ViewModels.Tests/ -v minimal
```

Expected: all tests pass (including existing ones — the new optional field doesn't break them).

**Step 6: Commit**

```
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs
git add tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelTests.cs
git commit -m "feat: action menu uses dynamic ring radius for zoom mode"
```

---

### Task 2: Update `GazeEngineAdapter` to pass `ZoomDisplayRadius` and fix clamping

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs`

**Background:** `OnShowActionMenu` currently hard-codes `const double margin = 210` (= RingRadius 160 + ButtonRadius 50). When in zoom mode the ring is wider, so the margin must update to prevent buttons clipping off-screen. The adapter already has access to `_settings.Zoom` to compute `ZoomDisplayRadius`.

**Step 1: Update `OnShowActionMenu` in `GazeEngineAdapter.cs`**

Replace the entire `OnShowActionMenu` method body with:

```csharp
private void OnShowActionMenu(ScreenPoint point) =>
    _dispatcher.BeginInvoke(() =>
    {
        var im = _interactionManager;
        var showBack = im.CurrentState == InteractionState.ZoomActionMenuVisible;
        var dwellMs = showBack
            ? _settings.Dwell.ZoomActionMenuDwellMs
            : _settings.Dwell.ActionMenuDwellMs;

        var zoomDisplayRadius = showBack
            ? (int)(_settings.Zoom.CircleRadiusPixels * _settings.Zoom.MagnificationLevel)
            : 0;

        // Margin keeps the full button ring on-screen.
        // Buttons fully outside zoom circle: effectiveRingRadius = zoomDisplayRadius + ButtonRadius(50) + gap(20)
        // Clamp uses: effectiveRingRadius + ButtonRadius(50)
        const double buttonRadius = 50;
        const double buttonOuterGap = 20;
        const double defaultRingRadius = 160;
        var effectiveRingRadius = zoomDisplayRadius > 0
            ? zoomDisplayRadius + buttonRadius + buttonOuterGap
            : defaultRingRadius;
        var margin = effectiveRingRadius + buttonRadius;

        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        var clampedPoint = new ScreenPoint(
            Math.Clamp(point.X, margin, screenW - margin),
            Math.Clamp(point.Y, margin, screenH - margin));

        _messenger.Send(new ShowActionMenuMessage(clampedPoint, _settings.ActionMenu.VisibleActions, showBack, dwellMs, zoomDisplayRadius));
    });
```

**Step 2: Build the solution**

```
dotnet build EyeCtrl.slnx -v minimal
```

Expected: builds with 0 errors.

**Step 3: Commit**

```
git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
git commit -m "feat: pass zoom display radius in action menu message, fix screen clamping"
```

---

### Task 3: Update `ActionMenuWindow` and `App.xaml.cs` to use the dynamic ring radius

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Background:** `ActionMenuWindow.Show()` independently renders buttons using its own `const RingRadius = 160`. This must accept the effective ring radius from the VM. `App.xaml.cs` calls `actionMenu.Show(...)` and must pass `actionMenuVM.EffectiveRingRadius`. Since `EffectiveRingRadius` is set in `Receive()` before `IsVisible = true`, it's already correct when `App.xaml.cs` reacts to the `IsVisible` property change.

**Step 1: Update `ActionMenuWindow.Show()` signature and layout**

In `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`:

a) Remove `private const double RingRadius = 160;` (line 13).

b) Change the `Show()` signature from:

```csharp
public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs)
```

to:

```csharp
public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius)
```

c) In the body of `Show()`, replace the two uses of `RingRadius` with `ringRadius`:

```csharp
var bx = canvasCenter.X + ringRadius * Math.Cos(angleRad);
var by = canvasCenter.Y + ringRadius * Math.Sin(angleRad);
```

**Step 2: Update `App.xaml.cs` to pass `EffectiveRingRadius`**

In `src/EyeCtrl.App/App.xaml.cs`, find the `actionMenu.Show(...)` call and add the ring radius argument:

```csharp
actionMenu.Show(actionMenuVM.Center,
    settings.ActionMenu.VisibleActions,
    actionMenuVM.ShowBack,
    actionMenuVM.ShowBack ? settings.Dwell.ZoomActionMenuDwellMs : settings.Dwell.ActionMenuDwellMs,
    actionMenuVM.EffectiveRingRadius);
```

**Step 3: Build the solution**

```
dotnet build EyeCtrl.slnx -v minimal
```

Expected: builds with 0 errors.

**Step 4: Run all tests**

```
dotnet test EyeCtrl.slnx -v minimal
```

Expected: all tests pass.

**Step 5: Commit**

```
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: render action buttons outside zoom circle"
```
