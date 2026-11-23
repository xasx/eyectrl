namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// D (sitting 11) — the text-only card that reveals its control row on a look. The suite pins
/// the whole contract: the 500 ms reveal dwell (no reveal before it), the revealed row with the
/// existing 600 ms controls, the "a reveal is not an action / a look held on the at-rest card
/// fires nothing" property, the 112x112 upward/sideways target that never grows into the row
/// band, the 2000 ms hide with gated/gap samples as no information, the identical text origin in
/// both states, the reserved-size placement (and the owner-keyboard sweep), the chrome and
/// inventory updates, the one-revealed-at-a-time rule, the D6 transition ends, the Move card's
/// option-C growth (full hint, work-area shift) and the content-free NOTE-REVEAL lines.
/// <para>
/// Headless: every window is constructed and never shown (the App test assembly's note show
/// seam is off); no test paints a visible window.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteRevealTests
{
    private const int StepUs = 50_000;

    private sealed class FakeClock
    {
        public long NowMs;
        public long Read() => NowMs;
    }

    private static NoteManager NewManager(FakeClock? clock = null,
        List<string>? lines = null, double textSize = NoteSettings.DefaultTextSizeDip,
        WeakReferenceMessenger? messenger = null)
    {
        messenger ??= new WeakReferenceMessenger();
        return new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock is null ? null : clock.Read,
            workAreaDip: NoteTestBench.WorkAreaProvider,
            noteSettings: new NoteSettings { TextSizeDip = (int)textSize },
            settleInfoLog: lines is null ? null : lines.Add);
    }

    private static void Sample(NoteManager manager, PhysicalPoint point, long t) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, t, IsInGap: false)));

    private static long Hold(NoteManager manager, PhysicalPoint point, long start, long ms,
        int stepUs = StepUs)
    {
        long end = start + (ms * 1000);
        for (long t = start; t <= end; t += stepUs)
            Sample(manager, point, t);
        return end + stepUs;
    }

    private static long Away(NoteManager manager, PhysicalPoint point, long start, long ms) =>
        Hold(manager, new PhysicalPoint(point.X + 10_000, point.Y), start, ms);

    private static string Saved(NoteManager manager, string text, double dipX, double dipY)
    {
        manager.CreateNoteAt(new PhysicalPoint(dipX * 2, dipY * 2));
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static PhysicalPoint Centre(Rect dipRect) => new(
        DpiHelper.LogicalToPhysicalX(dipRect.Left + (dipRect.Width / 2)),
        DpiHelper.LogicalToPhysicalY(dipRect.Top + (dipRect.Height / 2)));

    private static PhysicalPoint Dip(PhysicalPoint physical) => new(
        DpiHelper.PhysicalToLogicalX(physical.X), DpiHelper.PhysicalToLogicalY(physical.Y));

    private static PhysicalPoint Phys(double dipX, double dipY) => new(dipX * 2, dipY * 2);

    private static void Arrange(NoteWindow window)
    {
        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
    }

    private static Point TextOrigin(NoteWindow window)
    {
        var canvas = (Canvas)window.Content;
        var text = window.BodyTextForTest
            ?? throw new InvalidOperationException("the window must draw its body text");
        return text.TransformToAncestor(canvas).Transform(new Point(0, 0));
    }

    private static Rect DipRect(PhysicalRect physical) => new(
        DpiHelper.PhysicalToLogicalX(physical.X),
        DpiHelper.PhysicalToLogicalY(physical.Y),
        DpiHelper.PhysicalToLogicalX(physical.X + physical.Width) - DpiHelper.PhysicalToLogicalX(physical.X),
        DpiHelper.PhysicalToLogicalY(physical.Y + physical.Height) - DpiHelper.PhysicalToLogicalY(physical.Y));

    private static bool SameRect(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) < 1e-6 && Math.Abs(a.Top - b.Top) < 1e-6
        && Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;

    // ── 1. the reveal dwell: 400 ms does nothing, 500 ms reveals, a glance leaves it at rest ──

    [StaFact]
    public void A400msLook_LeavesTheNoteAtRest_AndThe500msLookReveals()
    {
        using var manager = NewManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "look at me", 800, 500);
            var centre = Centre(manager.DrawnCardForTest(id));

            long t = 0;
            t = Away(manager, centre, t, 200);
            // A 400 ms look: three quarters of the 500 ms dwell.
            t = Hold(manager, centre, t, 400);
            Check.WithCustomMessage("a 400 ms look must not reveal the row")
                .That(manager.IsRevealedForTest(id)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(1); // still only the reveal target

            // Leave (the detector restart) and look for the full 500 ms.
            t = Away(manager, centre, t, 300);
            t = Hold(manager, centre, t, 500);
            Check.WithCustomMessage("the 500 ms look reveals the row")
                .That(manager.IsRevealedForTest(id)).IsTrue();
            Check.WithCustomMessage("the revealed row is the three Display cells")
                .That(manager.LiveTargetRects()).HasSize(3);

            // A glance shorter than 500 ms after a hide leaves it at rest again.
            t = Away(manager, centre, t, 2_300);
            Check.That(manager.IsRevealedForTest(id)).IsFalse(); // the away hid it
            t = Hold(manager, centre, t, 300);
            Check.WithCustomMessage("a glance shorter than the dwell leaves the note at rest")
                .That(manager.IsRevealedForTest(id)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 2. the reveal look fires nothing; the deliberate move into ✎ then edits ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void TheRevealLook_AtTheBottomEdgeAndTheCentre_FiresNothing_ThenAnEditLookEdits(double size)
    {
        var lines = new List<string>();
        var messenger = new WeakReferenceMessenger();
        int cues = 0;
        messenger.Register<object, DwellActivationMessage>(this, (_, _) => cues++);
        using var manager = NewManager(lines: lines, textSize: size, messenger: messenger);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "one line", 800, 500);

            var rest = manager.DrawnCardForTest(id);
            var centre = Centre(rest);
            var bottomEdge = new PhysicalPoint(
                centre.X, DpiHelper.LogicalToPhysicalY(rest.Bottom) - 0.5);

            // 3 s at the at-rest card's bottom edge: the reveal fires at 500 ms; the held gaze
            // is latched by the row registration, so no control fires for the rest of the hold.
            long t = 0;
            t = Away(manager, centre, t, 200);
            t = Hold(manager, bottomEdge, t, 3_000);
            Check.WithCustomMessage($"at {size} DIP the bottom-edge look must reveal")
                .That(manager.IsRevealedForTest(id)).IsTrue();
            Check.WithCustomMessage($"at {size} DIP the bottom-edge look must fire no control")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // 3 s in the centre of the revealed card: still no action.
            t = Hold(manager, centre, t, 3_000);
            Check.WithCustomMessage($"at {size} DIP the centre look must fire no control")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage($"at {size} DIP no reveal or hold may send a dwell cue")
                .That(cues).IsEqualTo(0);

            // The deliberate move into ✎'s row cell + the 600 ms dwell enters Edit (the cue is
            // the action's, exactly one).
            var editCell = NoteTestBench.CellCentre(manager, id, 0);
            t = Hold(manager, editCell, t, 800);
            Check.WithCustomMessage($"at {size} DIP the deliberate ✎ look must enter Edit")
                .That(manager.IsEditing).IsTrue();
            Check.That(cues).IsEqualTo(1);
            Check.WithCustomMessage("a reveal is not an action: the shown line is not a cue")
                .That(lines.Count(l => l.StartsWith("NOTE-REVEAL out=shown", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 3. the reveal target's geometry: at least one cell, upward/sideways only, no row band ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void TheRevealTarget_IsAtLeastOneCell_AndNeverGrowsDownward(double size)
    {
        using var manager = NewManager(textSize: size);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "short", 800, 500);
            var rest = manager.DrawnCardForTest(id);
            var reserved = manager.PlacementForTest(id)!.Card;
            var target = manager.RevealTargetForTest(id);
            Check.That(target).IsNotNull();
            var reveal = target!.Value;

            Check.WithCustomMessage($"{size}: the reveal target is at least one hit cell per axis")
                .That(reveal.Width >= LayoutConstants.NoteCellDip
                    && reveal.Height >= LayoutConstants.NoteCellDip).IsTrue();
            Check.WithCustomMessage($"{size}: the target's bottom edge is the at-rest card's bottom")
                .That(reveal.Bottom).IsCloseTo(rest.Bottom, 1e-9);
            Check.WithCustomMessage($"{size}: the target grows upward only")
                .That(reveal.Top <= rest.Top).IsTrue();
            Check.WithCustomMessage($"{size}: it grows to BOTH sides symmetrically")
                .That(Math.Abs((rest.Left - reveal.Left) - (reveal.Right - rest.Right))).IsLessOrEqualThan(1e-9);

            // The row band is the reserved strip BELOW the at-rest card: one hit cell plus the
            // card's bottom padding (fixup-01 F1 — the cells start AT rest.Bottom, so none of
            // their area overlaps the visible at-rest card). The target must not reach into it:
            // only the shared edge.
            var rowBand = new Rect(reserved.Left, rest.Bottom, reserved.Width, reserved.Bottom - rest.Bottom);
            Check.WithCustomMessage($"{size}: the reveal target must not reach into the row band")
                .That(reveal.Bottom <= rowBand.Top + 1e-9).IsTrue();
            Check.That(rowBand.Height)
                .IsCloseTo(LayoutConstants.NoteCellDip + LayoutConstants.NoteButtonPadding, 1e-9);
            Check.WithCustomMessage("the reserved card is the at-rest card's top part plus the row and its pad")
                .That(reserved.Bottom - rest.Bottom)
                .IsCloseTo(LayoutConstants.NoteCellDip + LayoutConstants.NoteButtonPadding, 1e-9);
            // The first row cell's top edge is exactly the at-rest card's bottom edge.
            var cell0 = NoteTestBench.CellCentre(manager, id, 0);
            Check.WithCustomMessage($"{size}: the row's first hit cell must start at rest.Bottom")
                .That(DpiHelper.PhysicalToLogicalY(cell0.Y) - (LayoutConstants.NoteCellDip / 2))
                .IsCloseTo(rest.Bottom, 1e-6);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 4. the hide: 2000 ms outside, a return resets, a gaze loss / gated sample never counts ──

    [StaFact]
    public void TheHide_Is2000msOutside_AndNoInformationSamplesNeitherAdvanceNorReset()
    {
        var clock = new FakeClock();
        using var manager = NewManager(clock);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hide me", 800, 500);
            var rest = manager.DrawnCardForTest(id);
            var centre = Centre(rest);
            var revealedCard = manager.PlacementForTest(id)!.Card; // D5's hide rect
            var outside = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(revealedCard.Right + NoteLayout.RevealHideMarginDip + 40),
                centre.Y);

            long t = NoteTestBench.Reveal(manager, id, 0);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            t = Hold(manager, centre, t, 100); // reset the reveal helper's latch-release excursion
            t = Away(manager, centre, t, 1_900);
            Check.WithCustomMessage("1.9 s outside must keep the row")
                .That(manager.IsRevealedForTest(id)).IsTrue();

            // A return within 2 s resets the clock.
            t = Hold(manager, centre, t, 100);
            t = Hold(manager, outside, t, 1_900);
            Check.WithCustomMessage("a return within the 2 s resets the outside clock")
                .That(manager.IsRevealedForTest(id)).IsTrue();
            t = Hold(manager, outside, t, 200);
            Check.WithCustomMessage(
                    $"2 s of continuous outside hides the row (outside clock {manager.RevealOutsideMsForTest:F0} ms)")
                .That(manager.IsRevealedForTest(id)).IsFalse();

            // A 3 s gaze loss (gap-only samples) is no information: it does not hide.
            t = NoteTestBench.Reveal(manager, id, t);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            for (long g = t; g <= t + 3_000_000; g += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(outside, g, IsInGap: true)));
            Sample(manager, outside, t + 3_100_000);
            Check.WithCustomMessage("a 3 s gaze loss must not hide the row")
                .That(manager.IsRevealedForTest(id)).IsTrue();

            // A GATED sample is no information: 3 s under an occluder does not advance the clock.
            manager.SetOccluderSources(noteNoticeBounds: () =>
                new PhysicalRect(outside.X - 200, outside.Y - 200, 400, 400));
            t = Hold(manager, outside, t + 3_200_000, 3_000);
            Check.WithCustomMessage("gated samples must not advance the hide clock")
                .That(manager.IsRevealedForTest(id)).IsTrue();
            manager.SetOccluderSources(noteNoticeBounds: () => null);
            t = Hold(manager, outside, t, 900);
            Check.WithCustomMessage("after the gate opens the clock starts from zero, not from the gated time")
                .That(manager.IsRevealedForTest(id)).IsTrue();
            t = Hold(manager, outside, t, 1_200);
            Check.WithCustomMessage("the real 2 s outside then hides it")
                .That(manager.IsRevealedForTest(id)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 5. the text origin is identical at rest and revealed, at every size ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void TheTextOrigin_IsIdenticalAtRestAndRevealed(double size)
    {
        using var manager = NewManager(textSize: size);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "keep the text still", 800, 500);
            var window = manager.WindowForTest(id)!;
            Arrange(window);
            var atRest = TextOrigin(window);
            var restCard = manager.DrawnCardForTest(id);
            var reserved = manager.PlacementForTest(id)!.Card;

            window.SetRevealed(true);
            Arrange(window);
            var revealed = TextOrigin(window);

            Check.WithCustomMessage($"{size}: the text origin is identical in both states")
                .That(revealed.X).IsCloseTo(atRest.X, 1e-9);
            Check.That(revealed.Y).IsCloseTo(atRest.Y, 1e-9);
            Check.WithCustomMessage($"{size}: the at-rest card and the reserved card share the text corner")
                .That((restCard.Left, restCard.Top)).IsEqualTo((reserved.Left, reserved.Top));
            Check.WithCustomMessage($"{size}: the text sits at the card's top-left plus its margin")
                .That((atRest.X, atRest.Y)).IsEqualTo(
                    (NoteLayout.RectLocal(manager.PlacementForTest(id)!).Left + 10,
                     NoteLayout.RectLocal(manager.PlacementForTest(id)!).Top + 10));
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 6. placement uses the reserved size; the owner-keyboard sweep holds for both rects ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void ThePlacement_IsTheReservedSize_AndTheAtRestRectIsItsTopPart(double size)
    {
        using var manager = NewManager(textSize: size);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "a short note", 800, 500);
            var window = manager.WindowForTest(id)!;
            double cap = NoteLayout.TextCapHeightDip(NoteTestBench.WorkArea.Height, size);
            double textHeight = Math.Min(window.MeasureTextHeightDip("a short note"), cap);
            var expectedReserved = NoteLayout.DisplayReservedSize(
                window.MeasureTextWidthDip("a short note"), textHeight);

            var placement = manager.PlacementForTest(id)!;
            Check.WithCustomMessage($"{size}: every placement reads the RESERVED size")
                .That(placement.Card.Size).IsEqualTo(expectedReserved);
            var rest = manager.DrawnCardForTest(id);
            Check.WithCustomMessage($"{size}: the drawn at-rest card is the reserved rect's top part")
                .That((rest.Left, rest.Top, rest.Width, rest.Height)).IsEqualTo(
                    (placement.Card.Left, placement.Card.Top,
                     NoteLayout.DisplayRestWidthDip(window.MeasureTextWidthDip("a short note")),
                     NoteLayout.DisplayRestHeightDip(textHeight)));

            // The placement is exactly the pure Place over the reserved size.
            var raw = NoteLayout.Place(new PhysicalPoint(1600, 1000), NoteSide.Right,
                expectedReserved, NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!;
            var expected = NoteLayout.WithRevealTargetClearance(
                NoteLayout.WithTagClearance(raw, size),
                NoteLayout.DisplayRestSize(
                    window.MeasureTextWidthDip("a short note"), textHeight),
                DpiHelper.ScaleX);
            Check.WithCustomMessage($"{size}: the manager's placement is the reserved-size derivation "
                    + "plus the tag and reveal-target strips")
                .That(placement.Window).IsEqualTo(expected.Window);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void TheOwnerKeyboardSweep_HoldsForTheReservedAndTheAtRestRect(double size)
    {
        // The owner's real keyboard at F17's 1.75 key size — MEASURED from the real window:
        // 1642 x 736 DIP on the letters page, 1641.2 x 862.5 on Fn (the sweep uses 1642 for
        // both, 0.8 DIP conservative) — the 140 DIP toolbar's yield and the plain top/bottom
        // docks, over the anchor grid the old sweep used.
        // Display NEVER reads the keyboard: the avoid list is passed here only to prove that the
        // placement stays the pre-ladder derivation inside the 80 DIP inset (B's yielding margin
        // and gap are the Edit state's alone), and the at-rest rect is its top part.
        const double lettersHeight = 736.0;
        const double fnHeight = 862.5;
        var work = new Rect(0, 0, 1920, 1152);
        Rect[] keyboards =
        [
            new(139, 0, 1642, lettersHeight),
            new(139, 1152 - lettersHeight, 1642, lettersHeight),
            new(139, 140, 1642, lettersHeight),
            new(139, 0, 1642, fnHeight),
            new(139, 1152 - fnHeight, 1642, fnHeight),
            new(139, 140, 1642, fnHeight),
        ];
        double[] anchorsX = [100, 300, 960, 1600, 1820];
        double[] anchorsY = [100, 150, 300, 520, 746, 1000];
        var sides = new[] { NoteSide.Right, NoteSide.Left, NoteSide.Below, NoteSide.Above };
        var reserved = NoteLayout.DisplayReservedSize(0, NoteLayout.TextLineHeightDip(size));
        var rest = NoteLayout.DisplayRestSize(0, NoteLayout.TextLineHeightDip(size));
        double margin = NoteLayout.Constants.EdgeMarginDip;
        var inset = new Rect(margin, margin, work.Width - (2 * margin), work.Height - (2 * margin));

        foreach (var keyboard in keyboards)
        foreach (var x in anchorsX)
        foreach (var y in anchorsY)
        foreach (var side in sides)
        {
            var placement = NoteLayout.Place(
                new PhysicalPoint(x * 2, y * 2), side, reserved, work, new[] { keyboard }, 2.0)!;
            var drawn = NoteLayout.WithTagClearance(placement, size);
            var restCard = NoteLayout.RestCardRect(drawn, rest);
            string label = $"{size} @ ({x},{y}) {side} kb {keyboard.Top}/{keyboard.Height}";

            Check.WithCustomMessage($"{label}: the reserved placement must stay inside the inset")
                .That(placement.Window.Left >= inset.Left && placement.Window.Top >= inset.Top
                    && placement.Window.Right <= inset.Right && placement.Window.Bottom <= inset.Bottom).IsTrue();
            Check.WithCustomMessage($"{label}: the drawn card (tag strip excluded) must stay inside the inset")
                .That(drawn.Card.Left >= inset.Left && drawn.Card.Top >= inset.Top
                    && drawn.Card.Right <= inset.Right && drawn.Card.Bottom <= inset.Bottom).IsTrue();
            Check.WithCustomMessage($"{label}: the at-rest rect is inside the reserved card")
                .That(drawn.Card.Contains(restCard)).IsTrue();
            // Whatever clearance the placed reserved card keeps, the at-rest rect keeps too:
            // it is a subset of the same card (the tag strip is window chrome above both).
            Check.WithCustomMessage($"{label}: the at-rest rect cannot be less clear than the reserved card")
                .That(GapFrom(restCard, keyboard)).IsGreaterOrEqualThan(GapFrom(drawn.Card, keyboard) - 1e-6);
        }

        // F17 shrank the yielded letters band to 1152 - 160 (A2's inset) - 140 (toolbar) - 736
        // = 196 DIP. The AT-REST Display card plus tag still fits it with the full 24 DIP gap at
        // every size (a shown note is short); the RESERVED (revealed) footprint does not at 36
        // and 48 - the reveal is a deliberate look at a note the owner put there, and Display
        // never consumes the keyboard rects, so this is a recorded outcome, not a fit rule.
        double freeBand = inset.Bottom - (140 + lettersHeight);
        Check.WithCustomMessage($"F17: the yielded letters keyboard (736) leaves a 196 DIP band")
            .That(freeBand).IsCloseTo(196, 1e-6);
        double restDrawn = rest.Height + NoteLayout.TagProtrusionDip(size);
        Check.WithCustomMessage(
                $"{size}: the AT-REST Display card + tag + the 24 gap must fit the {freeBand:F1} DIP band")
            .That(restDrawn + NoteLayout.Constants.AvoidGapDip).IsLessOrEqualThan(freeBand);

        double reservedDrawn = reserved.Height + NoteLayout.TagProtrusionDip(size);
        bool reservedFits = reservedDrawn <= freeBand;
        Check.WithCustomMessage(
                $"{size}: the RESERVED Display card + tag ({reservedDrawn:F1} DIP) vs the {freeBand:F1} DIP band")
            .That(reservedFits).IsEqualTo(size < 36);
    }

    private static double GapFrom(Rect a, Rect b)
    {
        double dx = Math.Max(b.Left - a.Right, 0) + Math.Max(a.Left - b.Right, 0);
        double dy = Math.Max(b.Top - a.Bottom, 0) + Math.Max(a.Top - b.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // ── 7. chrome and inventory follow the reveal; at most one note is revealed ──

    [StaFact]
    public void ChromeAndTargetsFollowTheReveal_AndOnlyOneNoteIsRevealed()
    {
        var lines = new List<string>();
        var clock = new FakeClock();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, workAreaDip: NoteTestBench.WorkAreaProvider, settleInfoLog: lines.Add);
        int raised = 0;
        messenger.Register(new NoteManagerTests.NoteTargetsProbe(() => raised++));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = Saved(manager, "alpha", 700, 500);
            var b = Saved(manager, "beta", 1100, 500);
            var restA = manager.DrawnCardForTest(a);
            var targetA = manager.RevealTargetForTest(a)!.Value;

            Check.That(manager.LiveTargetRects()).HasSize(2); // two reveal targets
            Check.WithCustomMessage("at rest the chrome carries the at-rest card and the reveal target")
                .That(manager.ChromeRects().Select(DipRect).Any(r => SameRect(r, restA))).IsTrue();
            Check.That(manager.ChromeRects().Select(DipRect).Any(r => SameRect(r, targetA))).IsTrue();
            Check.WithCustomMessage("the reveal target is in the gaze-consumer inventory")
                .That(manager.LiveTargetRects().Select(DipRect).Any(r => SameRect(r, targetA))).IsTrue();

            int beforeReveal = raised;
            long t = NoteTestBench.Reveal(manager, a, 0);
            Check.That(manager.IsRevealedForTest(a)).IsTrue();
            Check.That(manager.IsRevealedForTest(b)).IsFalse();
            Check.WithCustomMessage("a reveal fires RaiseTargetsChanged")
                .That(raised).IsStrictlyGreaterThan(beforeReveal);
            Check.WithCustomMessage("one row (3 cells) + the other note's reveal target")
                .That(manager.LiveTargetRects()).HasSize(4);
            var revealedA = manager.PlacementForTest(a)!.Card;
            Check.WithCustomMessage("the chrome carries the revealed (reserved) card")
                .That(manager.ChromeRects().Select(DipRect).Any(r => SameRect(r, revealedA))).IsTrue();
            Check.WithCustomMessage("the chrome no longer carries A's reveal target")
                .That(manager.ChromeRects().Select(DipRect).Any(r => SameRect(r, targetA))).IsFalse();

            // Revealing B hides A at once (why=other).
            int beforeOther = raised;
            t = NoteTestBench.Reveal(manager, b, t);
            Check.That(manager.IsRevealedForTest(b)).IsTrue();
            Check.That(manager.IsRevealedForTest(a)).IsFalse();
            Check.WithCustomMessage("revealing another note hides the first")
                .That(lines.Any(l => l.StartsWith("NOTE-REVEAL out=hidden", StringComparison.Ordinal)
                    && l.EndsWith(" why=other", StringComparison.Ordinal))).IsTrue();
            Check.That(raised).IsStrictlyGreaterThan(beforeOther);

            // Hide B by looking away: both are at rest again, targets = two reveal targets.
            int beforeHide = raised;
            t = Away(manager, Centre(manager.DrawnCardForTest(b)), t, 2_300);
            Check.That(manager.IsRevealedForTest(b)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(2);
            Check.WithCustomMessage("a hide fires RaiseTargetsChanged")
                .That(raised).IsStrictlyGreaterThan(beforeHide);
            Check.WithCustomMessage("the hide line names why=away")
                .That(lines.Any(l => l.StartsWith("NOTE-REVEAL out=hidden", StringComparison.Ordinal)
                    && l.EndsWith(" why=away", StringComparison.Ordinal))).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 8. D6's transitions end at rest; the Move card's option-C growth; the outline reserved ──

    [StaFact]
    public void EditAndUndo_EndAtRest_AndTheMoveCardGrowsButTheOutlineStaysReserved()
    {
        var lines = new List<string>();
        var clock = new FakeClock();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, workAreaDip: NoteTestBench.WorkAreaProvider, settleInfoLog: lines.Add);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "hello", 800, 500);
            var reserved = manager.PlacementForTest(id)!.Card;
            long t = NoteTestBench.Reveal(manager, id, 0);

            // ✎ → Edit (the compact Edit card), then Done → at rest.
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 800);
            Check.That(manager.IsEditing).IsTrue();
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.WithCustomMessage("entering Edit hides the reveal with why=edit")
                .That(lines.Any(l => l.EndsWith(" why=edit", StringComparison.Ordinal))).IsTrue();
            Check.That(manager.PlacementForTest(id)!.Card.Size)
                .IsEqualTo(NoteLayout.EditCardSize(NoteSettings.DefaultTextSizeDip, 0, NoteTestBench.WorkArea.Height));
            manager.FinishEdit(id, commit: true);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(1); // at rest

            // ↕ → Move: the drawn card grows by the hint; the row follows; the outline stays the
            // reserved rect.
            t = NoteTestBench.Reveal(manager, id, t);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsTrue();
            var moving = manager.PlacementForTest(id)!;
            var window = manager.WindowForTest(id)!;
            double hint = window.MoveHintBlockHeightDip(reserved.Width);
            Check.WithCustomMessage("the Move card is the reserved card plus the hint block")
                .That(moving.Card.Height).IsCloseTo(reserved.Height + hint, 1e-6);
            Check.WithCustomMessage("the Move card keeps the text corner")
                .That((moving.Card.Left, moving.Card.Top)).IsEqualTo((reserved.Left, reserved.Top));
            Check.That(manager.LiveTargetRects()).HasSize(2);
            Check.That(window.MoveHintForTest).IsNotNull();

            // A settle shows the landing outline: its SIZE is the RESERVED card (what will land),
            // not the grown drawn card.
            t = Hold(manager, Phys(400, 900), t, 1_200);
            Check.That(manager.MovePreviewShownForTest).IsTrue();
            var outline = manager.MovePreviewWindowForTest!.CardRectDip;
            Check.WithCustomMessage("the move outline is the reserved card's size")
                .That(outline.Size).IsEqualTo(reserved.Size);
            Check.WithCustomMessage("the drawn Move card is taller than the outline")
                .That(moving.Card.Height).IsStrictlyGreaterThan(outline.Height);

            // Cancel → Display at rest.
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(1);
            Check.WithCustomMessage("Cancel puts the note back on the reserved placement")
                .That(manager.PlacementForTest(id)!.Card).IsEqualTo(reserved);

            // ↕ → settle → place → at rest at the new anchor.
            t = NoteTestBench.Reveal(manager, id, t);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsTrue();
            t = Hold(manager, Phys(400, 900), t, 1_200);
            var anchor = manager.PlaceCentroidForTest!.Value;
            var disc = manager.PlaceWindowForTest!;
            var disccentre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(manager.PlaceWindowRectForTest.X + disc.DiscCentreDip.X),
                DpiHelper.LogicalToPhysicalY(manager.PlaceWindowRectForTest.Y + disc.DiscCentreDip.Y));
            t = Hold(manager, disccentre, t, 800);
            Check.That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].Anchor).IsEqualTo(anchor);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(1);

            // ✕ → the chip anchored at the reserved text corner; Undo → Display at rest.
            var beforeChip = manager.PlacementForTest(id)!.Card;
            t = NoteTestBench.Reveal(manager, id, t);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 3), t, 800);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("the ✕ hides the reveal with why=close")
                .That(lines.Any(l => l.EndsWith(" why=close", StringComparison.Ordinal))).IsTrue();
            var chip = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the chip anchors at the reserved text corner")
                .That((chip.Card.Left, chip.Card.Top)).IsEqualTo((beforeChip.Left, beforeChip.Top));
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 0), t, 800); // Undo
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.That(manager.LiveTargetRects()).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheGrownMoveCard_ShiftsUpAtTheWorkAreaEdge_AndItsRowFollows()
    {
        var lines = new List<string>();
        var clock = new FakeClock();
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, workAreaDip: NoteTestBench.WorkAreaProvider, settleInfoLog: lines.Add);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // Near the bottom edge the reserved card is placed just inside the inset; growing
            // it by the hint would leave the work area, so the DRAWN card shifts up. This is the
            // one case where the text moves during a Move.
            var id = Saved(manager, "edge", 960, 1100);
            var reserved = manager.PlacementForTest(id)!.Card;
            long t = NoteTestBench.Reveal(manager, id, 0);
            t = Hold(manager, NoteTestBench.CellCentre(manager, id, 1), t, 800);
            Check.That(manager.IsMoving).IsTrue();

            var moving = manager.PlacementForTest(id)!.Card;
            var window = manager.WindowForTest(id)!;
            double hint = window.MoveHintBlockHeightDip(reserved.Width);
            double margin = NoteLayout.Constants.EdgeMarginDip;
            double insetBottom = NoteTestBench.WorkArea.Height - margin;

            Check.WithCustomMessage("the grown card must stay inside the inset work area")
                .That(moving.Bottom).IsLessOrEqualThan(insetBottom + 1e-6);
            Check.WithCustomMessage("it was shifted UP (the text moves only in this edge case)")
                .That(moving.Top).IsStrictlyLessThan(reserved.Top);
            Check.WithCustomMessage("it still grew by the hint block")
                .That(moving.Height).IsCloseTo(reserved.Height + hint, 1e-6);
            Check.WithCustomMessage("the row's ⇄/⊘ targets sit on the drawn card's row")
                .That(NoteTestBench.CellCentre(manager, id, 0).Y)
                .IsCloseTo(DpiHelper.LogicalToPhysicalY(moving.Bottom - 10 - 56), 1e-6);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 10. the tail stays attached to the drawn card (the coordinator's D follow-up) ──

    [StaTheory]
    [InlineData(18.0)]
    [InlineData(28.0)]
    [InlineData(36.0)]
    [InlineData(48.0)]
    public void TheTail_StaysAttachedToTheDrawnCard_AtRestAndRevealed(double size)
    {
        using var manager = NewManager(textSize: size);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "tail", 800, 500);
            var window = manager.WindowForTest(id)!;
            var placement = manager.PlacementForTest(id)!;
            var restCard = manager.DrawnCardForTest(id);

            void AssertTail(Rect drawnCard, string state)
            {
                Arrange(window);
                var tail = window.TailForTest;
                Check.WithCustomMessage($"{size}/{state}: the note must draw its tail")
                    .That(tail).IsNotNull();
                var points = tail!.Points;
                Check.That(points).HasSize(3);

                // The tip is the placement's TailTip in window coordinates (the anchor link is
                // unchanged by the reveal), and the base sits on the DRAWN card's tail edge.
                var tip = placement.TailTip!.Value;
                Check.WithCustomMessage($"{size}/{state}: the tip is the anchor's tail tip")
                    .That((points[0].X, points[0].Y)).IsEqualTo(
                        (tip.X - placement.Window.X, tip.Y - placement.Window.Y));
                double edge = placement.Side switch
                {
                    NoteSide.Right => drawnCard.Left - placement.Window.X,
                    NoteSide.Left => drawnCard.Right - placement.Window.X,
                    _ => 0,
                };
                if (placement.Side is NoteSide.Right or NoteSide.Left)
                {
                    Check.WithCustomMessage($"{size}/{state}: the base lies on the drawn card's edge")
                        .That(points[1].X).IsCloseTo(edge, 1e-9);
                    Check.That(points[2].X).IsCloseTo(edge, 1e-9);
                    Check.WithCustomMessage($"{size}/{state}: the base stays inside the drawn card")
                        .That(points[1].Y >= drawnCard.Top - placement.Window.Y - 1e-9
                            && points[2].Y <= drawnCard.Bottom - placement.Window.Y + 1e-9).IsTrue();
                }
                else
                {
                    double edgeY = placement.Side == NoteSide.Below
                        ? drawnCard.Top - placement.Window.Y
                        : drawnCard.Bottom - placement.Window.Y;
                    Check.WithCustomMessage($"{size}/{state}: the base lies on the drawn card's edge")
                        .That(points[1].Y).IsCloseTo(edgeY, 1e-9);
                    Check.That(points[2].Y).IsCloseTo(edgeY, 1e-9);
                }
            }

            AssertTail(restCard, "at rest");
            window.SetRevealed(true);
            AssertTail(manager.PlacementForTest(id)!.Card, "revealed");
            Check.WithCustomMessage("the placement's TailTip is untouched by the reveal")
                .That(manager.PlacementForTest(id)!.TailTip).IsEqualTo(placement.TailTip);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── 9. the reveal target is gated by an occluder; the NOTE-REVEAL lines are exact ──

    [StaFact]
    public void TheRevealTarget_IsGatedByAnOccluder_AndTheLogLinesAreExactAndContentFree()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        using var manager = NewManager(clock, lines);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = Saved(manager, "SECRET-WORD", 800, 500);
            var rest = manager.DrawnCardForTest(id);
            var centre = Centre(rest);
            var revealedCard = manager.PlacementForTest(id)!.Card; // D5's hide rect
            var outside = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(revealedCard.Right + NoteLayout.RevealHideMarginDip + 40),
                centre.Y);

            // An occluder over the at-rest card blocks the reveal exactly like it blocks ✎.
            manager.SetOccluderSources(noteNoticeBounds: () =>
                new PhysicalRect(centre.X - 300, centre.Y - 300, 600, 600));
            long t = Hold(manager, centre, 0, 2_000);
            Check.WithCustomMessage("an occluder over the at-rest card must block its reveal")
                .That(manager.IsRevealedForTest(id)).IsFalse();

            manager.SetOccluderSources(noteNoticeBounds: () => null);
            clock.NowMs = 1_000;
            t = Hold(manager, centre, t, 600);
            Check.That(manager.IsRevealedForTest(id)).IsTrue();
            Check.WithCustomMessage("the shown line is exact and carries no text")
                .That(lines).Contains("NOTE-REVEAL out=shown note=1 ms=500");

            clock.NowMs = 1_400;
            t = Hold(manager, outside, t, 2_300);
            Check.That(manager.IsRevealedForTest(id)).IsFalse();
            Check.WithCustomMessage("the hidden line is exact and carries no text")
                .That(lines).Contains("NOTE-REVEAL out=hidden note=1 ms=400 why=away");
            Check.WithCustomMessage("no reveal line may carry the note text")
                .That(lines.Count(l => l.Contains("SECRET", StringComparison.Ordinal))).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
