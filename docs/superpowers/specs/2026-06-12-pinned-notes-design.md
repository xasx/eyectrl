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
| Move interaction | Gaze-follow: dwell on move button → note follows gaze → dwell to place |
| Controls visibility | Always visible (large buttons on right side of note) |

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
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `INoteManager` as singleton in DI |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Add `NoteManager` to `EventCoordinationContext` |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` |
| `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` | Add pin symbol mapping for `PinNote` in `GetActionSymbol()` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add note lifecycle messages if needed |

## Data Model

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
  - Text becomes editable (focus the note for keyboard input)
  - Enter to save, Escape to cancel

Move state (dwell on move button):
  - Note becomes semi-transparent
  - Follows gaze cursor in real-time
  - Dwell (fixation) to place at new position
  - Moves to Display state after placement

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
- Symbols: ✕ (close, red), ✎ (edit, green), ↕ (move, blue)
- Dwell progress: lime-green arc (same pattern as ActionMenuWindow)

**Window behavior:**
- Topmost, `WS_EX_TOOLWINDOW` (no taskbar)
- NOT click-through (captures mouse for text editing)
- Window position follows note position in physical coordinates, converted to DIPs
- `ShowInTaskbar = false`

## NoteManager Design

```csharp
public class NoteManager : INoteManager
{
    private readonly List<NoteWindow> _notes = new();
    private readonly DwellDetector _dwellDetector = new();
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

**Dwell detection:**
- Single `DwellDetector` instance shared across all notes
- Each note registers its 3 buttons as `DwellTarget`s when visible
- Dwell time: 600ms (matching `ActionMenuDwellMs` from `DwellSettings`)
- On `TargetActivated`: dispatches to `RemoveNote`, `StartEdit`, or `StartMove` based on the target ID

**Max notes enforcement:**
- When 8 notes are active, `PinNote` action is hidden from menu (or shown greyed out)
- Alternatively: selecting `PinNote` at capacity shows a brief toast "Max notes reached"

## Action Menu Integration

**Enum:** `InteractionAction.PinNote = 9` (10th action)

**Symbol:** Unicode pin `"\uD83D\uDCCC"` (📌) — or simpler pin glyph if emoji rendering is problematic

**Default visibility:** Enabled in `ActionMenuSettings.VisibleActions`

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
  → GazeEngineAdapter → GazeCursorMovedMessage
    → NoteManager.OnGazeMoved(position, timestamp)
      → DwellDetector.Update(position, timestamp)
        → Hit-tests all note control buttons
        → TargetProgress → NoteWindow.UpdateDwellProgress(buttonId, progress)
        → TargetActivated → dispatch close/edit/move

Move mode:
  NoteManager.StartMove(id)
    → NoteWindow.State = Move (semi-transparent, blue border)
    → NoteManager.OnGazeMoved updates note position in real-time
    → Fixation detection on new position
    → NoteManager.FinishMove(id, newPoint)

Edit mode:
  NoteManager.StartEdit(id)
    → NoteWindow.State = Edit (green border, text input focused)
    → User types... Enter key
    → NoteWindow sends text to NoteManager.FinishEdit(id, text)
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
