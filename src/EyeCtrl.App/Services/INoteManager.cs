namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

public interface INoteManager
{
    IReadOnlyList<NoteData> ActiveNotes { get; }
    bool IsAtCapacity { get; }

    void CreateNoteAt(PhysicalPoint point);
    void RemoveNote(string id);
    void StartEdit(string id);
    void FinishEdit(string id, string text);
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
}
