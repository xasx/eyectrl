# Pinned Notes — Design Spec

**Date:** 2026-06-12
**Status:** Draft

## Overview

Add a "Pin Note" action to the action menu that places a persistent, editable text annotation at a screen location. Notes function like sticky notes — they remain visible after gaze moves away, can be edited, moved, and dismissed. Primary use case: showing and annotating elements on screen to others during screen sharing (more direct than describing in chat).

## Requirements

| Requirement | Decision |
|-------------|----------|
| Content | Text note (sticky note) |
| Text input | Physical keyboard + on-screen eye-tracking keyboard (whichever the user can use) |
| Max active notes | 8 |
| Note management | Per-note dwell controls (close, edit, move) |
| Mouse interaction | Notes capture mouse clicks (not click-through) |
| Persistence | Session-only (cleared on app close) |
| Architecture | Individual `OverlayWindow` per note (small, screen-capture-friendly) |
| Move interaction | Gaze-follow: dwell on move button → note follows gaze in real-time → dwell on a placement target to place |
| Controls visibility | Always visible (large buttons on right side of note) |
| Thread safety | All `NoteManager` operations marshaled to UI dispatcher (same thread as `DwellDetector`) |
| Max notes UX | `PinNote` stays visible in action menu; selecting it at capacity shows a brief toast ("Max 8 notes") on the note instead of creating a new one |

## Architecture

### New Components

```
┌─────────────────────────────────────────────────────────┐
│  Core Layer                                             │
│  ┌──────────────────────────────────────────────────┐   │
│  │ NoteData                                          │   │
│  │ - Text: string                                    │   │
│  │ - Position: PhysicalPoint                         │   │
│  │ - State: NoteState { Display, Edit, Move }        │   │
│  │ - Id: string (Guid)                               │   │
│  └──────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────┐   │
│  │ InteractionAction.PinNote (new enum value)        │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────┐
│  App Layer                                              │
│  ┌──────────────────────────────────────────────────┐   │
│  │ INoteManager / NoteManager                        │   │
│  │ - CreateNoteAt(PhysicalPoint) → NoteData           │   │
│  │ - RemoveNote(string id)                            │   │
│  │ - EditNote(string id)                              │   │
│  │ - StartMoveNote(string id)                         │   │
│  │ - FinishMoveNote(string id, PhysicalPoint)         │   │
│  │ - ActiveNotes: IReadOnlyList<NoteData>             │   │
│  │ - IsAtCapacity: bool                               │   │
│  │ - Owns DwellDetector for note controls             │   │
│  │ - Subscribes to GazeCursorMovedMessage for dwell   │   │
│  └──────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────┐   │
│  │ NoteWindow : OverlayWindow                        │   │
│  │ - Per-note overlay window                         │   │
│  │ - Canvas-based rendering                          │   │
│  │ - TextBlock for note content                      │   │
│  │ - 3 circular dwell buttons (close/edit/move)      │   │
│  │ - Dwell progress arcs on buttons                  │   │
│  │ - State-aware rendering (edit border, move tint)  │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

### Files

**New files:**
| File | Purpose |
|------|---------|
| `src/EyeCtrl.Core/Models/NoteData.cs` | Data model + NoteState enum |
| `src/EyeCtrl.App/Services/INoteManager.cs` | Interface |
| `src/EyeCtrl.App/Services/NoteManager.cs` | Lifecycle management, dwell detection |
| `src/EyeCtrl.App/Overlays/NoteWindow.xaml` | XAML template |
| `src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs` | Code-behind rendering |

**Modified files:**
| File | Change |
|------|--------|
| `src/EyeCtrl.Core/Models/InteractionAction.cs` | Add `PinNote = 9` |
| `src/EyeCtrl.Core/Processing/InteractionManager.cs` | **No changes required.** `OnActionSelected` already handles all `InteractionAction` values generically — resolves target point from current state, fires `ActionRequested`, resets to `FreeLook`. The PinNote special case (no `InputSimulator` call) is entirely in `EventCoordinator`. |
| `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` | Add PinNote to default `VisibleActions` |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Add `case PinNote:` before the existing InputSimulator switch in `HandleActionRequested` (around line 772) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `INoteManager` as singleton in DI |
| `src/EyeCtrl.App/App.xaml.cs` | Add `INoteManager` to the `EventCoordinationContext` constructor call |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Add `INoteManager NoteManager` to `EventCoordinationContext` record |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` (the other copy of the duplicated switch — see note below) |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | No changes required — existing `ActionRequestedMessage` covers PinNote |
| `src/EyeCtrl.Core/LayoutConstants.cs` | Add `NoteButtonRadius = 30.0` constant (60px diameter — gaze-targetable but compact) |

