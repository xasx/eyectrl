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
│  │ - CreateNoteAt(PhysicalPoint) → NoteWindow        │   │
│  │ - RemoveNote(string id)                           │   │
│  │ - EditNote(string id)                             │   │
│  │ - StartMoveNote(string id)                        │   │
│  │ - FinishMoveNote(string id, PhysicalPoint)        │   │
│  │ - ActiveNotes: IReadOnlyList<NoteWindow>          │   │
│  │ - MaxNotes: int (8)                               │   │
│  │ - Owns DwellDetector for note controls            │   │
│  │ - Subscribes to GazeCursorMovedMessage for dwell  │   │
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
| `src/EyeCtrl.Core/Processing/InteractionManager.cs` | Handle PinNote in `OnActionSelected` (no InputSimulator call — fires ActionRequested for NoteManager) |
| `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` | Add PinNote to default `VisibleActions` |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Wire `ActionRequestedMessage(PinNote)` → `NoteManager.CreateNoteAt()` |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `INoteManager` as singleton in DI; pass to `EventCoordinationContext` |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Add `INoteManager NoteManager` to `EventCoordinationContext` record |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` (duplicated in both ViewModels and App — see #8 below) |
| `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add note lifecycle messages if needed |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` | Add `NoteWindow` collection property if centralized creation needed |

**Code duplication note:** `GetActionSymbol()` exists in both `ActionMenuItemViewModel` and `ActionMenuWindow.xaml.cs` with identical switch statements. This pre-existing duplication means the pin symbol must be added in both places. A future refactor could extract this into a shared helper, but that's out of scope for this feature.

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
  │ PINNED NOTE              ◎  ◎  ◎│  ← close/edit/move buttons (50px each)
  │                                  │
  │  Click the gear icon             │  ← text content (yellow-ish on dark)
  │  in the top-right corner         │
  │                                  │
  └──────────────────────────────────┘

Edit state (dwell on edit button):
  - Green border appears
  - TextBox auto-focuses (no extra click/dwell needed)
  - User types via physical keyboard or on-screen eye-tracking keyboard
  - Enter to save, Escape to cancel
  - Close button remains active (to abort); Move button is disabled during Edit

Move state (dwell on move button):
  - Note becomes semi-transparent, blue border
  - Button dwell detection is temporarily disabled (prevents accidental close/edit)
  - Note position follows gaze cursor in real-time via `UpdateTargetCenter`
  - A single large "placement" target covers the note area; its center follows gaze each frame
  - Dwell on the placement target for 600ms → note placed at gaze position
  - Returns to Display state after placement

Close (dwell on close button):
  - Note window closes
  - Removed from NoteManager.ActiveNotes
```

## NoteWindow Visual Design

**Layout:**
- Width: ~280px (200px text area + 80px button column)
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
- 3 circular buttons stacked vertically, 50px diameter each
- 10px gap between buttons
- 10px padding from top/right edges
- Background: dark semi-transparent circle
- Symbols: close `"\u2715"` (✕), edit `"\u270E"` (✎), move `"\u2195"` (↕)
  - Colors: close = `#FF6666` (soft red), edit = `#66FF66` (soft green), move = `#6688FF` (soft blue)
  - All three symbols render correctly in Segoe UI at 16pt and above
- Dwell progress: lime-green arc (same pattern as `ActionMenuWindow`)

**Window behavior:**
- Topmost, `WS_EX_TOOLWINDOW` (no taskbar)
- NOT click-through (captures mouse for text editing)
- Window position follows note position in physical coordinates, converted to DIPs
- `ShowInTaskbar = false`

## NoteManager Design

**Thread safety:** All `NoteManager` methods operate on the UI dispatcher thread (same thread `DwellDetector` requires). `GazeEngineAdapter` already marshals gaze events to UI thread via `Dispatcher.BeginInvoke()`. `NoteManager` subscribes to `GazeCursorMovedMessage` via `WeakReferenceMessenger` — multiple subscribers are supported and ordering does not matter since `EventCoordinator` and `NoteManager` operate on disjoint target sets.

