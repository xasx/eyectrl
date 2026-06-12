namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class NoteDataTests
{
    [Fact]
    public void Constructor_GeneratesUniqueId()
    {
        var a = new NoteData();
        var b = new NoteData();
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrWhiteSpace(a.Id));
    }

    [Fact]
    public void Constructor_DefaultsToEditState()
    {
        var note = new NoteData();
        Assert.Equal(NoteState.Edit, note.State);
    }

    [Fact]
    public void Constructor_HasEmptyText()
    {
        var note = new NoteData();
        Assert.Equal(string.Empty, note.Text);
    }

    [Fact]
    public void Position_CanBeSet()
    {
        var note = new NoteData();
        note.Position = new PhysicalPoint(100, 200);
        Assert.Equal(100, note.Position.X);
        Assert.Equal(200, note.Position.Y);
    }

    [Fact]
    public void State_Transitions_BetweenValues()
    {
        var note = new NoteData();
        note.State = NoteState.Display;
        Assert.Equal(NoteState.Display, note.State);
        note.State = NoteState.Move;
        Assert.Equal(NoteState.Move, note.State);
    }

    [Fact]
    public void NoteState_Enum_HasThreeValues()
    {
        var values = Enum.GetValues<NoteState>();
        Assert.Equal(3, values.Length);
        Assert.Contains(NoteState.Display, values);
        Assert.Contains(NoteState.Edit, values);
        Assert.Contains(NoteState.Move, values);
    }
}
