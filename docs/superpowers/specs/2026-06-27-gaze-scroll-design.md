# Gaze Scroll Mode — Design Spec

## Problem

Current scrolling is discrete and cumbersome: fixate → activation button → action menu → scroll button → 3 notches. Repeating this for every scroll breaks flow and makes continuous navigation painful.

## Solution Overview

A "gaze joystick" scroll mode triggered from the action menu. Once active, the user's gaze offset from a fixed anchor controls 2D scroll velocity in real time. A dedicated stop button exits scroll mode.

## Interaction Model

### Entry
- User dwells on a "Scroll Mode" button in the radial action menu.
- The **anchor is the fixation/centroid that opened the action menu**, NOT the gaze point at the moment the button is dwelled. At the moment of the dwell the user is looking at the menu button, not at the content they want to scroll; using the button position would center the dead zone on the button and cause an immediate scroll kick. The opening fixation is available as `GazeEngine.StableCentroid` (already used for the same purpose in `EventCoordinator.HandleActionRequested`). Fall back to the dwelled point only if the centroid is null.
- The interaction state switches to `InteractionState.ScrollMode` (see Cursor Freeze below).
- The OS cursor is pinned at the anchor with a single `MoveCursor(anchor)` call on entry.
- Cursor circle overlay locks to the anchor position.

### Scrolling
- User looks away from the anchor to scroll in that direction.
- Gaze offset → 2D scroll velocity (linear mapping).
- ~30px dead zone around the anchor where no scrolling occurs.
- Outside dead zone: velocity increases linearly with distance from anchor.
- Diagonal gaze offset produces simultaneous horizontal + vertical scrolling.
- Cursor circle remains locked at anchor — does not follow gaze movement.

### Exit
- User dwells on the stop button (fixed position: right edge of screen, vertically centered).
- **Stop zone:** when gaze is within a radius of the stop button, scroll velocity is forced to zero. Without this, looking over to the stop button commands a large rightward scroll exactly while the user is trying to quit. The stop zone must be large enough to fully suppress scrolling before the dwell completes.
- On exit: state returns to `InteractionState.FreeLook`, OS cursor unfreezes (resumes following gaze automatically), cursor circle unlocks, scroll overlay hides, the 60Hz scroll worker stops.

## Visual Feedback (Option D)

Feedback is shown entirely within the cursor circle, which is locked at the anchor position:

1. **Anchor dot** — a small dot moves inside the circle showing the user's actual gaze position relative to the anchor. Centered = no scroll. Drifting toward the edge = scrolling in that direction. Distance from center = scroll speed.
2. **Ring thickness** — the circle's border thickens in the scroll direction, providing a secondary visual channel reinforcing direction.
3. **Stop button** — small dwell target on the right edge of the screen, fixed Y position (centered vertically), with its surrounding stop zone.

