# Activation Button Placement — Quadrant-Based Diagonal

**Date:** 2026-03-12
**Status:** Approved

## Problem

The activation button currently appears directly above the gaze cursor circle at a fixed vertical offset. This always places it in the same direction regardless of where on the screen the user is looking, which can cause it to appear off-screen or overlap with screen edges when gazing near the top.

## Requirements

1. The button must be offset from the circle (not overlapping it).
2. The button must appear on the **opposite side of the circle from the nearest screen corner** — so it stays away from edges and remains reachable.

## Design

### Approach: Strict quadrant diagonal

Divide the screen into 4 quadrants at its center. Based on which quadrant the gaze point falls in, select a diagonal direction pointing away from that quadrant's corner:

WPF screen coordinates: X increases rightward, Y increases downward. The table uses display-space labels (e.g., "Top-left" means smaller X and smaller Y in WPF coordinates).

| Gaze quadrant | Nearest corner | Button direction (display) | dx | dy |
|---|---|---|---|---|
| Top-left | Top-left | Bottom-right | +1 | +1 |
| Top-right | Top-right | Bottom-left | −1 | +1 |
| Bottom-left | Bottom-left | Top-right | +1 | −1 |
| Bottom-right | Bottom-right | Top-left | −1 | −1 |

`dy = +1` increases Y (moves visually down); `dy = -1` decreases Y (moves visually up).

**Tie-breaking:** when `gaze.X == screenW / 2` the `<` condition is false (else branch), giving `dx = -1` (button to the left). When `gaze.Y == screenH / 2`, `dy = -1` (button upward). These edge cases are rare and the behavior is acceptable.

The diagonal offset is applied at 45°. Screen dimensions come from `SystemParameters.PrimaryScreenWidth` / `PrimaryScreenHeight`. This feature operates on the primary screen only, consistent with the existing clamping code which already uses the same values. No DPI adjustment is applied — the app uses WPF logical units throughout and does not declare per-monitor DPI awareness, so `SystemParameters` values are consistent with the `Left`/`Top` coordinate space. Gaze coordinates outside the primary screen bounds are handled by the existing `Math.Clamp` calls. The quadrant is determined from the raw (unclamped) gaze point:

```
screenW = SystemParameters.PrimaryScreenWidth
screenH = SystemParameters.PrimaryScreenHeight

dx = (gaze.X < screenW / 2) ? +1 : -1
dy = (gaze.Y < screenH / 2) ? +1 : -1

buttonCenterX = gaze.X + dx * offset / √2
buttonCenterY = gaze.Y + dy * offset / √2
```

**Total distance preserved:** because `dx * offset/√2` and `dy * offset/√2` are the two legs of an isosceles right triangle, the hypotenuse equals `offset`. So the button center is always exactly `offset` pixels from the gaze center — the same distance as the current vertical implementation.

**Clearance:** the button's rendered visual is a circle of radius exactly 45px (from `ArcRadius = 45` in `ActivationButtonWindow.xaml.cs`), centered in the 100×100 window. `circleRadius = CircleSize / 2 = 20` (default `CircleSize = 40`). With defaults (circleRadius=20, offset = 20+55 = 75), clearance = offset − button visual radius − cursor radius = 75 − 45 − 20 = **10px**.

Then convert button center to top-left window origin and clamp to screen bounds. Use the XAML-declared `Width` and `Height` properties (both 100.0, type `double`), not `ActualWidth`/`ActualHeight` which can be 0 before first layout. `screenW` and `screenH` are `double` (matching `SystemParameters` return type):

```
Left = Math.Clamp(buttonCenterX - Width / 2,  0, screenW - Width)
Top  = Math.Clamp(buttonCenterY - Height / 2, 0, screenH - Height)
```

When gaze is at an extreme screen corner, clamping may pull the button slightly away from the ideal diagonal position, potentially reducing clearance from the cursor circle. This is acceptable — the same trade-off already exists in the current vertical implementation.

### Parameter change

The existing signature `ShowAt(ScreenPoint position, double verticalOffset = 180)` has a default value of 180. This default is intentionally removed in the new signature `ShowAt(ScreenPoint position, double offset)` — the concept of a "vertical offset" is replaced by an omnidirectional offset, making a direction-neutral default misleading. A codebase search confirms there is exactly one call site (`App.xaml.cs`), which always supplies the value explicitly — removing the default is safe.

### Files changed

**`src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs`**

- `ShowAt(ScreenPoint position, double verticalOffset = 180)` → `ShowAt(ScreenPoint position, double offset)`
- Replace the vertical-only positioning with the quadrant-diagonal computation above.

**`src/EyeCtrl.App/App.xaml.cs`**

- Pass `buttonOffset` (already computed as `circleRadius + 55`) to `ShowAt` — the existing call `activationButton.ShowAt(activationVM.Position, buttonOffset)` uses positional syntax (confirmed by codebase search), so the parameter rename does not affect the call site. No textual change is required here.

### What does not change

- Button window size (100×100 px)
- Offset distance formula (`circleRadius + 55`)
- Fade-in/out animation
- Screen edge clamping (existing `Math.Clamp` calls)
- All other overlay windows

## Testing

- Manually verify button appears in the correct diagonal quadrant for each screen quadrant.
- Verify button does not visually overlap the cursor circle under normal conditions.
- Verify button stays within screen bounds when gaze is near an edge.
- Verify correct placement at extreme corners where clamping engages.
