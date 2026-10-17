namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The pinned-note lifecycle. Extends <see cref="INoteTextInput"/> (P1 D1): the keyboard
/// dispatchers read the manager through <see cref="EventCoordinationContext.NoteManager"/> and
/// route every keyboard output into the editing note while <see cref="INoteTextInput.IsEditing"/>
/// holds, so the positional context record gains no new member.
/// </summary>
public interface INoteManager : INoteTextInput
{
    IReadOnlyList<NoteData> ActiveNotes { get; }
    bool IsAtCapacity { get; }

    void CreateNoteAt(PhysicalPoint point);
    void RemoveNote(string id);
    void StartEdit(string id);

    /// <summary>
    /// The ONLY Edit exit in P1 (D4): the note's ✓ Done dwell. P1 always passes
    /// <c>commit: true</c> — a commit saves the trimmed draft, and a brand-new note whose draft
    /// is empty disappears (nothing was ever saved). Discard arrives in P2 with Undo.
    /// </summary>
    void FinishEdit(string id, bool commit);

    void StartMove(string id);
    void FinishMove(string id, PhysicalPoint newPosition);

    void OnGazeMoved(PhysicalPoint gazePoint, long timestamp);

    /// <summary>
    /// Suspends note dwell while the predicate returns true (sub-pixel build plan C15). Note
    /// targets are gaze-only surfaces under no window, and dwell ignores z-order — so while the
    /// probe owns the screen (its opaque window covers the notes) a note's close/edit/move targets
    /// would otherwise stay live underneath it. Wired once from EventCoordinator to
    /// <c>() =&gt; _modal.ProbeOpen</c>.
    /// </summary>
    void SetSuspendPredicate(Func<bool> predicate);

    /// <summary>
    /// Wired once from EventCoordinator (P1 D5), like <see cref="SetSuspendPredicate"/>: the
    /// keyboard visibility probe that drives the summon/release of the eye keyboard, the ⌨
    /// Show-keyboard control, the keyboard-gate on note dwell, and the foreign-hide detection.
    /// </summary>
    void SetKeyboardProbe(IKeyboardProbe probe);
}
