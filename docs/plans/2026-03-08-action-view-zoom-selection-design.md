# Action View & Zoom Selection Mode — Design

**Date:** 2026-03-08
**Status:** Approved

## Overview

Redesign the Action Menu and Zoom Mode to introduce a unified **Action View** — a screen-preview circle with radial action buttons — and rename Zoom Mode to **Zoom Selection Mode** to clarify its purpose.

The Action View replaces the cursor circle when shown. Inside the circle: a 1:1 screen capture of the target area with a position indicator (crosshair or pin). Outside the circle: radial action buttons, same as today.

## Flows

### Non-Zoom Flow

1. Fixate on screen → activation button appears
2. Dwell on activation button (700ms)
3. Cursor circle hides. Action View appears at fixation point:
   - Circle filled with 1:1 screen capture centered on target
   - Crosshair/pin indicator at center
   - Radial action buttons around the circle
4. Dwell on action → execute at target point, return to FreeLook, cursor circle reappears

### Zoom Selection Flow

1. Fixate on screen → activation button appears
2. Dwell on activation button (700ms)
3. Cursor circle hides. Zoom View appears:
   - Magnified static screenshot (existing capture behavior)
   - Close (✕) and Re-capture (↻) buttons on the right side, outside the circle
   - Gaze passes through the circle (click-through) for precise fixation
4. Fixate on precise spot inside magnified area
5. Zoom View hides. Action View appears at the real (unmagnified) screen point:
   - 1:1 screen capture preview, crosshair/pin indicator
   - Radial action buttons including Back (↩)
6. Dwell on action → execute, return to FreeLook, cursor circle reappears
7. Or dwell on Back (↩) → Action View hides, Zoom View reappears (re-captured at same original center)

## State Machine

```
FreeLook
  → Fixation (fixation detected) — show activation button

Fixation
  → FreeLook (fixation lost) — hide activation button
  → ActionMenuVisible (activation dwelled, zoom OFF)
      hide activation button, hide cursor circle, show Action View
  → ZoomSelectionActive (activation dwelled, zoom ON)
      hide activation button, hide cursor circle, show Zoom View

ZoomSelectionActive
  → FreeLook (close button dwelled) — hide Zoom View, show cursor circle
  → ZoomSelectionActionMenuVisible (fixation in zoom area)
      hide Zoom View, show Action View with back button

ZoomSelectionActionMenuVisible
  → ZoomSelectionActive (back button dwelled)
      hide Action View, show Zoom View (re-capture at same center)
  → FreeLook (action selected)
      hide Action View, show cursor circle, execute action

ActionMenuVisible
  → FreeLook (action selected)
      hide Action View, show cursor circle, execute action
  → FreeLook (dismissed)
      hide Action View, show cursor circle
```

State enum renames:
- `ZoomActive` → `ZoomSelectionActive`
- `ZoomFixation` → `ZoomSelectionFixation` (NOTE: This state was later removed as unused - see architecture review)
- `ZoomActionMenuVisible` → `ZoomSelectionActionMenuVisible`

## Architectural Approach

**Evolve `ActionMenuWindow` into Action View** (Approach A). The existing window already owns radial button layout, dwell detection, back button, and screen-edge clamping. Extend it with a screen capture circle in the center.

### Action View — Enhanced ActionMenuWindow

Added to the center of the canvas:
1. **Screen capture** — GDI `CopyFromScreen` (same approach as `ZoomOverlayWindow`). Capture a square region sized `CircleSize` centered on the target point. Display clipped to an ellipse.
2. **Position indicator** — crosshair or pin icon at the exact center. Configurable via `PositionIndicatorStyle` setting.
3. **Cursor circle hides** — `CursorCircleViewModel` listens for `ShowActionMenuMessage` / `HideActionMenuMessage` to toggle visibility.

Ring radius becomes computed: `EffectiveRingRadius = CircleSize/2 + ButtonRadius + ButtonOuterGap`. With defaults (250/2 + 50 + 20 = 195px), replacing hardcoded `DefaultRingRadius = 160`.

Everything else unchanged: radial layout, dwell detection, progress arcs, back button, screen-edge clamping.

### Zoom View Changes

**Removed:** No action buttons around the zoom circle. Zoom fixation transitions to Action View instead.

**Added — Side buttons (right side, outside the circle):**
- Close (✕) — dwellable, top
- Re-capture (↻) — dwellable, below close
- Same style as existing buttons: circular, dark background, progress arc, Unicode symbol
- Button size: existing `ButtonRadius` (50px radius / 100px diameter)
- Position: `circle right edge + gap`, vertically centered around midpoint

**Dwell detection:** Zoom View gets its own `DwellDetector` for the two side buttons (same pattern as `ActivationButtonViewModel`). Reuses `ActionMenuDwellMs` for dwell time.

**Re-capture:** Calls existing `CaptureAndDisplay()` at same `_originalCenter`. Circle stays in place, screenshot refreshes.

**Back from Action View:** Returns to Zoom View re-captured at the same `_originalCenter` (not the precise point), so the user sees the same magnified area.

## Settings

### New

| Setting | Type | Default | Location |
|---|---|---|---|
| `PositionIndicatorStyle` | enum: `Crosshair`, `Pin` | `Crosshair` | `CursorSettings` |

### Existing (now affect Action View)

| Setting | Effect on Action View |
|---|---|
| `CircleSize` (250) | Preview circle diameter (same as cursor circle) |
| `ActionMenuDwellMs` (600) | Dwell time for action buttons + zoom side buttons |
| `ZoomActionMenuDwellMs` (600) | Dwell time for action buttons in zoom selection |
| `CircleColor` (#00FF00) | Preview circle border color |

### Computed

| Value | Formula | Default Result |
|---|---|---|
| `EffectiveRingRadius` (non-zoom) | `CircleSize/2 + ButtonRadius + ButtonOuterGap` | 195px |
| `EffectiveRingRadius` (zoom selection) | same formula | 195px |
