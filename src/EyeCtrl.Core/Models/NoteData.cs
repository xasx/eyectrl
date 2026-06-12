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
    public NoteState State { get; set; } = NoteState.Edit;
}
