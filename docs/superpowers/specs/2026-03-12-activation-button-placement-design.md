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

| Gaze quadrant | Nearest corner | Button direction |
|---|---|---|
| Top-left | Top-left | Bottom-right (+x, +y) |
| Top-right | Top-right | Bottom-left (−x, +y) |
| Bottom-left | Bottom-left | Top-right (+x, −y) |
| Bottom-right | Bottom-right | Top-left (−x, −y) |

The diagonal offset is applied at 45°:

```
dx = (gaze.X < screenW / 2) ? +1 : -1
dy = (gaze.Y < screenH / 2) ? +1 : -1

buttonCenterX = gaze.X + dx * offset / √2
buttonCenterY = gaze.Y + dy * offset / √2
```

The `offset` value is unchanged from the current formula: `circleRadius + 55` (pixels from gaze center to button center). The result is then clamped to screen bounds.

### Files changed

**`src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs`**

- `ShowAt(ScreenPoint position, double verticalOffset = 180)` → `ShowAt(ScreenPoint position, double offset)`
- Replace the vertical-only positioning with the quadrant-diagonal computation above.

**`src/EyeCtrl.App/App.xaml.cs`**

- Pass `buttonOffset` (already computed as `circleRadius + 55`) to `ShowAt` — no change to the value, only the behavior of how it's applied changes inside `ShowAt`.

### What does not change

- Button window size (100×100 px)
- Offset distance formula (`circleRadius + 55`)
- Fade-in/out animation
- Screen edge clamping (existing `Math.Clamp` calls)
- All other overlay windows

## Testing

- Manually verify button appears in the correct diagonal quadrant for each screen quadrant.
- Verify button does not overlap the cursor circle.
- Verify button stays within screen bounds when gaze is near an edge.
