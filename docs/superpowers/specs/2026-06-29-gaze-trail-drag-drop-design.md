# Gaze Trail Drag & Drop

**Date:** 2026-06-29
**Status:** Draft
**Author:** EyeCtrl Team

## Problem

The current drag-and-drop interaction requires two full fixation+action-menu cycles:

1. Fixate → action menu → dwell on ✊ DragStart → mouse held down
2. Move gaze to destination → fixate again → action menu → dwell on ✋ DragEnd → mouse released

This is slow (two complete interaction cycles), loses spatial context (no visual connection between grab and drop), and makes precise drops difficult due to gaze jitter at the destination.

## Goals

- Reduce drag operations from two menu cycles to one
- Provide continuous visual feedback during drag (spatial context)
- Enable precise drops via on-demand zoom magnification
- Maintain safety against accidental drags (action menu initiation)
- Support cancel and back-out without losing the drag state

## Non-Goals

- Replacing the action menu for other actions (click, scroll, etc.)
- Auto-detecting draggable elements without user initiation
- Supporting multi-item drag or drag-and-drop between applications beyond OS-level mouse simulation

## Design

### Interaction Flow

The drag operation has five states:

#### 1. Initiation (existing flow)

User fixates on a point → activation button appears → dwells on activation button → action menu appears → dwells on ✊ **Drag** action.

The `DragStart` action is replaced by a new `Drag` action that enters trail mode instead of immediately sending `MouseDown`. `DragEnd` is removed from the action menu.

**On selection:** `MouseDown` is sent at the **resolved target point** for the current selection mode — `AutoSelectionTarget.CenterPoint` (AutoSelection), `ManualSelectionDwellPoint` (ManualSelection), or `FixationPoint` (None) — the same point `InteractionManager.OnActionSelected` already computes for other actions. This becomes the **grab origin**. The system then enters `InteractionState.Dragging` instead of resetting to `FreeLook`.

#### 2. Dragging (free gaze movement)

