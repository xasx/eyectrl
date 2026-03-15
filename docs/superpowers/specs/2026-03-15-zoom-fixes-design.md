# Zoom View Bug Fixes — Design Spec

**Date:** 2026-03-15
**Status:** Approved

---

## Summary

Two bugs in zoom view:

1. **Fixation outside circle** — gaze outside the zoom circle accumulates fixation and can trigger the action menu from a region not visible in the zoom view.
2. **Action menu preview mismatch** — the action menu's center preview re-captures at the precise fixation point with a small radius, showing a different screen region than what the zoom view displayed.

---

## Fix 1 — Restrict fixation to inside zoom circle

### Problem

In `App.xaml.cs`, `zoomGazeHandler` calls `zoomFixation.Update(pt, ts)` unconditionally after the grace period. There is no check whether `pt` is within the zoom circle. Gaze outside the circle accumulates fixation progress normally and can fire `FixationStarted`, triggering the action menu at a point the user was not aiming at inside the zoom view.

### Design

**File:** `src/EyeCtrl.App/App.xaml.cs`, inside `zoomGazeHandler`.

`pt` from `gazeEngine.CursorMoved` is in physical pixels. `zoomVM.Center` is also in physical pixels. `zoomVM.Radius` stores `settings.Zoom.CircleRadiusPixels`, which is a logical/DIP value and must be converted to physical pixels for comparison.

Insert the following boundary check immediately after the grace period `return` (i.e., after `if ((ts - zoomStartTimestamp) < gracePeriodUs) return;`). The early return skips both `zoomFixation.Update(pt, ts)` and the progress update block that follows it — they must be treated as a unit:

```
physicalRadius = settings.Zoom.CircleRadiusPixels * DpiHelper.ScaleX
dx = pt.X - zoomVM.Center.X
dy = pt.Y - zoomVM.Center.Y
if (dx² + dy² > physicalRadius²):
    zoomFixation.Reset()
    Dispatcher.BeginInvoke(() =>
        zoomIndicatorVM.ResetFixation()
        zoomIndicator.AnimateReset(settings.Zoom.GazeIndicatorOuterDiameter,
                                   settings.Zoom.GazeIndicatorResetAnimationMs))
    return   // skips zoomFixation.Update AND the progress update block below
```

**Why explicit UI reset is required:** `FixationDetector.Reset()` does not fire `FixationEnded`. Without the explicit `zoomIndicatorVM.ResetFixation()` + `zoomIndicator.AnimateReset(...)` call, the visual progress ring stays non-zero when gaze exits the circle.

**Indicator visibility:** The indicator position update (`UpdateGazePosition` / `UpdatePosition`) runs before the grace period check and before this boundary check, so the indicator always tracks gaze even outside the circle — no change needed there.

**No changes to:** `FixationDetector`, `ZoomGazeIndicatorViewModel`, `ZoomOverlayWindow`, or any other class.

---

## Fix 2 — Action menu preview shows zoom region

### Problem

`ActionMenuWindow.Show()` captures a screen region centered on `center` (the precise in-zoom fixation point) with radius `previewCircleSize / 2`. The zoom view was centered on the zoom entry point with `CircleRadiusPixels`. These are different centers and different radii, so the action menu preview shows a different — and smaller — region of the screen.

### Design

The data flows through four layers: `GazeEngineAdapter` → `ShowActionMenuMessage` → `ActionMenuViewModel` → `App.xaml.cs` → `ActionMenuWindow.Show()`.

#### 1. Extend `ShowActionMenuMessage`

`ShowActionMenuMessage` is a positional record. Add `ZoomCenter` and `ZoomRadius` as **init-only properties** (not positional parameters) so existing call sites require no changes:

```csharp
public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions,
    bool ShowBack, int DwellTimeMs, int ZoomDisplayRadius = 0, double CircleSize = 250)
{
    public ScreenPoint? ZoomCenter { get; init; }
    public int? ZoomRadius { get; init; }   // logical/DIP value (CircleRadiusPixels)
}
```

Non-zoom call sites (no `ZoomCenter`/`ZoomRadius`) get `null` by default — no behavior change.

#### 2. Populate in `GazeEngineAdapter.cs`