**Code duplication note:** `GetActionSymbol()` exists in two places with near-identical switch statements:
- `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` (lines ~120+)
- `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` (line ~296)

The pin symbol must be added to both. `ActionMenuItemViewModel` only receives a `string Symbol` in its constructor — it does not do symbol mapping. A future refactor could extract the switch into a shared helper, but that's out of scope for this feature.

### Focus Mode for Editing

`OverlayWindow`'s `SetOverlayInteraction(clickThrough, focusable)` (at `OverlayWindow.cs:37`) supports runtime toggling of `WS_EX_NOACTIVATE`. During Edit state, `NoteWindow` calls `SetOverlayInteraction(clickThrough: false, focusable: true)` so the `TextBox` can receive focus and keyboard input (physical keyboard + eye keyboard `SendInput` keystrokes). On exiting Edit state, restore `SetOverlayInteraction(clickThrough: false, focusable: false)`.

### Eye Keyboard Integration

The eye keyboard types via `InputSimulator.SendInput` (which synthesizes keystrokes at the OS level). When the note's `TextBox` has focus (enabled by `focusable: true` during Edit), these synthesized keystrokes arrive at the `TextBox` naturally — no special message routing needed. Prediction selection (`PredictionSelectedMessage`) also calls `InputSimulator.TypeText()`, so predictions work the same way. The note's `TextBox` handles `KeyDown` events for Enter (confirm) and Escape (cancel).

## Data Model

**Coordinate space:** All positions in this spec are **physical screen pixels** (same as `GazePoint`, `PhysicalPoint` throughout the codebase). `NoteWindow` converts to WPF DIPs via `DpiHelper.PhysicalToLogicalX/Y()` at the window boundary — same pattern as `ActionMenuWindow`, `CursorCircleWindow`, etc.

```csharp
// src/EyeCtrl.Core/Models/NoteData.cs
namespace EyeCtrl.Core.Models;

public enum NoteState
{
    Display,
    Edit,
    Move
}

public class NoteData
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = string.Empty;
    public PhysicalPoint Position { get; set; }
    public NoteState State { get; set; } = NoteState.Edit; // starts in edit mode
}
```

## Note Lifecycle

```
FreeLook → Fixation → ActivationButton → ActionMenu → PinNote selected
  → NoteManager.CreateNoteAt(fixationPoint)
    → Creates NoteWindow at position
    → Note starts in Edit state (green border, text input active)
    → User types text (physical keyboard or eye keyboard)
    → Enter key → State → Display
    → Note shows text with controls visible

Display state:
  ┌──────────────────────────────────┐
  │ PINNED NOTE              ◎  ◎  ◎│  ← 30px radius note buttons
  │                                  │
  │  Click the gear icon             │  ← text content (yellow-ish on dark)
  │  in the top-right corner         │
  │                                  │
  └──────────────────────────────────┘

Edit state (dwell on edit button):
  - Green border appears; `SetOverlayInteraction(clickThrough: false, focusable: true)` enables focus
  - TextBox auto-focuses (no extra click/dwell needed) — receives both physical keyboard and eye-keyboard `SendInput`
  - User types via physical keyboard or on-screen eye-tracking keyboard
  - Enter to save, Escape to cancel; `TextBox.KeyDown` handler intercepts these
  - Close button remains active (to abort); Move button is disabled during Edit
  - On exit: `SetOverlayInteraction(clickThrough: false, focusable: false)` restores no-activate

Move state (dwell on move button):
  - Note becomes semi-transparent, blue border
  - Button dwell detection is temporarily disabled (prevents accidental close/edit)
  - Note position follows gaze cursor in real-time via `UpdateTargetCenter`
  - A single large "placement" target covers the note area; its center follows gaze each frame
  - Dwell on the placement target for 600ms → note placed at gaze position
  - Returns to Display state after placement

Close (dwell on close button):
  - Note window closes, all dwell targets removed, message subscriptions unsubscribed
  - Removed from NoteManager.ActiveNotes collection
```