```csharp
public class NoteManager : INoteManager
{
    private readonly List<NoteWindow> _notes = new();
    private readonly DwellDetector _dwellDetector = new(cooldownMs: 300);
    private const int MaxNotes = 8;

    public IReadOnlyList<NoteWindow> ActiveNotes => _notes.AsReadOnly();

    public NoteWindow CreateNoteAt(PhysicalPoint point);
    public void RemoveNote(string id);
    public void StartEdit(string id);
    public void FinishEdit(string id, string text);
    public void StartMove(string id);
    public void FinishMove(string id, PhysicalPoint newPosition);

    // Called each frame by GazeCursorMovedMessage handler
    public void OnGazeMoved(PhysicalPoint gazePoint, long timestamp);
}
```

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

**Move mode placement mechanism:**
1. `StartMove(id)` → add a large `DwellTarget` with ID `"{noteId}:place"`, radius ~100px, centered at note position
2. Each gaze frame: `UpdateTargetCenter("{noteId}:place", gazePoint)` keeps the placement target at the current gaze position
3. Note position also updated each frame to follow gaze (visual feedback)
4. When `TargetActivated` fires for the place target → `FinishMove(id, gazePoint)` removes the place target, sets note position, returns to Display state

**Max notes enforcement:**
- Keep `PinNote` visible in the action menu at all times
- When 8 notes are active and `PinNote` is selected, show a brief toast message ("Max 8 notes") on the oldest note, and do not create a new note
- Toast disappears after 2 seconds

## Action Menu Integration

**Enum:** `InteractionAction.PinNote = 9` (next available after `ScrollRight = 8` in the existing enum).

**Symbol:** Unicode pin `"\uD83D\uDCCC"` (📌). If standard WPF `TextBlock` renders this poorly at the button size (50px radius), fall back to a geometric glyph `"\u25C9"` (◉) or a simple `"P"` character. The existing emoji infrastructure (`Emoji.Wpf.TextBlock`) may be used if needed, matching how the emoji keyboard renders emoji.

**Default visibility:** Enabled in `ActionMenuSettings.VisibleActions` alongside LeftClick, RightClick, DoubleClick, DragStart, ScrollUp, ScrollDown.

**Execution path:**
```
ActionMenuViewModel.OnTargetActivated("PinNote")
  → ActionSelectedMessage(InteractionAction.PinNote)
    → InteractionManager.OnActionSelected(PinNote)
      → Determines target point from current state (fixation point)
      → Fires ActionRequested(PinNote, targetPoint)
        → EventCoordinator.HandleActionRequested()
          → case PinNote: _context.NoteManager.CreateNoteAt(point)
```

Note: `PinNote` does NOT call `InputSimulator` — it's a meta-action that spawns UI.

## Event Flow Summary

```
Gaze → GazeEngine → CursorMoved
  → GazeEngineAdapter (Dispatcher marshaling to UI thread)
    → GazeCursorMovedMessage (broadcast via WeakReferenceMessenger)
      ├→ EventCoordinator (handles action menu dwell)
      └→ NoteManager.OnGazeMoved(position, timestamp)
           → DwellDetector.Update(position, timestamp)
             → Hit-tests all note control targets
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

**ViewModel tests** (`EyeCtrl.ViewModels.Tests`):
- `PinNote` action symbol mapping
- `PinNote` in default visible actions

**App tests** (`EyeCtrl.App.Tests`):
- `NoteManager` create/remove/edit/move lifecycle
- `NoteManager` enforces max 8 notes
- Dwell detection on note control buttons
- Move mode gaze-follow updates position
- `EventCoordinator` routes `PinNote` to `NoteManager.CreateNoteAt()`

## Out of Scope

- Note persistence between sessions
- Rich text formatting (bold, italic, colors)
- Font size customization
- Note-to-note linking or numbering
- Drag-to-resize notes
- Note rotation
- Image attachments
- Export/import notes