- An **elastic trail line** is drawn from the grab origin to the current cursor position. Orange (#ff9800), fades with distance.
- The cursor displays a ✊ grab hand icon.
- A **✋ release button** floats near the cursor (offset up-left). Dwelling on it triggers an instant drop at the current position.
- A **✕ cancel button** is fixed on the **right screen edge**. Dwelling on it cancels the drag (MouseUp at origin, item returns).
- Gaze moves freely — cursor follows gaze, trail stretches. No zoom overlay.
- The OS mouse button remains held down throughout.

#### 3. Settling (gaze stops)

When gaze stays within a configurable settle radius (default: 40px physical) for a configurable settle dwell time (default: 500ms):

- A **progress ring** begins filling around the cursor position (green, #4caf50).
- In **ManualSelection mode**, the **zoom magnifier** appears at the settle location (reuses existing `ManualSelectionOverlay` infrastructure). The user can fine-tune cursor position within the magnified view.
- In **None mode**, no zoom appears — the settle ring fills directly at the raw gaze position.
- If the progress ring reaches 100%, the drop is confirmed (go to state 5).
- If gaze leaves the settle radius before 100%, the timer resets and the system returns to state 2.

#### 4. Zoom Active (precision refinement, ManualSelection mode only)

While zoom is active:

- **↩ back button** appears on the right screen edge, above ✕ cancel. Dwelling on it dismisses the zoom and returns to state 2 (free drag). The settle timer resets.
- **✕ cancel** remains on the right edge below ↩ back. Aborts the entire drag operation.
- **✋ release** remains near the zoom circle for instant drop without waiting for settle.

#### 5. Dropped

- `MouseUp` is sent at the current cursor position.
- Brief green flash confirms success.
- Trail disappears. Zoom dismisses.
- Cursor returns to normal crosshair.
- System returns to `InteractionState.FreeLook`.

### Cancel Behavior

- Dwelling on ✕ cancel sends `MouseUp` at the **grab origin** (item returns to original position).
- Brief red flash confirms cancellation.
- Trail disappears. System returns to `FreeLook`.

### Right Edge Control Panel

The right screen edge hosts fixed-position dwell target buttons that adapt to the current drag state:

| Drag State | Buttons on Right Edge |
|---|---|
| Free drag | ✕ cancel |
| Zoom active | ↩ back (dismiss zoom) + ✕ cancel (abort drag) |
| After ↩ back | ✕ cancel |

Buttons are vertically stacked, each 26×40px, positioned at a fixed X coordinate near the right screen edge. They are stable dwell targets — the user doesn't need to chase them.

### Selection Mode Integration

- **None mode:** Settle-to-drop uses raw gaze coordinates. No zoom magnifier appears (settle ring fills directly).
- **ManualSelection mode:** Zoom magnifier appears on settle, providing precision placement within the magnified view.
- **AutoSelection mode:** On drop, the system snaps to the nearest UIA-resolved drop target before sending `MouseUp`.

## Architecture

### New State

Add `Dragging` to `InteractionState` enum. The `InteractionManager` transitions:

```
ActionMenuVisible             ──[Drag action selected]──> Dragging
ManualSelectionActionMenuVisible ──[Drag action selected]──> Dragging
Dragging ──[settle confirmed / release button]──> FreeLook (MouseUp at cursor)
Dragging ──[cancel button]──> FreeLook (MouseUp at origin)
Dragging ──[zoom dismissed via ↩ back]──> Dragging (still in drag, no zoom)
```

The `Drag` action can be selected from **either** action-menu state — `ActionMenuVisible` (None / AutoSelection modes) or `ManualSelectionActionMenuVisible` (ManualSelection mode). In both cases `OnActionSelected` resolves the grab origin (see Initiation) and transitions to `Dragging`.

**`InteractionManager.OnActionSelected` must special-case `Drag`.** For every other action it fires `ActionRequested` and then calls `ResetToFreeLook()`. For `Drag` it must instead enter `Dragging` (and, in ManualSelection mode, end the manual-selection overlay via `EndManualSelection` exactly as it does for normal actions) — it must **not** reset to `FreeLook`, or the drag state is lost the instant it begins.

**The cursor-movement gate must be widened.** `GazeEngine.OnGazePoint` currently moves the OS cursor only when `CurrentState == InteractionState.FreeLook`. For the cursor to follow gaze during the drag, this gate must accept `FreeLook` **or** `Dragging`. The `InteractionManager` still suppresses fixation detection while `Dragging` (no new activation buttons or action menus appear).

### New Components

| Component | Project | Responsibility |
|---|---|---|
| `DragTrailOverlay` | EyeCtrl.App | Transparent WPF window rendering elastic line, floating buttons, progress ring, zoom magnifier |
| `DragTrailViewModel` | EyeCtrl.ViewModels | State management, dwell detection for release/back/cancel buttons, settle detection |
| `DragSettings` | EyeCtrl.Core | Configurable parameters (settle time, settle radius, trail color/opacity) |

### Reused Components

| Existing Component | How Reused |
|---|---|
| `ManualSelectionOverlayWindow` (via `OverlayManager.ManualSelectionOverlay`) | Zoom magnifier rendering during settle/zoom-active states |
| `DwellDetector` | Dedicated instance for right-edge buttons and floating release button |
| `FixationDetector` logic | Settle detection (same algorithm, different radius/timing parameters) |
| `IInputSimulator` | `MouseDown`/`MouseUp`/`MoveCursor` calls |
| `ActionMenuWindow` | Modified to show `Drag` instead of `DragStart`/`DragEnd` |

### Settings

New `DragSettings` class (in `EyeCtrl.Core.Settings`). It holds the trail/settle parameters that are specific to dragging:

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `SettleDwellMs` | int | 500 | 200–2000 | Time gaze must stay within settle radius to confirm drop |
| `SettleRadiusPixels` | int | 40 | 10–200 | Max drift allowed during settle (physical pixels) |
| `TrailColor` | string | "#ff9800" | hex color | Elastic trail line color |
| `TrailOpacity` | double | 0.7 | 0.1–1.0 | Trail line opacity |

The **button dwell times** belong in the existing `DwellSettings` class, not `DragSettings` — all other dwell timings in the app (`ActionMenuDwellMs`, `ManualSelectionActionMenuDwellMs`, `ToolbarButtonDwellMs`, …) are centralized there, and scattering these two into `DragSettings` would break that convention:

| Property (add to `DwellSettings`) | Type | Default | Range | Description |
|---|---|---|---|---|
| `DragReleaseButtonDwellMs` | int | 600 | 200–2000 | Dwell time on floating ✋ release button |
| `DragRightEdgeButtonDwellMs` | int | 600 | 200–2000 | Dwell time on right-edge ↩ back and ✕ cancel buttons |

Each `*Settings` class is `ObservableObject + IValidatable` with `partial void OnXChanging` range guards and a `Validate()` method (existing convention).

**`DragSettings` must be wired into the settings aggregate in three places** (the codebase enumerates every sub-setting explicitly in each):
1. Add `[ObservableProperty] private DragSettings _drag = new();` to `EyeCtrlSettings`.
2. Add a null-check + `errors.AddRange(Drag.Validate())` branch to `EyeCtrlSettings.Validate()`.
3. Add `Drag` to `SettingsViewModel.ResetAllSettings()` so a settings reset restores its defaults.

### Action Menu Changes

- `InteractionAction.DragStart` is **renamed** to `InteractionAction.Drag`. Note the enum is serialized **by camelCase name** (`JsonStringEnumConverter`), so the rename changes the on-disk string `"dragStart"` → `"drag"` — see Migration for why this matters.
- `InteractionAction.DragEnd` is **removed** from the enum. It is **not** in the default action list today, so no default-list edit is needed for its removal — but every other reference to it must be deleted or compilation breaks:
  - `ActionMenuViewModel` glyph map: remove the `DragEnd => "✋"` entry. (✋ is reused as the floating release-button glyph drawn by `DragTrailOverlay`, not via the action menu.)
  - `EventCoordinator.HandleActionRequested`: remove the `case InteractionAction.DragEnd: MouseUp(...)` arm.
- `ActionMenuSettings` stores the visible-action list in the property **`VisibleActions`** (there is no `DefaultVisibleActions`). The default `DragStart` entry must be renamed to `Drag` in **both** hardcoded locations: the `_visibleActions` field initializer **and** the `EnsureDefaults()` method.
- The `Drag` action's icon remains ✊ (raised fist) — rename the `DragStart => "✊"` glyph-map entry to `Drag => "✊"`.

### Event Coordinator Changes

Responsibility is split between `InteractionManager` (state) and `EventCoordinator` (I/O and overlay), matching the existing action pipeline:

- **`InteractionManager.OnActionSelected`** resolves the grab origin (mode-specific target point, as above), ends the manual-selection overlay if in ManualSelection mode, and sets `InteractionState.Dragging` instead of calling `ResetToFreeLook()`. It signals the drag start (e.g. a dedicated `DragStarted(originPoint)` event/message) carrying the resolved origin so the consumer doesn't re-derive the cursor position.
- **`EventCoordinator`** reacts to that signal:
  1. Sends `MouseDown` at the resolved **grab origin** (not the raw cursor — they differ in ManualSelection/AutoSelection modes).
  2. Shows `DragTrailOverlay`.
  3. Registers gaze updates to the overlay for trail rendering and settle detection.

The existing generic `case InteractionAction.DragStart: MouseDown(pt)` arm in `HandleActionRequested` is **removed** — `Drag` no longer flows through the fire-and-forget `ActionRequested` path, because that path resets to `FreeLook`. On drop/cancel, `EventCoordinator` sends the matching `MouseUp` (at cursor for a drop, at origin for a cancel) and the `InteractionManager` returns to `FreeLook`.

### Overlay Lifecycle

`DragTrailOverlay` is created on drag initiation and destroyed on drop/cancel. It is a transparent, topmost, click-through WPF window (same pattern as `ActionMenuWindow`, `ManualSelectionOverlayWindow`). Click-through (`WS_EX_TRANSPARENT`) is required here, not just conventional: a real OS drag is in progress with the mouse button held, so the overlay must not intercept it.

## Error Handling

| Scenario | Behavior |
|---|---|
| Gaze tracking lost during drag | Hold cursor position, show "tracking lost" indicator on trail overlay. Resume when tracking returns. If lost > 3s, auto-cancel (MouseUp at origin). |
| User presses physical Escape key | Cancel drag (MouseUp at origin). |
| Display resolution change during drag | Cancel drag. |
| Settle interrupted by gaze leaving radius | Reset settle timer. Zoom dismisses if it was visible. Return to free drag state. |

## Testing

### Unit Tests (EyeCtrl.Core.Tests)

- `DragSettings` validation (range clamping, defaults)
- Settle detection logic (radius threshold, timing)
- **Settings migration:** a `settings.json` fixture containing `"dragStart"` / `"dragEnd"` in `actionMenu.visibleActions` loads successfully (settings are *not* reset to defaults) and ends up with `Drag` and no `DragEnd`. Also assert an otherwise-customized fixture (e.g. a non-default `selectionMode`) survives the load.
- `EyeCtrlSettings.Validate()` includes `DragSettings` errors; `SettingsViewModel.ResetAllSettings()` restores `Drag` defaults (guards the "extend both enumerations" convention).
- `OnActionSelected(Drag)` transitions to `Dragging` (not `FreeLook`) from **both** `ActionMenuVisible` and `ManualSelectionActionMenuVisible`, and resolves the grab origin per selection mode.

### ViewModel Tests (EyeCtrl.ViewModels.Tests)

- `DragTrailViewModel` state transitions (free drag → settling → zoom active → dropped)
- Right edge button visibility based on drag state
- Cancel sends MouseUp at origin
- Release button sends MouseUp at current position

### Integration

- Manual testing with eye tracker hardware for settle timing feel
- Verify trail rendering performance (60fps target)
- Verify zoom magnifier reuses existing infrastructure correctly

## Migration

**The rename is a breaking change to persisted settings and must be migrated *before* deserialization.** `InteractionAction` is serialized by camelCase name. After the rename, a user's existing `settings.json` contains `"dragStart"` (and possibly `"dragEnd"`) inside `actionMenu.visibleActions`. `JsonStringEnumConverter` **throws** on an unknown enum string, and `SettingsService.Load()` catches the exception and returns a **fresh default `EyeCtrlSettings`** — silently discarding *all* of the user's settings, not just the action list. (This is also why "same enum value" is irrelevant: the integer value is never written; only the string name is.)

Required migration, in `SettingsService.Load()` **before** `JsonSerializer.Deserialize`:

- Read the raw JSON text and rewrite the action-name strings: `"dragStart"` → `"drag"`, and drop any `"dragEnd"` array element. A targeted string/JSON-node pass on `actionMenu.visibleActions` is sufficient; do not rely on post-deserialization fix-up, which never runs because the deserialize call fails first.
- After migrating, run `ActionMenuSettings.EnsureDefaults()` so any list left without `Drag` regains it.
- Mirror the existing `SelectionMode` migration already in `Load()` (a precedent for in-loader migration).

Defaults:

- Rename the `DragStart` entry to `Drag` in `ActionMenuSettings`'s `_visibleActions` initializer **and** in `EnsureDefaults()`. (`DragEnd` is absent from both today, so nothing to remove there.)

- No breaking changes to external dwell service or keyboard interfaces.
