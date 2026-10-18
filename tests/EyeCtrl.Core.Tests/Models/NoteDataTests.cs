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
        Check.That(a.Id).IsNotEqualTo(b.Id);
        Check.That(string.IsNullOrWhiteSpace(a.Id)).IsFalse();
    }

    [Fact]
    public void Constructor_DefaultsToEditState()
    {
        var note = new NoteData();
        Check.That(note.State).IsEqualTo(NoteState.Edit);
    }

    [Fact]
    public void Constructor_HasEmptyText()
    {
        var note = new NoteData();
        Check.That(note.Text).IsEqualTo(string.Empty);
    }

    [Fact]
    public void Position_CanBeSet()
    {
        var note = new NoteData();
        note.Position = new PhysicalPoint(100, 200);
        Check.That(note.Position.X).IsEqualTo(100);
        Check.That(note.Position.Y).IsEqualTo(200);
    }

    [Fact]
    public void State_Transitions_BetweenValues()
    {
        var note = new NoteData();
        note.State = NoteState.Display;
        Check.That(note.State).IsEqualTo(NoteState.Display);
        note.State = NoteState.Move;
        Check.That(note.State).IsEqualTo(NoteState.Move);
    }

    [Fact]
    public void NoteState_Enum_HasFourValues()
    {
        // P2 D5 added PendingUndo — the window-renders-as-a-chip state.
        var values = Enum.GetValues<NoteState>();
        Check.That(values.Length).IsEqualTo(4);
        Check.That(values).Contains(NoteState.Display);
        Check.That(values).Contains(NoteState.Edit);
        Check.That(values).Contains(NoteState.Move);
        Check.That(values).Contains(NoteState.PendingUndo);
    }

    [Fact]
    public void PendingUndoKind_Enum_HasTheThreeKinds()
    {
        // P2 D5's table: Removed, DiscardedNew, DiscardedExisting — the kind decides the chip
        // text, Undo's and expiry's destination, and whether the chip yields its slot.
        var values = Enum.GetValues<PendingUndoKind>();
        Check.That(values.Length).IsEqualTo(3);
        Check.That(values).Contains(PendingUndoKind.Removed);
        Check.That(values).Contains(PendingUndoKind.DiscardedNew);
        Check.That(values).Contains(PendingUndoKind.DiscardedExisting);
    }
}