## NoteWindow Visual Design

**Layout:**
- Width: ~280px (200px text area + 80px button column: 60px buttons + 10px gap + 10px right padding)
- Height: auto, min ~120px
- Rounded corners (6px)
- Background: `#1A1A2E` (dark navy, matching EyeCtrl palette)
- Shadow: WPF `DropShadowEffect`
- Border: 1px `#334` (display), 2px `#00FF00` (edit), 2px `#4488FF` (move)

**Text area:**
- Left aligned, padding 10px
- Font: Segoe UI 12pt
- Color: `#FFEAA7` (warm yellow)
- Word wrap enabled
- Max width 200px

**Button column (right side):**
- 3 circular buttons stacked vertically, **30px radius (60px diameter)** each — defined as `LayoutConstants.NoteButtonRadius`
- 10px gap between buttons
- 10px padding from top/right edges
- Background: dark semi-transparent circle (`#33FFFFFF` with `#1A1A2E` fill)
- Symbols: close `"\u2715"` (✕), edit `"\u270E"` (✎), move `"\u2195"` (↕)
  - Colors: close = `#FF6666` (soft red), edit = `#66FF66` (soft green), move = `#6688FF` (soft blue)
  - All three symbols render correctly in Segoe UI at 14pt
  - Font size scales with button radius (14pt for 30px radius, ~80% of diameter)
- Dwell progress: lime-green arc (same pattern as `ActionMenuWindow`)

**Window behavior:**
- Topmost, `WS_EX_TOOLWINDOW` (no taskbar)
- NOT click-through (captures mouse for text editing): `WS_EX_TRANSPARENT` removed
- Default: `WS_EX_NOACTIVATE` set (does not steal focus)
- **Edit state:** `WS_EX_NOACTIVATE` removed via `SetOverlayInteraction(clickThrough: false, focusable: true)` — enables `TextBox` focus for keyboard input
- Window position follows note position in physical coordinates, converted to DIPs
- `ShowInTaskbar = false`

## NoteManager Design

**Thread safety:** All `NoteManager` methods operate on the UI dispatcher thread (same thread `DwellDetector` requires). `GazeEngineAdapter` already marshals gaze events to UI thread via `Dispatcher.BeginInvoke()`. `NoteManager` subscribes to `GazeCursorMovedMessage` via `WeakReferenceMessenger` — multiple subscribers are supported and ordering does not matter since `EventCoordinator` and `NoteManager` operate on disjoint target sets.

```csharp
public class NoteManager : INoteManager
{
    private readonly List<NoteWindow> _noteWindows = new();
    private readonly DwellDetector _dwellDetector = new(cooldownMs: 300);
    private const int MaxNotes = 8;

    public IReadOnlyList<NoteData> ActiveNotes => ...; // exposes NoteData, not NoteWindow
    public bool IsAtCapacity => _noteWindows.Count >= MaxNotes;

    public void CreateNoteAt(PhysicalPoint point);
    public void RemoveNote(string id);
    public void StartEdit(string id);
    public void FinishEdit(string id, string text);
    public void StartMove(string id);
    public void FinishMove(string id, PhysicalPoint newPosition);

    // Called each frame by GazeCursorMovedMessage handler
    public void OnGazeMoved(PhysicalPoint gazePoint, long timestamp);
}
```