`ShowActionMenuMessage` is sent from `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` (line 101). The zoom path is identified by `showBack == true`. In that path, populate the new init-only properties using object initializer syntax:

```csharp
_messenger.Send(new ShowActionMenuMessage(...) {
    ZoomCenter = showBack ? _interactionManager.FixationPoint! : null,
    ZoomRadius = showBack ? _settings.Zoom.CircleRadiusPixels : null
});
```

`_interactionManager.FixationPoint` is in physical pixels (gaze coordinate) and is always non-null in the zoom path (`showBack == true`) since it is set before zoom activates. `_settings.Zoom.CircleRadiusPixels` is a logical/DIP value.

#### 3. Store in `ActionMenuViewModel`

`ActionMenuViewModel` receives `ShowActionMenuMessage` and stores the new fields as properties:

```csharp
public ScreenPoint? ZoomCenter { get; private set; }
public int? ZoomRadius { get; private set; }
```

Populated from the message when it arrives.

#### 4. Pass from `App.xaml.cs` to `ActionMenuWindow.Show()`

`App.xaml.cs` calls `actionMenu.Show(...)` when `actionMenuVM.IsVisible` becomes true (line 423). Pass the new values:

```csharp
actionMenu.Show(actionMenuVM.Center, ..., zoomCenter: actionMenuVM.ZoomCenter, zoomRadius: actionMenuVM.ZoomRadius);
```

#### 5. Capture logic in `ActionMenuWindow.Show()`

Add optional parameters:

```csharp
ScreenPoint? zoomCenter = null, int? zoomRadius = null
```

When `zoomCenter != null` and `zoomRadius != null`:

- `captureCenter` = `zoomCenter` (instead of `center`, the precise fixation point)
- `captureRadius` = `(int)(zoomRadius.Value * DpiHelper.ScaleX)` — converts logical DIP radius to physical pixels, matching the zoom view's capture

When null: existing behavior unchanged (`captureCenter = center`, `captureRadius = previewRadius`).

#### 6. Crosshair / pin offset

The existing `center` parameter of `Show()` is the precise fixation point (in physical pixels). The position indicator must show where within the preview this point lies.

`center` and `zoomCenter` are in physical pixels. `previewCircleSize` (and `canvasCenter`) is in logical display pixels. `zoomRadius` is a logical/DIP value. `captureRadius = zoomRadius.Value * DpiHelper.ScaleX` is in physical pixels.

To keep units consistent, the scale factor must use `zoomRadius.Value` (logical) in the denominator, not the already-scaled `captureRadius` — otherwise the scale would be wrong at non-100% DPI:

```
scale = previewCircleSize / (2.0 * zoomRadius.Value)
```

This gives: (logical display px) / (logical px) × (physical px offset) = (logical display px offset). Correct for Canvas positioning.

Offset from preview center in display pixels:

```
offsetX = (center.X - zoomCenter.X) * scale
offsetY = (center.Y - zoomCenter.Y) * scale
```

Clamp: if `sqrt(offsetX² + offsetY²) > previewCircleSize / 2 - margin`, scale down proportionally.

**Crosshair:** shift all four line endpoints by `(offsetX, offsetY)`.

**Pin:** shift all pin sub-elements (pinHead ellipse and pinLine) by `(offsetX, offsetY)` uniformly, so the pin tip lands at `(canvasCenter.X + offsetX, canvasCenter.Y + offsetY)`.

When `zoomCenter` is null, draw at `canvasCenter` as today.

#### Files changed

| File | Change |
|------|--------|
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `ZoomCenter`, `ZoomRadius` init-only properties to `ShowActionMenuMessage` |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | Populate new properties in zoom path (`showBack == true`) |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Store and expose `ZoomCenter`, `ZoomRadius` from message |
| `src/EyeCtrl.App/App.xaml.cs` | Pass `actionMenuVM.ZoomCenter` and `actionMenuVM.ZoomRadius` to `actionMenu.Show()` |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add optional params; use zoom context for capture center/radius; offset crosshair/pin |

No changes to `InteractionManager`, `ZoomOverlayWindow`, or non-zoom action menu paths.

---

## Out of scope

- Changing how the zoom circle is entered or exited
- Changing the zoom magnification or capture logic
- Any changes to the action menu button layout or dwell logic
