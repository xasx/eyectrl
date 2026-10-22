namespace EyeCtrl.Core.Models;

/// <summary>States of a pinned note on the overlay.</summary>
public enum NoteState
{
    /// <summary>The note is displayed but not editable.</summary>
    Display,
    /// <summary>The note is in edit mode (text can be entered).</summary>
    Edit,
    /// <summary>The note is being repositioned via gaze dwell.</summary>
    Move
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
    /// <summary>The current interaction state of the note (edit, display, or move).</summary>
    public NoteState State { get; set; } = NoteState.Edit;
}
