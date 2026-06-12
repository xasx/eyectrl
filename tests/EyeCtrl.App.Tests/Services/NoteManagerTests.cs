namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NoteManagerTests
{
    static NoteManagerTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [StaFact]
    public void CreateNote_AddsToActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(100, 200));

        Assert.Single(manager.ActiveNotes);
        Assert.Equal(100, manager.ActiveNotes[0].Position.X);
        Assert.Equal(200, manager.ActiveNotes[0].Position.Y);
    }

    [StaFact]
    public void CreateNote_DefaultsToEditState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Assert.Equal(NoteState.Edit, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void RemoveNote_RemovesFromActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.RemoveNote(id);

        Assert.Empty(manager.ActiveNotes);
    }

    [StaFact]
    public void IsAtCapacity_ReturnsTrue_WhenMaxReached()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));

        Assert.True(manager.IsAtCapacity);
    }

    [StaFact]
    public void StartEdit_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test text");

        manager.StartEdit(id);

        Assert.Equal(NoteState.Edit, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void FinishEdit_SavesText()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.FinishEdit(id, "hello world");

        Assert.Equal("hello world", manager.ActiveNotes[0].Text);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void StartMove_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");

        manager.StartMove(id);

        Assert.Equal(NoteState.Move, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void FinishMove_UpdatesPosition()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.FinishMove(id, new PhysicalPoint(500, 600));

        Assert.Equal(500, manager.ActiveNotes[0].Position.X);
        Assert.Equal(600, manager.ActiveNotes[0].Position.Y);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }
}