This avoids a feedback loop (cursor circle doesn't follow gaze) and keeps all directional information in one stable visual element. Note the feedback is driven by the *filtered* gaze point (OneEuroFilter output), since that is what `GazeEngine` emits — see Gaze Pipeline.

## Cursor Freeze — How It Actually Works

> **Correction from prior draft.** The OS cursor is NOT moved by `EventCoordinator`. It is moved inside `GazeEngine.OnGazePoint` (`src/EyeCtrl.Core/GazeEngine.cs`) via `_inputSimulator.MoveCursor(filtered)`, gated on `InteractionManager.CurrentState == InteractionState.FreeLook`. `EventCoordinator`'s gaze handler only forwards gaze to the toolbar/keyboard/settings windows. Therefore "suppress MoveCursor in EventCoordinator" is impossible and is removed.

Freezing the cursor = **not being in `FreeLook`**, exactly like the existing `Paused` state already does. The mechanism:

- Add `ScrollMode` to the `InteractionState` enum (`src/EyeCtrl.Core/Models/InteractionState.cs`).
- `InteractionManager` (`src/EyeCtrl.Core/Processing/InteractionManager.cs`) owns the transition into and out of `ScrollMode`, consistent with how `ActionMenuVisible`, `ManualSelectionActive`, and `Paused` are handled. Do NOT build a second, parallel state machine inside `EventCoordinator` — it would race the existing one (e.g. a fixation during scroll could still push `InteractionManager` into `ActionMenuVisible`). While in `ScrollMode`, `InteractionManager` must ignore fixation-driven transitions.
- Because `GazeEngine` only calls `MoveCursor` in `FreeLook`, entering `ScrollMode` stops cursor movement for free. `GazeEngine` keeps firing `CursorMoved` (filtered point) in every state, so the scroll engine and overlays still receive gaze.

## Gaze Pipeline

Gaze arrives at ~30Hz from the eye tracker. The existing OneEuroFilter on the gaze pipeline dampens input jitter; `GazeEngine.CursorMoved` carries the filtered point. The filter adds some lag during fast gaze movement, which is acceptable for joystick-style control (the offset is a velocity command, not a position click).

## Scroll Engine

`ScrollEngine` (in framework-agnostic Core) is **pure math only** — offset → fractional scroll-velocity. It does NOT own threads or call the input simulator. The 60Hz worker and `IInputSimulator.Scroll()` calls live in the App/Platform layer (see Smoothing), keeping Core free of timers and input concerns.

Input: gaze position + anchor position + sensitivity setting.
Output: a fractional target velocity (vx, vy) in **scroll notches per second**.

Processing per gaze sample:
1. Compute offset vector (dx, dy) from anchor to gaze position.
2. Compute distance from anchor.
3. If distance < dead zone radius (~30px), emit zero velocity.
4. Otherwise: effective = distance - dead zone radius.
5. Apply sensitivity (see Settings for the agreed scale): `magnitude = effective × sensitivityFactor`.
6. Clamp magnitude to [0, maxVelocity].
7. Decompose along the offset direction: `vx = (dx / distance) × magnitude`, `vy = (dy / distance) × magnitude`.

These velocities are **fractional** and stay fractional — quantization to integer notches happens only in the worker (next section).

## Smoothing & Integer-Delta Accumulation

At 30Hz gaze input, raw deltas would produce jerky scrolling. A scroll worker runs at ~60Hz (owned by App/Platform, not Core):
- Each gaze sample updates a shared *target* velocity (written from the WPF dispatcher thread; read from the worker thread — guard with a lock or volatile reference).
- On each ~16ms tick the worker lerps current velocity toward target velocity.
- The worker converts velocity (notches/sec) to a per-tick delta in fractional notches, **accumulates the fractional remainder across ticks**, emits only the integer part to `IInputSimulator.Scroll(anchor, intDeltaX, intDeltaY)`, and carries the fraction forward.

**Why accumulation is mandatory:** `IInputSimulator.Scroll(PhysicalPoint, int deltaX, int deltaY)` takes integer notches and multiplies by `WHEEL_DELTA` (120) internally (`src/EyeCtrl.Platform/Input/Win32InputSimulator.cs`). If each tick truncated the fractional velocity to int, slow scrolling near the dead-zone edge would round to 0 (no movement) and overall motion would be jerky — defeating the 60Hz worker entirely. Carrying the remainder yields smooth slow scrolling.

Magnitude sanity check: 1 notch/tick × 60Hz = 60 notches/s is already brisk; `maxVelocity` and the sensitivity range must be tuned so typical offsets land well under that.

Worker lifecycle: started on `ScrollMode` entry, stopped on exit, on app shutdown, and on dispose.

## Settings

One sensitivity slider in `EyeCtrlSettings`:
- Range: 0.5x to 2.0x (default 1.0x), exposed in the UI.
- The slider value (0.5–2.0) is a multiplier on a fixed internal base factor that converts pixels-of-offset to notches/sec. (Resolves the earlier contradiction between a "0.5x–2.0x" slider and an `offset × sensitivity/100` formula — there is one base constant, and the slider scales it.)

### Settings wiring checklist (project convention — all required)
- `src/EyeCtrl.Core/Settings/ScrollSettings.cs` — `ObservableObject + IValidatable`, `partial void OnXChanging` guard on the sensitivity range, `Validate()`.
- `EyeCtrlSettings.cs` — add `[ObservableProperty] private ScrollSettings _scroll = new();`.
- `EyeCtrlSettings.Validate()` — add a `Scroll` null-check + `errors.AddRange(Scroll.Validate())` branch (it enumerates *every* sub-setting).
- `SettingsViewModel.ResetAllSettings()` — add `CopySettings(defaults.Scroll, _settings.Scroll);` (it resets *every* sub-setting; omitting this silently skips scroll on reset).
- `SettingsViewModel` tab building — add a `SliderSettingItemViewModel` for the sensitivity slider, or it never appears in the Settings UI.

## Action Menu Entry

- Add a `ScrollMode` value to the `InteractionAction` enum (`src/EyeCtrl.Core/Models/InteractionAction.cs`).
- Add it to the available/default visible action lists in `ActionMenuSettings` (`src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`) so it can appear in `Settings.ActionMenu.VisibleActions`.
- Wire the action through `EventCoordinator.HandleActionRequested` to trigger `InteractionManager` entry into `ScrollMode` (rather than calling `IInputSimulator` like the discrete scroll actions do).

## Files Changed / Created

### New files
- `src/EyeCtrl.Core/Processing/ScrollEngine.cs` — pure offset→fractional-velocity math, dead zone, clamp.
- `src/EyeCtrl.Core/Settings/ScrollSettings.cs` — sensitivity setting (`ObservableObject + IValidatable`).
- `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml` (+ `.xaml.cs`) — anchor/feedback indicator + stop button with stop zone.
- `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs` — `IRecipient<GazeCursorMovedMessage>`; receives gaze, runs the stop-button `DwellDetector`, drives Option-D feedback. (Established overlay pattern — every overlay VM subscribes to `GazeCursorMovedMessage`.)
- The 60Hz scroll worker (App or Platform layer) — owns the timer, lerp, fractional accumulation, and `IInputSimulator.Scroll` calls.

### Modified files
- `src/EyeCtrl.Core/Models/InteractionState.cs` — add `ScrollMode`.
- `src/EyeCtrl.Core/Models/InteractionAction.cs` — add `ScrollMode`.
- `src/EyeCtrl.Core/Processing/InteractionManager.cs` — `ScrollMode` enter/exit transitions; ignore fixation-driven transitions while in `ScrollMode`.
- `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` — add `ScrollMode` to available/default actions.
- `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` — add `ScrollSettings` field + `Validate()` branch.
- `src/EyeCtrl.ViewModels/SettingsViewModel.cs` — `ResetAllSettings()` branch + sensitivity slider item.
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — route the `ScrollMode` action into `InteractionManager`; pin cursor at anchor on entry; show/hide scroll overlay; start/stop the scroll worker; wire stop-button completion → exit.
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — register `ScrollOverlayWindow` / `ScrollOverlayViewModel`.
- `src/EyeCtrl.App/Services/OverlayManager.cs` (+ the `OverlayViewModels` record) — manage `ScrollOverlayWindow` lifecycle and hold its VM.

### Unchanged
- `GazeEngine` — already fires `CursorMoved` (filtered) in every state, and already gates `MoveCursor` on `FreeLook`; entering `ScrollMode` suppresses cursor movement with no change here.
- `FixationDetector`, `DwellDetector` — no mode awareness needed.
- `IInputSimulator` / `Win32InputSimulator` — `Scroll()` is already 2D-capable (`MOUSEEVENTF_WHEEL` + `MOUSEEVENTF_HWHEEL`); no changes.

## Architecture Flow

```
Action Menu → "Scroll Mode" selected
  ↓
EventCoordinator.HandleActionRequested(ScrollMode)
  ├─ anchor = GazeEngine.StableCentroid ?? dwelledPoint
  ├─ InteractionManager → InteractionState.ScrollMode   (GazeEngine stops MoveCursor)
  ├─ InputSimulator.MoveCursor(anchor)                  (pin cursor once)
  ├─ Lock cursor circle overlay at anchor
  ├─ Show scroll overlay (stop button + stop zone)
  └─ Start 60Hz scroll worker
  ↓
GazeEngine fires CursorMoved (filtered point, every state)
  ↓
ScrollOverlayViewModel / scroll routing receives gaze
  ├─ ScrollEngine.ComputeVelocity(gazePos, anchor, sensitivity) → fractional (vx, vy)
  │     (zero if inside dead zone OR inside stop zone)
  └─ worker: lerp → accumulate fractional notches → InputSimulator.Scroll(anchor, intDx, intDy)
  ↓
Stop button dwelled (gaze in stop zone ⇒ velocity already 0)
  └─ EventCoordinator exit
      ├─ InteractionManager → InteractionState.FreeLook (GazeEngine resumes MoveCursor)
      ├─ Unlock cursor circle
      ├─ Hide scroll overlay
      └─ Stop 60Hz scroll worker
```
