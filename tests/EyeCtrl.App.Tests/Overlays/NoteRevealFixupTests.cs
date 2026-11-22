namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Fixup-01 (drv review, 22.11): the reviewer's probes A, C, E, F and G ported as real tests.
/// They were RED on the reviewed head and are green after F1/F2/F4/F5. The remaining fixup pins
/// (F1 arming/deliberate look, F2 belt, F3 window/fill, F6 hide geometry, F7 at-rest overlap,
/// F8 gate reset) live below the ports.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteRevealFixupTests
{
    private static NoteManager NewManager(double textSize, WeakReferenceMessenger messenger,
        IStruggleTelemetrySink? sink = null) =>
        new(messenger, sink: sink, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider,
            noteSettings: new NoteSettings { TextSizeDip = (int)textSize });

    private static void Sample(NoteManager manager, PhysicalPoint point, long t) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, t, IsInGap: false)));

    private static long Hold(NoteManager manager, PhysicalPoint point, long start, long ms,
        int stepUs = 33_000)
    {
        long end = start + (ms * 1000);
        for (long t = start; t <= end; t += stepUs)
            Sample(manager, point, t);
        return end + stepUs;
    }

    private static string Saved(NoteManager manager, string text, double dipX, double dipY)
    {
        manager.CreateNoteAt(new PhysicalPoint(dipX * 2, dipY * 2));
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static PhysicalPoint P(double dipX, double dipY) => new(
        DpiHelper.LogicalToPhysicalX(dipX), DpiHelper.LogicalToPhysicalY(dipY));

    // ── probe A (F1): a 4 DIP drift across the card's bottom edge after the reveal ──

    [StaTheory]
    [InlineData(18.0, 0)]
    [InlineData(36.0, 0)]
    [InlineData(48.0, 0)]
    [InlineData(36.0, 3)]
    public void F1_A_DriftAcrossTheCardBottomEdge_AfterTheReveal_FiresNothing(double size, int slot)
    {
        var messenger = new WeakReferenceMessenger();
        int cues = 0;
        messenger.Register<object, DwellActivationMessage>(this, (_, _) => cues++);
        using var manager = NewManager(size, messenger);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "a fairly long line of note text here", 600, 500);
            var rest = manager.DrawnCardForTest(id);
            var cell = NoteTestBench.CellCentre(manager, id, slot);
            double cellTopDip = DpiHelper.PhysicalToLogicalY(cell.Y) - (LayoutConstants.NoteCellDip / 2);
            double x = DpiHelper.PhysicalToLogicalX(cell.X);
            var above = P(x, cellTopDip - 2);
            var below = P(x, cellTopDip + 2);
            Check.WithCustomMessage("the reveal look rests above the cell, on the at-rest card")
                .That(DpiHelper.PhysicalToLogicalY(above.Y) < rest.Bottom
                    && DpiHelper.PhysicalToLogicalY(above.Y) > rest.Top
                    && x < rest.Right && x > rest.Left).IsTrue();

            // The reveal look: 600 ms above the cell (the reveal fires at 500), then the 4 DIP
            // drift down for 700 ms. F1: the row must not fire within the look.
            long t = Hold(manager, P(x + 3000, cellTopDip), 0, 200);
            t = Hold(manager, above, t, 600);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            t = Hold(manager, below, t, 700);
            Check.WithCustomMessage(
                    $"{size}/{slot}: a 4 DIP drift after the reveal fired a control "
                    + $"(state {manager.ActiveNotes[0].State}, cues {cues})")
                .That(manager.ActiveNotes[0].State == NoteState.Display && cues == 0).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── probe C (F2): the chip's ↺ must not sit where the fired ✕ was ──

    [StaTheory]
    [InlineData(18.0, 300, 500)]
    [InlineData(36.0, 300, 500)]
    [InlineData(48.0, 300, 500)]
    [InlineData(36.0, 1850, 500)]
    [InlineData(36.0, 900, 1100)]
    [InlineData(36.0, 900, 40)]
    [InlineData(18.0, 1850, 500)]
    [InlineData(48.0, 1850, 500)]
    public void F2_C_TheChipUndo_IsAtLeastTwoCellsFromTheFiredClose(double size, double ax, double ay)
    {
        using var manager = NewManager(size, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", ax, ay);
            var close = NoteTestBench.CellCentre(manager, id, 3);
            var undo = manager.ChipUndoCentreForTest(id);
            double dx = DpiHelper.PhysicalToLogicalX(Math.Abs(close.X - undo.X));
            double dy = DpiHelper.PhysicalToLogicalY(Math.Abs(close.Y - undo.Y));
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            double half = LayoutConstants.NoteCellDip / 2;
            var closeCell = new Rect(
                DpiHelper.PhysicalToLogicalX(close.X) - half,
                DpiHelper.PhysicalToLogicalY(close.Y) - half,
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            var undoCell = new Rect(
                DpiHelper.PhysicalToLogicalX(undo.X) - half,
                DpiHelper.PhysicalToLogicalY(undo.Y) - half,
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage(
                    $"{size}@({ax},{ay}) side {manager.PlacementForTest(id)!.Side}: ↺ is only "
                    + $"{distance:F1} DIP from ✕ (dx {dx:F1}, dy {dy:F1}; need >= {2 * LayoutConstants.NoteCellDip})")
                .That(distance).IsGreaterOrEqualThan(2 * LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"{size}@({ax},{ay}): the ↺ and ✕ hit cells must not intersect")
                .That(closeCell.IntersectsWith(undoCell)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── probe E (F2): a 5 DIP drift from the fired ✕ toward the born ↺ must not undo ──

    [StaTheory]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void F2_E_ADriftFromTheFiredCloseIntoTheBornUndo_DoesNotUndo(double size)
    {
        using var manager = NewManager(size, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 300, 500);
            var close = NoteTestBench.CellCentre(manager, id, 3);
            var undo = manager.ChipUndoCentreForTest(id);
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, close, t, 800);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            double vx = undo.X - close.X, vy = undo.Y - close.Y;
            double len = Math.Sqrt((vx * vx) + (vy * vy));
            double step = DpiHelper.LogicalToPhysicalX(5);
            var drift = new PhysicalPoint(close.X + (vx / len * step), close.Y + (vy / len * step));
            t = Hold(manager, drift, t, 900);
            Check.WithCustomMessage(
                    $"{size}: a 5 DIP drift from the fired ✕ undid the close (state {manager.ActiveNotes[0].State})")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── probe F (F4): the reveal must not feed the Notes struggle telemetry ──

    private sealed class Recorder : IStruggleTelemetrySink
    {
        public readonly List<DwellOutcome> Outcomes = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    [StaFact]
    public void F4_F_TheRevealReportsNoNotesOutcome()
    {
        var sink = new Recorder();
        using var manager = NewManager(NoteSettings.DefaultTextSizeDip,
            new WeakReferenceMessenger(), sink);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "read me", 600, 500);
            var rest = manager.DrawnCardForTest(id);
            var centre = P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2));
            var away = new PhysicalPoint(centre.X + 3000, centre.Y);
            long t = 0;
            // Three reading glances of 400 ms (80 % of the reveal dwell), then one full reveal.
            for (int glance = 0; glance < 3; glance++)
            {
                t = Hold(manager, away, t, 300);
                t = Hold(manager, centre, t, 400);
            }
            t = Hold(manager, away, t, 300);
            t = Hold(manager, centre, t, 600);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            var reveal = sink.Outcomes.Where(o => o.TargetId.EndsWith(":reveal", StringComparison.Ordinal)).ToList();
            Check.WithCustomMessage(
                    $"reveal outcomes reported to the Notes telemetry: "
                    + string.Join(", ", reveal.Select(o => $"{o.Context}/{o.Kind}/{o.ProgressAtEnd:F2}")))
                .That(reveal).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── probe G (F5): a gaze held where it revealed must not hide the row ──

    [StaFact]
    public void F5_G_AGazeHeldWhereItRevealed_NeverHidesTheRow()
    {
        using var manager = NewManager(18, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            var target = manager.RevealTargetForTest(id)!.Value;
            var card = manager.PlacementForTest(id)!.Card;
            var p = P(target.Left + (target.Width / 2), target.Top + 4);
            Check.WithCustomMessage($"the probe point must be inside the target {target} and above the hide rect of {card}")
                .That(target.Top + 4 < card.Top - 24).IsTrue();

            long t = Hold(manager, new PhysicalPoint(p.X + 3000, p.Y), 0, 300);
            t = Hold(manager, p, t, 700);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            t = Hold(manager, p, t, 2_500);
            Check.WithCustomMessage("the row hid while the gaze stayed where it revealed it")
                .That(manager.IsRevealedForTest(id)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F1: slow drift inside the protected region, and the deliberate way down ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void F1_SlowDriftInsideTheAtRestCardOrTarget_NeverFires(double size)
    {
        var messenger = new WeakReferenceMessenger();
        int cues = 0;
        messenger.Register<object, DwellActivationMessage>(this, (_, _) => cues++);
        using var manager = NewManager(size, messenger);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "a fairly long line of note text here", 600, 500);
            var rest = manager.DrawnCardForTest(id);
            var target = manager.RevealTargetForTest(id)!.Value;
            var centre = P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2));

            long t = Hold(manager, new PhysicalPoint(centre.X + 3000, centre.Y), 0, 200);
            t = Hold(manager, centre, t, 700);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();

            // A slow sweep along the protected region: the bottom padding (above the cells),
            // then up the text, then the target's upward extension. 700 ms at every point.
            var points = new List<PhysicalPoint>
            {
                P(rest.Left + 20, rest.Bottom - 1),
                P(rest.Left + (rest.Width / 2), rest.Bottom - 1),
                P(rest.Right - 20, rest.Bottom - 1),
                P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2)),
                P(target.Left + (target.Width / 2), target.Top + 4),
            };
            foreach (var point in points)
            {
                Check.WithCustomMessage($"{size}: the sweep point must be inside the protected region")
                    .That(rest.Contains(Pd(point)) || target.Contains(Pd(point))).IsTrue();
                t = Hold(manager, point, t, 700);
            }

            Check.WithCustomMessage(
                    $"{size}: a sweep inside the at-rest card/target fired a control "
                    + $"(state {manager.ActiveNotes[0].State}, cues {cues})")
                .That(manager.ActiveNotes[0].State == NoteState.Display && cues == 0).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void F1_ADeliberateLookDownIntoTheEditCell_AfterAnArmedLeave_Edits(double size)
    {
        var messenger = new WeakReferenceMessenger();
        int cues = 0;
        messenger.Register<object, DwellActivationMessage>(this, (_, _) => cues++);
        using var manager = NewManager(size, messenger);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            var rest = manager.DrawnCardForTest(id);
            var centre = P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2));
            long t = Hold(manager, new PhysicalPoint(centre.X + 3000, centre.Y), 0, 200);
            t = Hold(manager, centre, t, 700);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();

            // Out of the card (the empty gap cell) for 200 ms — past the 150 ms arming — then
            // the deliberate 600 ms on ✎.
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 2), t, 200);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 650);
            Check.WithCustomMessage($"{size}: a deliberate look down into ✎ must edit")
                .That(manager.IsEditing).IsTrue();
            Check.That(cues).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F2: the close-cell belt (the distance itself is probe C) ──

    /// <summary>
    /// Fixup-02 F3: this is a CAPTURE pin, not a behavioural one. The geometry keeps ↺ two cells
    /// (336 DIP) from the fired ✕, so the belt's release rule cannot be reached by a real look:
    /// mutants M24 (the filter conjunct dropped) and M26 (the belt releases on the first outside
    /// sample) change nothing a gaze can observe. What this test does pin is the capture itself —
    /// a chip born under a still-held ✕ cell starts DISARMED and the seam reports that state —
    /// which is the part M23 (belt never captured) still turns red for.
    /// </summary>
    [StaFact]
    public void F2_TheUndoBornUnderTheFiredCloseCell_StaysDisarmedUntilTheGazeLeavesIt()
    {
        using var manager = NewManager(36, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 300, 500);
            var close = NoteTestBench.CellCentre(manager, id, 3);
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, close, t, 800);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("a ↺ born while the gaze is in the fired ✕'s cell must stay disarmed")
                .That(manager.ChipUndoArmedForTest(id)).IsFalse();

            // Out of that cell for 200 ms re-arms it.
            t = Hold(manager, new PhysicalPoint(close.X + 6000, close.Y), t, 200);
            Check.WithCustomMessage("the ↺ must re-arm after 150 ms outside the fired ✕'s cell")
                .That(manager.ChipUndoArmedForTest(id)).IsTrue();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void F2_TheChipText_StaysInsideTheChipCard(double size)
    {
        using var manager = NewManager(size, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 300, 500);
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 3), t, 800);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            var window = manager.WindowForTest(id)!;
            var placement = manager.PlacementForTest(id)!;
            var cardLocal = NoteLayout.RectLocal(placement);
            var content = window.ContentBorderForTest!;
            double left = Canvas.GetLeft(content);
            Check.WithCustomMessage(
                    $"{size}: the chip text border {left}+{content.Width} must start at the cell edge "
                    + $"and end {LayoutConstants.NoteButtonPadding} DIP inside the card {cardLocal}")
                .That(left).IsCloseTo(
                    cardLocal.Left + LayoutConstants.NoteButtonPadding + LayoutConstants.NoteCellDip, 1e-6);
            Check.WithCustomMessage($"{size}: the chip text column must keep its 10 DIP right inset")
                .That(left + content.Width).IsLessOrEqualThan(
                    cardLocal.Right - LayoutConstants.NoteButtonPadding + 1e-6);
            Check.That(window.ChipTextForTest).IsNotNull();
            Check.WithCustomMessage("the Undo column supplies the chip text's left gap")
                .That(window.ChipTextForTest!.Margin.Left).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F3: the window contains and paints the reveal target; the own-window gate reads it ──

    private sealed class PaintedPixelsHitTester : INoteHitTester
    {
        public IntPtr Painted { get; set; }
        public IntPtr Foreign { get; set; }
        public Func<PhysicalPoint, bool> IsPainted { get; set; } = _ => false;
        public IntPtr RootWindowAt(PhysicalPoint point) => IsPainted(point) ? Painted : Foreign;
    }

    [StaFact]
    public void F3_TheWindowContainsAndPaintsTheRevealTarget()
    {
        using var manager = NewManager(18, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            var target = manager.RevealTargetForTest(id)!.Value;
            var window = manager.WindowForTest(id);
            Check.That(window).IsNotNull();
            var bounds = new Rect(window!.Left, window.Top, window.Width, window.Height);
            Check.WithCustomMessage($"the window {bounds} must contain the reveal target {target}")
                .That(bounds.Contains(target)).IsTrue();

            var fill = window.RevealFillForTest;
            Check.WithCustomMessage("an at-rest Display note must paint the reveal target").That(fill).IsNotNull();
            Check.That(fill!.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(fill.IsHitTestVisible).IsTrue();
            Check.That(fill.Fill).IsNotNull();
            Check.WithCustomMessage("the fill must be alpha > 0 so WindowFromPoint can report the note")
                .That(fill.Fill is SolidColorBrush brush && brush.Opacity > 0).IsTrue();
            // The fill must contain the registered target in the PLACEMENT's coordinates (the
            // HWND's own bounds snap to device pixels; the drawn fill and the registered target
            // share the DIP placement, so this comparison is exact).
            var placementWindow = manager.PlacementForTest(id)!.Window;
            double fillLeft = placementWindow.Left + Canvas.GetLeft(fill);
            double fillTop = placementWindow.Top + Canvas.GetTop(fill);
            Check.WithCustomMessage(
                    $"the fill {fillLeft},{fillTop},{fill.Width},{fill.Height} must cover the target {target}")
                .That(fillLeft <= target.Left + 1e-6 && fillTop <= target.Top + 1e-6
                    && fillLeft + fill.Width >= target.Right - 1e-6
                    && fillTop + fill.Height >= target.Bottom - 1e-6).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void F3_ARevealFromTheEnlargedBand_NeedsTheNotesOwnWindow()
    {
        var tester = new PaintedPixelsHitTester { Foreign = new IntPtr(0x1234) };
        using var manager = new NoteManager(new WeakReferenceMessenger(),
            hitTester: tester, workAreaDip: NoteTestBench.WorkAreaProvider,
            noteSettings: new NoteSettings { TextSizeDip = 18 });
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hi", 600, 500);
            var window = manager.WindowForTest(id)!;
            tester.Painted = new WindowInteropHelper(window).EnsureHandle();
            var target = manager.RevealTargetForTest(id)!.Value;
            var rest = manager.DrawnCardForTest(id);
            // The band point: inside the revealed target, ABOVE the at-rest card — painted by the
            // fill, but not by the card body.
            var band = P(target.Left + (target.Width / 2), target.Top + 4);
            Check.WithCustomMessage("the band point must be outside the at-rest card")
                .That(Pd(band).Y < rest.Top).IsTrue();
            tester.IsPainted = point =>
            {
                var dip = Pd(point);
                return target.Contains(dip) || rest.Contains(dip);
            };

            // With the note's own window reported on the painted band, the reveal works.
            long t = Hold(manager, new PhysicalPoint(band.X + 3000, band.Y), 0, 200);
            t = Hold(manager, band, t, 600);
            Check.WithCustomMessage("the reveal target's painted band must be live on its own window")
                .That(manager.IsRevealedForTest(id)).IsTrue();

            // A foreign window over the same band blocks it (M15: ignoring reveal handles turns
            // this into a reveal and the test goes red).
            using var guarded = new NoteManager(new WeakReferenceMessenger(),
                hitTester: new PaintedPixelsHitTester
                {
                    Painted = new IntPtr(0x9999),
                    Foreign = new IntPtr(0x5678),
                    IsPainted = _ => false
                },
                workAreaDip: NoteTestBench.WorkAreaProvider,
                noteSettings: new NoteSettings { TextSizeDip = 18 });
            var guardedId = Saved(guarded, "hi", 600, 500);
            var guardedTarget = guarded.RevealTargetForTest(guardedId)!.Value;
            var guardedBand = P(guardedTarget.Left + (guardedTarget.Width / 2), guardedTarget.Top + 4);
            long gt = Hold(guarded, new PhysicalPoint(guardedBand.X + 3000, guardedBand.Y), 0, 200);
            gt = Hold(guarded, guardedBand, gt, 700);
            Check.WithCustomMessage("a foreign window over the painted band must block the reveal")
                .That(guarded.IsRevealedForTest(guardedId)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F6: the hide geometry ──

    [StaFact]
    public void F6_TheHideGeometry_EdgePlus20Keeps_Plus30Hides_TheRowBandKeeps()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // +20 DIP of the revealed card: inside the 24 DIP margin, so a 3 s hold keeps it.
            using (var manager = NewManager(18, new WeakReferenceMessenger()))
            {
                var id = Saved(manager, "hi", 600, 500);
                long t = NoteTestBench.Reveal(manager, id, 0);
                var card = manager.PlacementForTest(id)!.Card;
                var edgePlus20 = P(card.Right + 20, card.Top + (card.Height / 2));
                t = Hold(manager, edgePlus20, t, 3_000);
                Check.WithCustomMessage("the revealed edge + 20 DIP must stay inside the 24 DIP hide margin")
                    .That(manager.IsRevealedForTest(id)).IsTrue();
            }

            // +30 DIP: outside the margin, so it hides after 2 s.
            using (var manager = NewManager(18, new WeakReferenceMessenger()))
            {
                var id = Saved(manager, "hi", 600, 500);
                long t = NoteTestBench.Reveal(manager, id, 0);
                var card = manager.PlacementForTest(id)!.Card;
                var edgePlus30 = P(card.Right + 30, card.Top + (card.Height / 2));
                t = Hold(manager, edgePlus30, t, 2_500);
                Check.WithCustomMessage("the revealed edge + 30 DIP must hide after 2 s")
                    .That(manager.IsRevealedForTest(id)).IsFalse();
            }

            // The row band (below the at-rest card) belongs to the revealed card: it keeps it.
            using (var manager = NewManager(18, new WeakReferenceMessenger()))
            {
                var id = Saved(manager, "hi", 600, 500);
                long t = NoteTestBench.Reveal(manager, id, 0);
                var gap = NoteTestBench.CellCentre(manager, id, 2);
                t = Hold(manager, gap, t, 3_000);
                Check.WithCustomMessage("a gaze on the row band must keep the row")
                    .That(manager.IsRevealedForTest(id)).IsTrue();
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F7: overlapping AT-REST notes: the top note's reveal target is live ──

    [StaFact]
    public void F7_OfTwoOverlappingAtRestNotes_TheTopRevealTargetIsLive()
    {
        var messenger = new WeakReferenceMessenger();
        var tester = new PaintedPixelsHitTester { Foreign = new IntPtr(0x2222) };
        using var manager = new NoteManager(messenger, hitTester: tester,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var bottomId = Saved(manager, "bottom", 800, 500);
            var topId = Saved(manager, "top", 820, 500);
            var topWindow = manager.WindowForTest(topId)!;
            tester.Painted = new WindowInteropHelper(topWindow).EnsureHandle();
            var bottomTarget = manager.RevealTargetForTest(bottomId)!.Value;
            var topTarget = manager.RevealTargetForTest(topId)!.Value;
            var bottomRest = manager.DrawnCardForTest(bottomId);
            // The gaze sits on the BOTTOM target's centre — inside both targets, nearest to the
            // bottom — while the top window owns it.
            var gaze = P(bottomTarget.Left + (bottomTarget.Width / 2),
                bottomTarget.Top + (bottomTarget.Height / 2));
            Check.WithCustomMessage("the gaze must be inside both reveal targets")
                .That(bottomTarget.Contains(Pd(gaze)) && topTarget.Contains(Pd(gaze))).IsTrue();
            Check.WithCustomMessage("the bottom target must be the nearer one")
                .That(Distance(bottomTarget, Pd(gaze)) < Distance(topTarget, Pd(gaze))).IsTrue();
            tester.IsPainted = point => topTarget.Contains(Pd(point)) || bottomRest.Contains(Pd(point));

            long t = Hold(manager, new PhysicalPoint(gaze.X + 3000, gaze.Y), 0, 200);
            t = Hold(manager, gaze, t, 700);
            Check.WithCustomMessage("the top note's reveal target must take the dwell")
                .That(manager.IsRevealedForTest(topId)).IsTrue();
            Check.WithCustomMessage("the bottom note's reveal target must stay filtered")
                .That(manager.IsRevealedForTest(bottomId)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── F8: gate recovery with the gaze OFF the reveal target ──

    /// <summary>
    /// Fixup-02 F3's F8 rebuild. The old pin held the gaze ON the card across the pause; the
    /// recovery's latch then covered the same spot, so it could not tell whether the reveal
    /// detector was reset. This one recovers with the gaze OFF the target, where the clock's
    /// external effect is observable: the pre-pause progress is gone, and the target needs a
    /// fresh 500 ms look.
    /// <para>
    /// HONEST LIMIT (measured): removing the reset (mutant M32) still leaves this green. The
    /// recovery sample itself is outside the target, and <c>DwellDetector.Update</c>
    /// clears any target's clock when the gaze is not on it, so the reset has no externally
    /// distinguishable effect on this path. The reset stays for consistency with the card and
    /// chip detectors (fixup-01 F8); it is NOT claimed to be behaviourally pinned by this test.
    /// </para>
    /// </summary>
    [StaFact]
    public void F8_GateRecoveryOffTheTarget_LeavesNoRevealProgress()
    {
        using var manager = NewManager(36, new WeakReferenceMessenger());
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "pause me", 600, 500);
            var rest = manager.DrawnCardForTest(id);
            var c = P(rest.Left + (rest.Width / 2), rest.Top + (rest.Height / 2));
            long t = Hold(manager, new PhysicalPoint(3000, 3000), 0, 200);
            t = Hold(manager, c, t, 400);
            Check.WithCustomMessage("the reveal dwell must have accumulated before the pause")
                .That(manager.RevealProgressForTest(id)).IsStrictlyGreaterThan(0.5);

            for (int i = 0; i < 3; i++, t += 33_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(c, t, IsInGap: false))
                { IsPaused = true });
            // Recovery sample OFF the target: no latch, and the pre-pause 400 ms must be gone.
            Sample(manager, new PhysicalPoint(3000, 3000), t);
            Check.WithCustomMessage("the gate recovery must not carry the pre-pause reveal progress")
                .That(manager.RevealProgressForTest(id)).IsEqualTo(0);
            Check.WithCustomMessage("no reveal may complete on the first clear sample after a pause")
                .That(manager.IsRevealedForTest(id)).IsFalse();

            // A fresh 400 ms look must still fall short of the 500 ms reveal: the old progress
            // does not resume.
            t = Hold(manager, c, t + 33_000, 400);
            Check.WithCustomMessage("a fresh 400 ms look must not complete the reveal")
                .That(manager.IsRevealedForTest(id)).IsFalse();
            t = Hold(manager, c, t, 200);
            Check.WithCustomMessage("the fresh 500 ms look then reveals").That(manager.IsRevealedForTest(id)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static Point Pd(PhysicalPoint p) => new(
        DpiHelper.PhysicalToLogicalX(p.X), DpiHelper.PhysicalToLogicalY(p.Y));

    private static double Distance(Rect rect, Point p) => Math.Sqrt(
        Math.Pow(p.X - (rect.Left + (rect.Width / 2)), 2)
        + Math.Pow(p.Y - (rect.Top + (rect.Height / 2)), 2));
}
