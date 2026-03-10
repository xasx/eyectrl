# Timing & Window Management Fixes

## Problem

VM testing with mouse simulation revealed several UX timing issues and visual glitches:

1. **Rapid re-triggering** — Toolbar buttons (and potentially other dwell targets) re-trigger immediately after activation because `DwellDetector` has no cooldown. The gaze is still over the button, so accumulation restarts on the next frame.
2. **Zoom fixation too fast** — When zoom mode activates, the user's gaze is already focused on the target area. The 300ms fixation threshold triggers almost instantly, not giving the user time to orient in the magnified view.
3. **Zoom visible behind action menu** — During `ZoomSelectionActionMenuVisible`, the zoom overlay stays visible underneath the action menu. It should be hidden.
4. **Cursor circle in screenshots** — WPF rendering race: the cursor circle hides via state change, but `CopyFromScreen` may execute before WPF renders the hidden state, capturing the circle in the screenshot.
5. **Possible double windows** — Although overlays are singletons, defensive guards are needed to ensure at most one overlay of each type is visible at any time.

## Fix 1: Configurable Dwell Cooldown

### Setting

Add `DwellCooldownMs` to `DwellSettings` with default value `400`.

### Implementation

In `DwellDetector`:
- Add `_lastActivationTimestamp` (long?) field, set when `TargetActivated` fires.
- In `Update()`, before processing any target, check: if `_lastActivationTimestamp` is set and `(currentTimestamp - _lastActivationTimestamp) < cooldownMicroseconds`, skip all dwell accumulation (reset progress to 0, return early).
- Once the cooldown elapses, clear `_lastActivationTimestamp` and resume normal processing.

This applies universally to all `DwellDetector` instances (toolbar, action menu, activation button, zoom buttons). Each instance gets the same cooldown. The cooldown only blocks the detector that just fired — other detectors are unaffected (they are separate instances).

### Why 400ms default

- Long enough to prevent accidental double-trigger (a typical saccade + refixation takes ~200-300ms).
- Short enough to not feel sluggish if the user intentionally re-dwells.
- Configurable, so it can be tuned with real eye-tracker testing.

## Fix 2: Zoom Grace Period

### Setting

Add `ZoomGracePeriodMs` to `ZoomSettings` with default value `500`.

### Implementation

In `App.xaml.cs` where the zoom `FixationDetector` is wired to `CursorMoved`:
- Record `zoomStartTimestamp` when zoom activates.
- In the `CursorMoved` handler, skip feeding gaze data to the zoom `FixationDetector` until `(currentTimestamp - zoomStartTimestamp) >= gracePeriodMicroseconds`.

This gives the user time to visually orient in the magnified view before fixation detection begins. The 500ms default accounts for the visual processing time needed to parse a suddenly magnified view.

## Fix 3: Hide Zoom Overlay During Action Menu

### Implementation

In `InteractionManager`:
- Add a new `HideZoom` event (or reuse the existing event pattern).
- `OnZoomFixation()`: fire `HideZoom` before firing `ShowActionMenu`.
- `OnZoomBack()`: already fires `StartZoom` which re-shows the zoom overlay. No change needed.
- `OnActionSelected()` in zoom mode: already fires `EndZoom`. No change needed.

In `App.xaml.cs`:
- Subscribe to `HideZoom` and call `zoomOverlay.Hide()` + `zoomSideButtons.Hide()`.

The action menu already captures its own screen region at the fixation point, so the zoom overlay provides no visual value once the action menu appears.

## Fix 4: Render Delay Before Screenshots

### Implementation

In both `ActionMenuWindow.Show()` and `ZoomOverlayWindow.CaptureAndDisplay()`:
- After hiding overlays (cursor circle, activation button), introduce a ~100ms delay before calling `CopyFromScreen`.
- Use `await Task.Delay(100)` if the method can be made async, or a `DispatcherTimer` one-shot callback if not.

100ms covers ~6 WPF render frames at 60fps, providing ample time for the visibility change to render.

### Scope

Only the cursor circle is the concern — it's the only overlay visible during `Fixation` state that must be hidden before capture. The activation button hides via the same state change mechanism, so the same delay protects it.

## Fix 5: Defensive Window Visibility Guards

### Implementation

In `App.xaml.cs` message handlers, add explicit hide calls before showing conflicting overlays:

| When showing...       | Explicitly hide...                                  |
|-----------------------|-----------------------------------------------------|
| Action menu           | Activation button, cursor circle, zoom overlay (if zoom mode) |
| Zoom overlay          | Activation button, cursor circle                    |
| Activation button     | Action menu (shouldn't happen, but defensive)       |

In `OverlayWindow` base class or each overlay's `Show()`:
- Guard against double-show (if already visible, skip or re-position only).

This is purely defensive — the state machine should already handle these transitions correctly, but the guards catch edge cases from timing races or unexpected state transitions.

## Settings Summary

| Setting | Location | Default | Purpose |
|---------|----------|---------|---------|
| `DwellCooldownMs` | `DwellSettings` | 400 | Post-activation cooldown before dwell restarts |
| `ZoomGracePeriodMs` | `ZoomSettings` | 500 | Delay before zoom fixation detection begins |

## Testing Notes

- All timing values are configurable for tuning with real eye-tracker hardware.
- VM mouse testing can validate the structural fixes (zoom hiding, screenshot race, window guards).
- Real eye-tracker testing needed to fine-tune cooldown and grace period defaults.
