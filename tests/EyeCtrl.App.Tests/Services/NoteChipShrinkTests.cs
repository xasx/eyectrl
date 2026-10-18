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
            Check.WithCustomMessage("the shrunk window keeps the full window top-left (in place)")
                .That(new Point(chip.Window.Left, chip.Window.Top))
                .IsEqualTo(new Point(full.Window.Left, full.Window.Top));
            Check.WithCustomMessage("the real window follows the chip placement")
                .That((window.Width, window.Height))
                .IsEqualTo((chip.Window.Width, chip.Window.Height));
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
    public void AWorkAreaChange_WhileAChipIsPending_KeepsItShrunk_AndTheUndoDwellLive()
    {
        var (manager, messenger) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            var chip = manager.PlacementForTest(id)!;
            var undo = NoteTestBench.CellCentre(manager, id, 0);

            // T6 while the chip is pending: the chip re-derives SHRUNK, in place, and the Undo
            // target neither moves nor resets - the dwell the gaze is holding keeps running.
            messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1920, 1200, 2, 2)));
            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the chip stays shrunk through a T6 re-place")
                .That((after.Window.Width, after.Window.Height))
                .IsEqualTo((chip.Window.Width, chip.Window.Height));
            Check.WithCustomMessage("the Undo target never moves (the gaze stays on it)")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undo);

            // The held gaze completes the Undo dwell (the chip did not shrink away).
            for (long t = 2_000_000; t <= 3_200_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(undo, t, IsInGap: false)));
            Check.WithCustomMessage("a gaze resting on the Undo target still fires it after the re-place")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("saved text");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
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
