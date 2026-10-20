namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The chip shrink (P4 D3, the owner default "the note window shrunk in place"): entering
/// PendingUndo resizes the window to the chip card at the FULL card top-left, so the Undo cell
/// (slot 0) keeps its centre - a gaze resting on it is never displaced by the shrink. T6 while
/// a chip is pending re-derives the same way. Undo restores the full card.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteChipShrinkTests
{
    private static (NoteManager Manager, WeakReferenceMessenger Messenger) MakeManager()
    {
        var messenger = new WeakReferenceMessenger();
        return (new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider), messenger);
    }

    private static string SavedNote(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static void Hold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(centre.X + 10_000, centre.Y), t, IsInGap: false)));
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(centre, t, IsInGap: false)));
    }

    [StaFact]
    public void EnteringPendingUndo_ShrinksTheWindowInPlace_AndTheUndoCellDoesNotMove()
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            var window = manager.WindowForTest(id)!;
            var full = manager.PlacementForTest(id)!;
            var undoBefore = NoteTestBench.CellCentre(manager, id, 0);

            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0); // x- claims the chip
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            var chip = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"the chip card {chip.Card} must be smaller than the full card {full.Card}")
                .That(chip.Card.Width * chip.Card.Height).IsStrictlyLessThan(full.Card.Width * full.Card.Height);
            // B1: the anchor is the COLUMN edge - the card edge away from the tail. Right keeps
            // its right edge fixed (and its top), so the Undo cell cannot move.
            Check.WithCustomMessage("the shrunk window keeps the full window column edge")
                .That((chip.Window.Right, chip.Window.Top))
                .IsEqualTo((full.Window.Right, full.Window.Top));
            Check.WithCustomMessage("the real window follows the chip placement — within the "
                    + "half-DIP rounding at 2× (the grown strip is a fractional ink height)")
                .That(Math.Abs(window.Width - chip.Window.Width) < 0.5
                    && Math.Abs(window.Height - chip.Window.Height) < 0.5).IsTrue();
            Check.WithCustomMessage("the Undo cell keeps its centre - the gaze is never displaced")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void ChromeRects_FollowTheShrunkChip_CardPlusOneCell()
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            var chip = manager.PlacementForTest(id)!;
            var rects = manager.ChromeRects();
            Check.WithCustomMessage("the chip reports exactly its card and the one Undo cell")
                .That(rects).HasSize(2);

            double half = DpiHelper.LogicalToPhysicalX(LayoutConstants.NoteCellDip / 2);
            var undo = NoteTestBench.CellCentre(manager, id, 0);
            Check.That(rects).Contains(new PhysicalRect(undo.X - half, undo.Y - half, half * 2, half * 2));
            Check.That(rects).Contains(DipRectToPhysical(manager, chip.Card));
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    [StaFact]
    public void AWorkAreaChange_WhileAChipIsPending_MovesTheChip_AndTheUndoTargetFollows()
    {
        var (manager, messenger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(2600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            var before = manager.PlacementForTest(id)!;
            var oldUndo = NoteTestBench.CellCentre(manager, id, 0);

            // T6 with a REAL geometry change: the work area shrinks, the chip clamps to a new
            // spot - and its Undo target must follow (review-01 M3), not stay at the old,
            // now-empty spot.
            var area = new Rect(0, 0, 1300, 1100);
            manager.UseWorkAreaForTest(() => area);
            messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1300, 1100, 2, 2)));

            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"the chip must actually move (before {before.Window}, after {after.Window})")
                .That((after.Window.Left, after.Window.Top))
                .IsNotEqualTo((before.Window.Left, before.Window.Top));
            Check.WithCustomMessage("the chip stays shrunk through the move")
                .That((after.Window.Width, after.Window.Height))
                .IsEqualTo((before.Window.Width, before.Window.Height));
            var newUndo = NoteTestBench.CellCentre(manager, id, 0);
            Check.WithCustomMessage($"the Undo target must follow the drawn cell (old {oldUndo}, new {newUndo})")
                .That(newUndo).IsNotEqualTo(oldUndo);

            // R14, both branches: a hold on the DRAWN new centre fires Undo; a hold on the old,
            // empty spot must not.
            for (long t = 2_000_000; t <= 2_300_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(newUndo.X + 10_000, newUndo.Y), t, IsInGap: false)));
            for (long t = 2_400_000; t <= 5_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(newUndo, t, IsInGap: false)));
            Check.WithCustomMessage("a hold on the DRAWN (new) Undo cell must fire Undo")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("saved text");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    // R14, old branch: the mirror scenario - a hold on the OLD centre after a T6 move fires
    // nothing (the ghost is gone).
    [StaFact]
    public void AWorkAreaMove_LeavesTheOldUndoSpotDead()
    {
        var (manager, messenger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(2600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            var oldUndo = NoteTestBench.CellCentre(manager, id, 0);

            var area = new Rect(0, 0, 1300, 1100);
            manager.UseWorkAreaForTest(() => area);
            messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1300, 1100, 2, 2)));
            var newUndo = NoteTestBench.CellCentre(manager, id, 0);
            Check.That(newUndo).IsNotEqualTo(oldUndo);

            for (long t = 2_000_000; t <= 2_300_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(oldUndo.X + 10_000, oldUndo.Y), t, IsInGap: false)));
            for (long t = 2_400_000; t <= 5_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(oldUndo, t, IsInGap: false)));
            Check.WithCustomMessage("a hold on the OLD, empty spot must not fire Undo")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    // R12 (adopted): a LEFT-side chip keeps its Undo cell through a text-size change - the
    // translation reference is the chip itself, so a size-independent chip never moves.
    [StaFact]
    public void R12_ALeftSideChip_KeepsItsUndoCell_ThroughATextSizeChange()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(3000, 1200)); // DIP (1500, 600): Left fits, Right does not
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);
            Check.That(manager.PlacementForTest(id)!.Side).IsEqualTo(NoteSide.Left);

            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            var undoBefore = NoteTestBench.CellCentre(manager, id, 0);

            settings.TextSizeDip = 40;

            Check.WithCustomMessage($"the Undo cell moved from {undoBefore} to {NoteTestBench.CellCentre(manager, id, 0)}")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Undo_RestoresTheFullCard()
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            var full = manager.PlacementForTest(id)!;
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            Hold(manager, NoteTestBench.CellCentre(manager, id, 0), startUs: 2_000_000); // the Undo dwell
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            var restored = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("Undo restores the full card size")
                .That((restored.Card.Width, restored.Card.Height))
                .IsEqualTo((full.Card.Width, full.Card.Height));
            Check.WithCustomMessage("Undo restores the full window")
                .That((restored.Window.Width, restored.Window.Height))
                .IsEqualTo((full.Window.Width, full.Window.Height));
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    private static PhysicalRect DipRectToPhysical(NoteManager manager, Rect r) =>
        new(
            DpiHelper.LogicalToPhysicalX(r.Left),
            DpiHelper.LogicalToPhysicalY(r.Top),
            DpiHelper.LogicalToPhysicalX(r.Width),
            DpiHelper.LogicalToPhysicalY(r.Height));
}
