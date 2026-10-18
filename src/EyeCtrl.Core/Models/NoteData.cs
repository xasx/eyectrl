namespace EyeCtrl.Core.Models;

/// <summary>States of a pinned note on the overlay.</summary>
public enum NoteState
{
    /// <summary>The note is displayed but not editable.</summary>
    Display,
    /// <summary>The note is in edit mode (text can be entered).</summary>
    Edit,
    /// <summary>The note is being repositioned via gaze dwell.</summary>
    Move,
    /// <summary>
    /// The window re-renders in place as an Undo chip (P2 D5): the card's dwell targets are
    /// unregistered, a single <c>{id}:undo</c> target lives in the manager's second detector,
    /// and a clock counts the chip's reachable time (8 s) until Undo or expiry.
    /// </summary>
    PendingUndo
}

/// <summary>What kind of destruction an Undo chip can reverse (P2 D5's table). The kind decides
/// the chip's text, Undo's destination, expiry's destination, and whether the chip yields its
/// capacity slot.</summary>
public enum PendingUndoKind
{
    /// <summary>✕ in Display, or Done on an emptied existing note. Undo → Display with the saved
    /// text; expiry → gone. The chip yields its slot.</summary>
    Removed,
    /// <summary>Discard of a changed draft on a NEW note. Undo → Edit with the draft; expiry →
    /// gone. The chip yields its slot.</summary>
    DiscardedNew,
    /// <summary>Discard of a changed draft on an EXISTING note. Undo → Edit with the draft;
    /// expiry → Display with the previous text. The chip does NOT yield: it counts as a live
    /// note, because the saved text still exists behind it.</summary>
    DiscardedExisting
}

/// <summary>A pinned on-screen note that can be created, edited, and repositioned via gaze.</summary>
public class NoteData
{
    /// <summary>Unique identifier for this note, generated at creation.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();
    /// <summary>The text content of the note.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>
    /// The last saved text (P1 D4): <c>null</c> for a note that was never saved, so Done on an
    /// emptied draft of a NEW note removes it while Done on an emptied EXISTING note restores this.
    /// </summary>
    public string? PreviousText { get; set; }
    /// <summary>The in-progress text while the note is in Edit (P1 D4); mirrors Text in Display.</summary>
    public string Draft { get; set; } = string.Empty;
    /// <summary>The position of the note in physical screen pixels.</summary>
    public PhysicalPoint Position { get; set; }
    /// <summary>The current interaction state of the note (edit, display, move, or pending undo).</summary>
    public NoteState State { get; set; } = NoteState.Edit;
    /// <summary>What a <see cref="NoteState.PendingUndo"/> chip can reverse. Null in every other
    /// state; set exactly when the chip is entered (P2 D5).</summary>
    public PendingUndoKind? UndoKind { get; set; }
}
