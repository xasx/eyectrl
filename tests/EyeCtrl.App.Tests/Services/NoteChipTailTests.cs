namespace EyeCtrl.App.Tests.Services;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T5b review-02 F1: the Undo chip draws NO tail. The chip placement translates the window off
/// the anchor by exactly the width and height the card lost (T5 B1's Undo-cell centring), so the
/// translated tail tip pointed 155-338 DIP away from the anchor, at empty space — a removed note
/// appearing to point at something it never pointed at. The coordinator's decision: chips draw
/// no tail; the full card keeps its anchor tail, and Undo of Removed restores it.
///
/// <para>
/// The pin runs the REAL manager path on every side — create, commit, then a dwell on ✕ (slot
/// 3) into the Removed chip, then a dwell on the chip's Undo disc — because the defect lived in
/// <c>ChipPlacementFor</c>'s interaction with the shrink, not in any single drawing call.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteChipTailTests
{
    /// <summary>One valid, non-gap sample at the manager's gate, timestamped in the detector's
    /// microseconds (the same helper NoteUndoTests drives its dwells with).</summary>
    private static void Sample(NoteManager manager, PhysicalPoint point, long tUs) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, tUs, IsInGap: false)));

    /// <summary>Arms a control (200 ms outside its circle, past the 150 ms release) and then
    /// holds it for 900 ms — enough for a fresh 600 ms dwell to complete.</summary>
    private static void ArmThenHold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            Sample(manager, new PhysicalPoint(centre.X + 10_000, centre.Y), t);
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            Sample(manager, centre, t);
    }

    [StaTheory]
    [InlineData("Right", 1200.0, 1000.0)]
    [InlineData("Left", 3000.0, 1200.0)]
    [InlineData("Below", 1920.0, 200.0)]
    [InlineData("Above", 1920.0, 2000.0)]
    public void TheUndoChip_DrawsNoTail_AndUndoRestoresTheAnchorTail(string label, double px, double py)
    {
        using var manager = new NoteManager(new WeakReferenceMessenger(),
            hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(px, py);
            manager.CreateNoteAt(anchor);
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);

            // The full card owns the anchor tail; its tip sits TipGapDip short of the anchor.
            var full = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"{label}: the full card must show the anchor tail")
                .That(full.TailVisible).IsTrue();

            // Dwell ✕ (slot 3) into the Removed chip — the production shrink path.
            ArmThenHold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.WithCustomMessage($"{label}: the ✕ dwell must reach the Removed chip")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            var chip = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"{label}: the chip placement must have no tail — the chip "
                    + "window is translated off the anchor by the shrink, so a tail on it would "
                    + "point at empty space far from the anchor (T5b review-02 F1)")
                .That(chip.TailVisible).IsFalse();

            var chipWindow = manager.WindowForTest(id)!;
            var chipCanvas = (Canvas)chipWindow.Content;
            chipCanvas.Measure(new Size(chipWindow.Width, chipWindow.Height));
            chipCanvas.Arrange(new Rect(new Point(0, 0), new Size(chipWindow.Width, chipWindow.Height)));
            Check.WithCustomMessage($"{label}: the chip window's canvas must draw no tail "
                    + $"polygon — placement.TailVisible alone would stay green over a window "
                    + "that still draws one (the pre-fix defect was exactly that pairing)")
                .That(chipCanvas.Children.OfType<Polygon>()).IsEmpty();

            // Dwell the chip's Undo disc (slot 0): the full card comes back WITH its anchor tail.
            ArmThenHold(manager, NoteTestBench.CellCentre(manager, id, 0), startUs: 2_000_000);
            Check.WithCustomMessage($"{label}: Undo of the Removed chip must restore the note "
                    + "to Display").That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            var restored = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"{label}: the restored full card must show the tail again")
                .That(restored.TailVisible).IsTrue();
            var anchorDip = new Point(anchor.X / DpiHelper.ScaleX, anchor.Y / DpiHelper.ScaleY);
            var tip = restored.TailTip!.Value;
            var offAnchor = Math.Sqrt(
                Math.Pow(tip.X - anchorDip.X, 2) + Math.Pow(tip.Y - anchorDip.Y, 2));
            Check.WithCustomMessage($"{label}: the restored tail tip must sit "
                    + $"{NoteLayout.Constants.TipGapDip:F0} DIP short of the anchor, not at the "
                    + $"translated position (measured {offAnchor:F1} DIP)")
                .That(offAnchor).IsCloseTo(NoteLayout.Constants.TipGapDip, 1e-6);

            // T5b review-03 R1: the placement's tip alone would stay green over a canvas that
            // draws a stale extra polygon — pin the DRAWN shape count too.
            var restoredWindow = manager.WindowForTest(id)!;
            var restoredCanvas = (Canvas)restoredWindow.Content;
            restoredCanvas.Measure(new Size(restoredWindow.Width, restoredWindow.Height));
            restoredCanvas.Arrange(new Rect(new Point(0, 0), new Size(restoredWindow.Width, restoredWindow.Height)));
            Check.WithCustomMessage($"{label}: the restored card's canvas must draw exactly one tail polygon")
                .That(restoredCanvas.Children.OfType<Polygon>().Count()).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
