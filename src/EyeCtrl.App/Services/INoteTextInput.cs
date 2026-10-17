namespace EyeCtrl.App.Services;

using EyeCtrl.App.Services.Notes;

/// <summary>
/// The typing seam the keyboard dispatchers route through while a note is in Edit (P1 D1).
/// Everything the eye keyboard produces — keys, predictions, special keys, canned text and
/// shortcuts — reaches the note through <see cref="Apply"/> or is dropped, and never becomes
/// OS input while <see cref="IsEditing"/> holds. <see cref="INoteManager"/> extends this, so the
/// positional <see cref="EventCoordinationContext"/> gains no new member.
/// </summary>
public interface INoteTextInput
{
    /// <summary>True while some note is being edited: every keyboard output routes here.</summary>
    bool IsEditing { get; }

    /// <summary>
    /// Applies one mapped edit to the editing note's draft and re-renders its card once.
    /// A prediction is two edits (delete the composing input, append the word) and arrives as
    /// one <see cref="NotePrediction"/>, applied atomically (P1 Risks).
    /// </summary>
    void Apply(NoteEdit edit);
}
