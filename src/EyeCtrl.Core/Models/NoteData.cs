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
    /// <summary>The position of the note in physical screen pixels.</summary>
    public PhysicalPoint Position { get; set; }
    /// <summary>The current interaction state of the note (edit, display, or move).</summary>
    public NoteState State { get; set; } = NoteState.Edit;
}