**API design:** `INoteManager` exposes `NoteData` (Core model) and string IDs, never WPF `NoteWindow` types. This keeps the interface testable without WPF dependencies. `NoteManager` owns `NoteWindow` instances internally.

**Dwell target ID scheme:** `"{noteId}:close"`, `"{noteId}:edit"`, `"{noteId}:move"`, `"{noteId}:place"`. The `place` target is only active during Move state. `NoteWindow` uses the ID suffix to identify which button to animate.

**Dwell detection:**
- Single `DwellDetector` instance shared across all notes
- Each note registers its 3 buttons as `DwellTarget`s when in Display state
- Dwell time: 600ms (matching `ActionMenuDwellMs` from `DwellSettings`)
- On `TargetActivated`: dispatches to `RemoveNote`, `StartEdit`, or `StartMove` based on the target ID suffix

**State-specific target management:**
- **Display state:** close, edit, move targets are active
- **Edit state:** close target active; edit target acts as "confirm" (same as Enter); move target removed
- **Move state:** only the "place" target is active; close/edit/move targets are removed to prevent accidental activation
- **On every state transition:** call `_dwellDetector.ResetAllState()` to clear stale progress and prevent spurious activation after the switch

**RemoveNote cleanup (full lifecycle):**
1. Remove all dwell targets for the note from `_dwellDetector`
2. Unsubscribe from any note-specific message handlers
3. Call `NoteWindow.Close()` to destroy the WPF window
4. Remove from internal `_noteWindows` list

**Move mode placement mechanism:**
1. `StartMove(id)` → add a large `DwellTarget` with ID `"{noteId}:place"`, radius ~100px, centered at note position
2. Each gaze frame: `UpdateTargetCenter("{noteId}:place", gazePoint)` keeps the placement target at the current gaze position
3. Note position also updated each frame to follow gaze (visual feedback)
4. When `TargetActivated` fires for the place target → `FinishMove(id, gazePoint)` removes the place target, sets note position, returns to Display state

**Max notes enforcement:**
- Keep `PinNote` visible in the action menu at all times
- When `IsAtCapacity` is true and `PinNote` is selected, show a brief toast message ("Max 8 notes") instead of creating a new note
- **Toast implementation:** a temporary `TextBlock` overlaid on the most recently created note's window. Text fades in, holds for 2 seconds, then fades out via a WPF `DispatcherTimer`. No new overlay window is needed — reuses the existing note's `Canvas`.

## Action Menu Integration

**Enum:** `InteractionAction.PinNote = 9` (next available after `ScrollRight = 8` in the existing enum).

**Symbol:** Unicode pin `"\uD83D\uDCCC"` (📌). If standard WPF `TextBlock` renders this poorly at the action menu button size (50px radius / 100px diameter), fall back to a geometric glyph `"\u25C9"` (◉) or a simple `"P"` character. The existing emoji infrastructure (`Emoji.Wpf.TextBlock`) may be used if needed, matching how the emoji keyboard renders emoji.

**Default visibility:** Enabled in `ActionMenuSettings.VisibleActions` alongside LeftClick, RightClick, DoubleClick, DragStart, ScrollUp, ScrollDown.

**Execution path:**
```
ActionMenuViewModel.OnTargetActivated("PinNote")
  → ActionSelectedMessage(InteractionAction.PinNote)
    → InteractionManager.OnActionSelected(PinNote)
      → Determines target point from current state (fixation point)
      → Fires ActionRequested(PinNote, targetPoint)
        → EventCoordinator.HandleActionRequested()
          → case PinNote: _context.NoteManager.CreateNoteAt(point); return;
          → default: // existing InputSimulator switch for mouse actions
```

