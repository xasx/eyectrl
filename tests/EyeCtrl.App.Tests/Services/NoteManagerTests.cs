namespace EyeCtrl.App.Tests.Services;

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
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(100, 200));

        Assert.Single(manager.ActiveNotes);
        Assert.Equal(100, manager.ActiveNotes[0].Position.X);
        Assert.Equal(200, manager.ActiveNotes[0].Position.Y);
    }

    [StaFact]
    public void CreateNote_DefaultsToEditState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Assert.Equal(NoteState.Edit, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void RemoveNote_RemovesFromActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.RemoveNote(id);

        Assert.Empty(manager.ActiveNotes);
    }

    [StaFact]
    public void IsAtCapacity_ReturnsTrue_WhenMaxReached()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));

        Assert.True(manager.IsAtCapacity);
    }

    [StaFact]
    public void CreateNote_DoesNotAdd_WhenAtCapacity()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));
        manager.CreateNoteAt(new PhysicalPoint(999, 999));

        Assert.Equal(8, manager.ActiveNotes.Count);
    }

    [StaFact]
    public void StartEdit_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
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
        using var manager = new NoteManager(messenger);
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
        using var manager = new NoteManager(messenger);
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
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.FinishMove(id, new PhysicalPoint(500, 600));

        Assert.Equal(500, manager.ActiveNotes[0].Position.X);
        Assert.Equal(600, manager.ActiveNotes[0].Position.Y);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void OnGazeMoved_UpdatesLastGazePoint_ForPlaceTarget()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.OnGazeMoved(new PhysicalPoint(300, 400), 1000000);
        manager.FinishMove(id, new PhysicalPoint(300, 400));

        Assert.Equal(300, manager.ActiveNotes[0].Position.X);
        Assert.Equal(400, manager.ActiveNotes[0].Position.Y);
    }

    [StaFact]
    public void MoveOneNote_KeepsOtherNotesControls()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        manager.CreateNoteAt(new PhysicalPoint(300, 0));
        var id1 = manager.ActiveNotes[0].Id;
        var id2 = manager.ActiveNotes[1].Id;
        manager.FinishEdit(id1, "note1");
        manager.FinishEdit(id2, "note2");

        manager.StartMove(id1);
        manager.FinishMove(id1, new PhysicalPoint(100, 0));

        // Both notes should be in Display state and have their targets
        Assert.Equal(2, manager.ActiveNotes.Count);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[1].State);
    }

    [StaFact]
    public void StartEdit_DoesNotTriggerEditTwice()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "original");

        // Enter edit, cancel, re-enter — should work without retrigger
        manager.StartEdit(id);
        manager.FinishEdit(id, "original"); // cancel
        manager.StartEdit(id);
        manager.FinishEdit(id, "changed");

        Assert.Equal("changed", manager.ActiveNotes[0].Text);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void CreateNote_ShowsWindow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(400, 300));

        // Regression guard: the original code only called Show() inside the window's own Loaded
        // handler, which never fires, so the note was never displayed. The window must be visible.
        Assert.True(manager.AllWindowsVisible);
    }

    [StaFact]
    public void CloseButtonDwell_AtVisiblePosition_RemovesNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "x"); // -> Display: registers close/edit/move dwell targets

        // Close-button center, computed the same way NoteWindow lays the button out (DPI 1.0 here,
        // so logical == physical). Regression guard for the dwell-target/visual-button mismatch:
        // the buggy code placed the target ~150px right of where the button actually renders.
        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        double cy = EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        var closeCenter = new PhysicalPoint(winLeft + cx, winTop + cy);

        // Hold gaze on the close button past the 600ms dwell threshold.
        for (long t = 0; t <= 800_000; t += 33_000)
            manager.OnGazeMoved(closeCenter, t);

        Assert.Empty(manager.ActiveNotes);
    }
}
