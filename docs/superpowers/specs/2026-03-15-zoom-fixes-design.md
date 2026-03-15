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

**File:** `src/EyeCtrl.App/App.xaml.cs`, inside `zoomGazeHandler` (after grace period check, before `zoomFixation.Update`).

Add a boundary test using physical pixel coordinates (both `pt` from `gazeEngine.CursorMoved` and `zoomVM.Center` / `zoomVM.Radius` are in physical pixels):

```
dx = pt.X - zoomVM.Center.X
dy = pt.Y - zoomVM.Center.Y
insideCircle = (dx² + dy²) <= zoomVM.Radius²
```

**If outside circle:**
- Call `zoomFixation.Reset()` — clears accumulated fixation state, `GetProgress()` returns 0.
- Dispatch to UI thread: `zoomIndicatorVM.ResetFixation()` + `zoomIndicator.AnimateReset(...)` — resets visual progress indicator.
- Return early; skip `zoomFixation.Update(pt, ts)` and the progress update block.

**If inside circle:**
- Proceed as today: `zoomFixation.Update(pt, ts)`, update progress.

**Indicator visibility:** The indicator position update (`UpdateGazePosition` / `UpdatePosition`) runs unconditionally before the boundary check, so the indicator always tracks gaze even outside the circle.

**No changes to:** `FixationDetector`, `ZoomGazeIndicatorViewModel`, `ZoomOverlayWindow`, or any other class.

---

## Fix 2 — Action menu preview shows zoom region

### Problem

`ActionMenuWindow.Show()` captures a screen region centered on `precisePoint` (the exact fixation spot) with radius `previewCircleSize / 2`. The zoom view was centered on `_originalCenter` (the zoom entry point) with `CircleRadiusPixels`. These are different centers and different radii, so the action menu preview shows a different — and smaller — region of the screen.

### Design

#### 1. Extend `ShowActionMenuMessage`

Add two nullable fields:

```csharp
public ScreenPoint? ZoomCenter { get; init; }
public int? ZoomRadius { get; init; }
```

Non-zoom action menu paths leave these null (no behavior change).

#### 2. Populate in `App.xaml.cs`

In `OnZoomFixation` wiring (where `ShowActionMenu?.Invoke(precisePoint)` leads to sending `ShowActionMenuMessage`), populate the new fields:

```csharp
ZoomCenter = im.FixationPoint,       // physical pixels — zoom entry center
ZoomRadius = settings.Zoom.CircleRadiusPixels
```

`im.FixationPoint` is the screen point stored by `InteractionManager` when zoom was entered — it is already available.

#### 3. Pass to `ActionMenuWindow.Show()`

Add optional parameters to `ActionMenuWindow.Show()`:

```csharp
ScreenPoint? zoomCenter = null, int? zoomRadius = null
```

#### 4. Capture logic in `ActionMenuWindow`

When `zoomCenter != null` and `zoomRadius != null`:

- `captureCenter` = `zoomCenter` (instead of `precisePoint`)
- `captureRadius` = `zoomRadius * DpiHelper.ScaleX` (physical pixels, matching zoom view capture)

When null: existing behavior unchanged.

#### 5. Crosshair / pin offset

The position indicator must show where within the preview the precise fixation point is.

Scale factor (actual screen pixels → display pixels within preview circle):

```
scale = previewCircleSize / (2.0 * captureRadius)
```

Offset from preview center in display pixels:

```
offsetX = (precisePoint.X - zoomCenter.X) * scale
offsetY = (precisePoint.Y - zoomCenter.Y) * scale
```

Draw the crosshair/pin at `(canvasCenter.X + offsetX, canvasCenter.Y + offsetY)` instead of `canvasCenter`.

Clamp offset magnitude to `previewCircleSize / 2 - margin` to keep the indicator inside the circle even at extreme positions.

#### Files changed

| File | Change |
|------|--------|
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `ZoomCenter`, `ZoomRadius` to `ShowActionMenuMessage` |
| `src/EyeCtrl.App/App.xaml.cs` | Populate new fields when sending `ShowActionMenuMessage` from zoom fixation |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add `zoomCenter`/`zoomRadius` params to `Show()`; use for capture center/radius; offset crosshair/pin |

No changes to `InteractionManager`, `ActionMenuViewModel`, `ZoomOverlayWindow`, or non-zoom action menu paths.

---

## Out of scope

- Changing how the zoom circle is entered or exited
- Changing the zoom magnification or capture logic
- Any changes to the action menu button layout or dwell logic
