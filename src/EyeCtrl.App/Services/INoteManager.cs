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
}