**Important:** The `PinNote` case must be added **before** the existing `switch` block in `HandleActionRequested` (around `EventCoordinator.cs:772`) since `PinNote` does not call `InputSimulator`. It returns early after delegating to `NoteManager`. `InteractionManager.OnActionSelected` requires **no changes** — it generically resolves the target point and fires `ActionRequested` for all `InteractionAction` values regardless of which action is selected.

## Event Flow Summary

```
Gaze → GazeEngine → CursorMoved
  → GazeEngineAdapter (Dispatcher marshaling to UI thread)
    → GazeCursorMovedMessage (broadcast via WeakReferenceMessenger; sent by GazeEngineAdapter)
      ├→ EventCoordinator (handles action menu dwell)
      └→ NoteManager.OnGazeMoved(position, timestamp)
           → DwellDetector.Update(position, timestamp)
              → Hit-tests all note control targets, accumulates dwell only on the nearest one
              → TargetProgress → NoteWindow.UpdateDwellProgress(targetId, progress)
               → Maps suffix: "close" → red arc, "edit" → green arc, "move" → blue arc
             → TargetActivated → dispatch based on suffix:
                 "close" → RemoveNote(id)
                 "edit"  → StartEdit(id)
                 "move"  → StartMove(id)
                 "place" → FinishMove(id, gazePoint)

Move mode:
  NoteManager.StartMove(id)
    → Removes close/edit/move targets for this note
    → Adds "place" target (100px radius) centered at current note position
    → NoteWindow.State = Move (semi-transparent, blue border)
    → Each frame: UpdateTargetCenter("{id}:place", gazePoint) + update note position
    → Dwell completes on "place" → NoteManager.FinishMove(id, newPoint)
      → Removes "place" target, adds close/edit/move targets at new position
      → NoteWindow.State = Display

Edit mode:
  NoteManager.StartEdit(id)
    → Removes move target (prevent accidental move during edit)
    → Close target remains active; edit target acts as "confirm"
    → NoteWindow.State = Edit (green border, TextBox auto-focused)
    → User types... Enter key or dwell on edit button
    → NoteWindow sends text to NoteManager.FinishEdit(id, text)
    → Returns to Display state with updated text
```

## Testing

**Unit tests** (`EyeCtrl.Core.Tests`):
- `NoteData` construction and state transitions
- `InteractionAction.PinNote` enum value
- `PinNote` in default `ActionMenuSettings.VisibleActions`

**ViewModel tests** (`EyeCtrl.ViewModels.Tests`):
- `PinNote` action symbol mapping in `GetActionSymbol()`

**App tests** (`EyeCtrl.App.Tests`):
- `NoteManager` create/remove/edit/move lifecycle (using `NoteData` API)
- `NoteManager` enforces max 8 notes (toast shown, no new note created)
- `NoteManager.RemoveNote` full cleanup (targets removed, window closed)
- Dwell detection on note control buttons
- Move mode gaze-follow updates position
- State transitions call `ResetAllState()`
- `EventCoordinator` routes `PinNote` to `NoteManager.CreateNoteAt()` (not `InputSimulator`)

## Out of Scope

- Note persistence between sessions
- Rich text formatting (bold, italic, colors)
- Font size customization
- Note-to-note linking or numbering
- Drag-to-resize notes
- Note rotation
- Image attachments
- Export/import notes

## Impact Summary (GitNexus)

Blast radius for the primary symbols touched:

| Symbol | Risk | Affected |
|--------|------|----------|
| `EventCoordinator.HandleActionRequested` | **HIGH** | 4 direct dependents, affects Initialize/OnStartup/SetupMessageHandlers processes |
| `ActionMenuViewModel` (GetActionSymbol) | **HIGH** | 16 direct dependents, mostly its 10+ viewmodel tests |
| `InteractionAction` (enum) | LOW | Graph edges weak for enums; real surface is 2x symbol switches + settings + exec switch + downstream tests |

All changes are surgical additions (new enum value, new case, new default in list, new symbol mapping) — no existing behavior is modified.
