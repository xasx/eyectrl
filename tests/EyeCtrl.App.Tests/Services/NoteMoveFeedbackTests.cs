namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Sitting 9, row c — the Move feedback the owner asked for ("the move indicator has no
/// Feedback. Also, use a familiar control."):
/// <list type="bullet">
/// <item>the dimmed card's HINT — the German instruction "Auf den neuen Platz für die Notiz
/// schauen" — at the note text size, SemiBold, inside the text column and inside the card's
/// border; it stays at full opacity while the card's own content dims (F1), is gone on every
/// exit and never covers the Move controls;</item>
/// <item>the SETTLE RING + DASHED LANDING PREVIEW — appears only after the settle window has held
/// ~150 ms, its arc follows the tracker's progress, its dashed rect IS the placement the real
/// re-anchor derives, it holds the accepted spot while the place disc is up, and it hides on an
/// off-radius sample, a gap, a pause, Cancel and place;</item>
/// <item>the PLACE-HERE DISC — the :place target on the drawn disc's centre, the dwell progress
/// reaching the disc's arc, and the German label under it.</item>
/// </list>
/// <para>
/// Everything here is headless: the manager runs with its show seam off (<see cref="NoteManager.RealNoteWindows"/>
/// is false for this assembly), so no window reaches the owner's screen. The German table is
/// process-global, so the class lives in the exclusive string-table collection and resets it in a
/// finally.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class NoteMoveFeedbackTests
{
    private const double TextSize = NoteSettings.DefaultTextSizeDip;

    private readonly ITestOutputHelper _output;

    public NoteMoveFeedbackTests(ITestOutputHelper output) => _output = output;

    // ── the hint ──

    [StaFact]
    public void TheMoveHint_ShowsTheGermanLineInsideTheCard_AtTheNoteTextSizeSemiBold()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        StringTable.Activate(GermanDraft.Table);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var window = manager.WindowForTest(id)!;

            var hint = window.MoveHintTextForTest;
            Check.WithCustomMessage("the Move state must show the hint line").That(hint).IsNotNull();
            Check.WithCustomMessage("the hint must carry the German line the owner asked for")
                .That(hint!.Text).IsEqualTo("Auf den neuen Platz für die Notiz schauen");
            Check.WithCustomMessage("the hint reads at the note's own text size")
                .That(hint.FontSize).IsEqualTo(TextSize);
            Check.WithCustomMessage("the hint is an instruction: SemiBold")
                .That(hint.FontWeight).IsEqualTo(FontWeights.SemiBold);

            var placement = manager.PlacementForTest(id)!;
            var box = window.MoveHintForTest!;
            var local = new Rect(Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height);
            var card = NoteLayout.RectLocal(placement);
            Check.WithCustomMessage("the hint box must stay inside the card")
                .That(card.Contains(local)).IsTrue();
            // F1: the plate is inset by the card's border, so the left and bottom border lines
            // stay unbroken around it.
            Check.WithCustomMessage("the hint plate must start inside the card's left border")
                .That(local.Left).IsStrictlyGreaterThan(card.Left);
            Check.WithCustomMessage("the hint plate must end inside the card's bottom border")
                .That(local.Bottom).IsStrictlyLessThan(card.Bottom);

            // The hint owns no dwell target and never reaches the control column: neither Move
            // cell may intersect it, whatever side the card is on.
            foreach (var slot in NoteLayout.SlotsFor(NoteState.Move))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement, slot);
                var cell = new Rect(cx - (LayoutConstants.NoteCellDip / 2), cy - (LayoutConstants.NoteCellDip / 2),
                    LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
                Check.WithCustomMessage($"the hint must leave the Move control in slot {slot} untouched")
                    .That(local.IntersectsWith(cell)).IsFalse();
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void TheMoveHint_StaysAtFullOpacity_WhileTheCardsOwnContentDims()
    {
        // F1: the Move dim lives on the card's own elements, never on the window. A
        // window-level Opacity multiplies every child, so the hint (the one line the owner must
        // read in Move) came out grey at 0.6 — the readability row the owner already failed.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var window = manager.WindowForTest(id)!;

            Check.WithCustomMessage("the window itself must never be dimmed")
                .That(window.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("the hint plate must be at full opacity")
                .That(window.MoveHintForTest!.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("the hint's EFFECTIVE opacity (window x plate) must be 1.0")
                .That(window.Opacity * window.MoveHintForTest!.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("the card's border/background must dim to 0.6")
                .That(window.CardBorderForTest!.Opacity).IsEqualTo(0.6);
            Check.WithCustomMessage("the card's EFFECTIVE opacity (window x card) must be 0.6")
                .That(window.Opacity * window.CardBorderForTest!.Opacity).IsEqualTo(0.6);
            Check.WithCustomMessage("the body text (the card's content layer) must dim with the card")
                .That(window.ContentBorderForTest!.Opacity).IsEqualTo(0.6);

            var buttons = window.ButtonsForTest();
            Check.WithCustomMessage("the Move discs stay crisp so they remain familiar controls")
                .That(buttons[$"{id}:cancel"].Opacity).IsEqualTo(1.0);
            Check.That(buttons[$"{id}:otherside"].Opacity).IsEqualTo(1.0);

            // Leaving Move restores the card's own content.
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present); // Cancel
            Check.That(manager.IsMoving).IsFalse();
            Check.That(window.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("Display restores the card's full opacity")
                .That(window.CardBorderForTest!.Opacity).IsEqualTo(1.0);
            Check.That(window.ContentBorderForTest!.Opacity).IsEqualTo(1.0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData(18)]
    [InlineData(28)]
    [InlineData(48)]
    public void TheMoveHint_StaysInsideTheCard_AtEveryTextSize(double textSize)
    {
        // F3 addendum: the German instruction is 41 characters. It must wrap to at most two
        // lines at 28 DIP in the 420 DIP text column, and the plate must stay inside the card —
        // inside its border — at the smallest and largest sizes too.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings { TextSizeDip = (int)textSize };
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        StringTable.Activate(GermanDraft.Table);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var window = manager.WindowForTest(id)!;
            var text = window.MoveHintTextForTest!;
            var box = window.MoveHintForTest!;
            int lines = (int)Math.Round(text.DesiredSize.Height / text.LineHeight);
            _output.WriteLine(
                $"hint at {textSize} DIP: {lines} line(s), plate {box.Width:0}x{box.Height:0} DIP");

            var card = NoteLayout.RectLocal(manager.PlacementForTest(id)!);
            var local = new Rect(Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height);
            Check.WithCustomMessage($"at {textSize} DIP the hint plate must stay inside the card")
                .That(card.Contains(local)).IsTrue();
            Check.WithCustomMessage($"at {textSize} DIP the plate must start inside the card's left border")
                .That(local.Left).IsStrictlyGreaterThan(card.Left);
            Check.WithCustomMessage($"at {textSize} DIP the plate must end inside the card's bottom border")
                .That(local.Bottom).IsStrictlyLessThan(card.Bottom);

            foreach (var slot in NoteLayout.SlotsFor(NoteState.Move))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(manager.PlacementForTest(id)!, slot);
                var cell = new Rect(cx - (LayoutConstants.NoteCellDip / 2), cy - (LayoutConstants.NoteCellDip / 2),
                    LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
                Check.WithCustomMessage($"at {textSize} DIP the hint must leave slot {slot} untouched")
                    .That(local.IntersectsWith(cell)).IsFalse();
            }

            if (textSize == TextSize)
            {
                Check.WithCustomMessage("the German hint must wrap to at most two lines at 28 DIP")
                    .That(lines).IsLessOrEqualThan(2);
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void TheMoveHint_IsAbsentInDisplay_AndTheMoveControlsKeepTheirCentres()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewDisplayNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var window = manager.WindowForTest(id)!;
            Check.WithCustomMessage("a Display note carries no Move hint")
                .That(window.MoveHintForTest).IsNull();

            // The two cells the Move set re-registers (⇄ Other side, ⊘ Cancel) are the Display
            // set's slots 0 and 1: pick-up must leave their drawn and registered centres alone.
            var before = new[] { NoteTestBench.CellCentre(manager, id, 0), NoteTestBench.CellCentre(manager, id, 1) };

            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
            Check.That(manager.IsMoving).IsTrue();
            Check.That(window.MoveHintForTest).IsNotNull();

            var placement = manager.PlacementForTest(id)!;
            var buttons = window.ButtonsForTest();
            Span<(string Suffix, int Slot)> moveSet = [("otherside", 0), ("cancel", 1)];
            foreach (var (suffix, slot) in moveSet)
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement, slot);
                var ellipse = buttons[$"{id}:{suffix}"];
                Check.WithCustomMessage($"the drawn {suffix} disc must sit on its cell centre")
                    .That(Canvas.GetLeft(ellipse) + (ellipse.Width / 2)).IsCloseTo(cx, 1e-6);
                Check.That(Canvas.GetTop(ellipse) + (ellipse.Height / 2)).IsCloseTo(cy, 1e-6);
                Check.WithCustomMessage($"the registered {suffix} cell must not move on pick-up")
                    .That(NoteTestBench.CellCentre(manager, id, slot)).IsEqualTo(before[slot]);
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData("cancel")]
    [InlineData("otherside")]
    [InlineData("place")]
    public void TheMoveHint_IsGoneAfterEveryMoveExit(string exit)
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var window = manager.WindowForTest(id)!;
            Check.That(window.MoveHintForTest).IsNotNull();

            switch (exit)
            {
                case "cancel":
                    present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
                    break;
                case "otherside":
                    present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 0), present);
                    break;
                default:
                    // Settle first: place needs the disc, which appears on an accepted settle.
                    present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
                    Check.That(manager.PlaceShownForTest).IsTrue();
                    present = HoldGaze(manager, PlaceDiscCentrePhysical(manager), present);
                    break;
            }

            Check.WithCustomMessage($"leaving Move by {exit} must return to Display")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage($"the Move hint must be gone after {exit}")
                .That(window.MoveHintForTest).IsNull();
            Check.That(window.MoveHintTextForTest).IsNull();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the preview window ──

    [StaFact]
    public void ThePreview_AppearsAfterTheHold_AndItsRingFollowsTheSettle()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800); // DIP (400, 900), off the card

            long t = present;
            manager.OnGazeMoved(spot, t);
            manager.OnGazeMoved(spot, t + 100_000);
            Check.WithCustomMessage("a 100 ms rest must not flash the preview")
                .That(manager.MovePreviewShownForTest).IsFalse();

            manager.OnGazeMoved(spot, t + 200_000);
            Check.WithCustomMessage("the preview must appear once the window has held ~150 ms")
                .That(manager.MovePreviewShownForTest).IsTrue();
            var preview = manager.MovePreviewWindowForTest!;
            Check.WithCustomMessage("the ring's fill follows the settle's progress")
                .That(preview.Progress).IsCloseTo(0.4, 1e-9);
            Check.That(preview.RingShown).IsTrue();
            Check.WithCustomMessage("the ring is centred on the settle anchor")
                .That(preview.RingCentreDip).IsEqualTo(new Point(DipPoint(spot).X, DipPoint(spot).Y));

            // The dashed rect IS the derivation the real re-anchor uses for this anchor.
            var expected = ExpectedPlacement(manager, id, spot);
            Check.WithCustomMessage("the dashed landing rect must equal NoteLayout.Place's card for the anchor")
                .That(preview.CardRectDip).IsEqualTo(expected.Card);

            manager.OnGazeMoved(spot, t + 400_000);
            Check.WithCustomMessage("the ring keeps following the settle")
                .That(preview.Progress).IsCloseTo(0.8, 1e-9);
            Check.That(preview.RingShown).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheAcceptedPreview_HoldsItsSpot_AndEqualsTheRealPlacementAfterPlace()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800);

            // A full 500 ms settle on one spot: the centroid is that spot, so the preview's
            // derivation and the real re-anchor's must coincide exactly.
            present = Hold(manager, spot, present, 600);

            Check.That(manager.PlaceShownForTest).IsTrue();
            Check.WithCustomMessage("the accepted outline stays up while the place disc is shown")
                .That(manager.MovePreviewShownForTest).IsTrue();
            var preview = manager.MovePreviewWindowForTest!;
            Check.WithCustomMessage("the ring is over once the settle is accepted")
                .That(preview.RingShown).IsFalse();
            Check.That(preview.Progress).IsCloseTo(1.0, 1e-9);
            Check.WithCustomMessage("the accepted outline equals the card NoteLayout.Place gives for the centroid")
                .That(preview.CardRectDip).IsEqualTo(ExpectedPlacement(manager, id, spot).Card);

            // New samples while the disc is up must not move the accepted outline.
            var accepted = preview.CardRectDip;
            manager.OnGazeMoved(new PhysicalPoint(1200, 1400), present);
            manager.OnGazeMoved(new PhysicalPoint(1200, 1400), present + 300_000);
            Check.WithCustomMessage("a sample after the accept must not move the accepted outline")
                .That(preview.CardRectDip).IsEqualTo(accepted);

            // Dwell the disc: the note lands where the outline said it would.
            present = HoldGaze(manager, PlaceDiscCentrePhysical(manager), present);
            Check.That(manager.IsMoving).IsFalse();
            Check.WithCustomMessage("the preview goes when the note lands")
                .That(manager.MovePreviewShownForTest).IsFalse();
            Check.WithCustomMessage("preview == result: the real placement's card is the dashed rect")
                .That(manager.PlacementForTest(id)!.Card).IsEqualTo(accepted);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePreview_HidesOnAnOffRadiusSample()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800);

            manager.OnGazeMoved(spot, present);
            manager.OnGazeMoved(spot, present + 200_000);
            Check.That(manager.MovePreviewShownForTest).IsTrue();

            // 300 DIP away: past the 80 DIP radius, so the settle window restarts and the
            // preview has nothing to show.
            manager.OnGazeMoved(new PhysicalPoint(spot.X + 600, spot.Y), present + 300_000);
            Check.WithCustomMessage("an off-radius sample must drop the preview")
                .That(manager.MovePreviewShownForTest).IsFalse();
            Check.That(manager.MovePreviewWindowForTest!.IsDisplayed).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePreview_HidesOnAGap_APause_AndCancel()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800);

            // A gap: no information, so the tracker's window (and the preview) restart.
            manager.OnGazeMoved(spot, present);
            manager.OnGazeMoved(spot, present + 200_000);
            Check.That(manager.MovePreviewShownForTest).IsTrue();
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(spot, present + 300_000, IsInGap: true)));
            Check.WithCustomMessage("a gap must drop the preview")
                .That(manager.MovePreviewShownForTest).IsFalse();

            // A paused sample: the gate closes and the accumulating window restarts.
            present += 400_000;
            manager.OnGazeMoved(spot, present);
            manager.OnGazeMoved(spot, present + 200_000);
            Check.That(manager.MovePreviewShownForTest).IsTrue();
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(spot, present + 300_000, IsInGap: false))
            {
                IsPaused = true
            });
            Check.WithCustomMessage("a pause must drop the preview")
                .That(manager.MovePreviewShownForTest).IsFalse();

            // Cancel with the ACCEPTED outline on screen: while the place disc is up the outline
            // holds through the gaze's move to the Cancel cell, so only Cancel itself can take
            // it down — the case the leaving-Move hide exists for.
            present += 400_000;
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            Check.WithCustomMessage("the accepted outline must survive the move to the Cancel cell")
                .That(manager.MovePreviewShownForTest).IsTrue();
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
            Check.That(manager.IsMoving).IsFalse();
            Check.WithCustomMessage("Cancel must drop the preview")
                .That(manager.MovePreviewShownForTest).IsFalse();
            Check.That(manager.MovePreviewWindowForTest!.IsDisplayed).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the disc's hold (sitting 10 M1) and the Move log (sitting 10 M3) ──

    [StaFact]
    public void ThePlaceDisc_HoldsThroughSettlesNearTheAcceptedSpot_AndMovesOnlyBeyondTheHoldRadius()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800); // DIP (400, 900), off the card

            present = HoldGaze(manager, spot, present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var accepted = manager.PlaceCentroidForTest!.Value;
            var discRect = manager.PlaceWindowRectForTest;

            // A settle back ON the accepted spot — which sits ~115+ DIP from the disc, past the
            // 88 DIP margin; exactly the look-back that kept moving the disc away (sitting 10).
            // The hold radius pins it: the disc, its dwell and the outline stay put.
            present = HoldGaze(manager, accepted, present, 1200);
            Check.WithCustomMessage("a settle on the accepted spot must not move the disc")
                .That(manager.PlaceWindowRectForTest).IsEqualTo(discRect);
            Check.WithCustomMessage("the accepted spot itself is unchanged")
                .That(manager.PlaceCentroidForTest).IsEqualTo(accepted);

            // A settle 150 DIP below the accepted spot: past the disc's margin, inside the
            // 200 DIP hold radius → still held. (Physical px: 300 px = 150 DIP at 200 %.)
            var near = new PhysicalPoint(accepted.X, accepted.Y + 300);
            present = HoldGaze(manager, near, present, 1200);
            Check.WithCustomMessage("a settle 150 DIP from the accepted spot must not move the disc")
                .That(manager.PlaceWindowRectForTest).IsEqualTo(discRect);

            // A settle 250 DIP left of the accepted spot: beyond the hold radius → the disc
            // relocates to the new spot.
            var far = new PhysicalPoint(accepted.X - 500, accepted.Y); // 500 px = 250 DIP left
            present = HoldGaze(manager, far, present, 1200);
            Check.WithCustomMessage("a settle 250 DIP from the accepted spot must relocate the disc")
                .That(manager.PlaceWindowRectForTest).IsNotEqualTo(discRect);
            Check.WithCustomMessage("the disc relocates beside the NEW accepted spot")
                .That(manager.PlaceCentroidForTest).IsEqualTo(far);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePlaceDiscsDwell_SurvivesAHeldSettle_AndTheDiscStillPlaces()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var spot = new PhysicalPoint(800, 1800);

            present = HoldGaze(manager, spot, present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var accepted = manager.PlaceCentroidForTest!.Value;
            var discRect = manager.PlaceWindowRectForTest;
            var disc = PlaceDiscCentrePhysical(manager);

            // Partial dwell on the disc: the arc is mid-fill when the owner's gaze wanders off.
            for (var t = present; t <= present + 200_000; t += 100_000)
                manager.OnGazeMoved(new PhysicalPoint(disc.X + 10_000, disc.Y), t);
            for (var t = present + 250_000; t <= present + 350_000; t += 50_000)
                manager.OnGazeMoved(disc, t);
            present += 400_000;
            Check.WithCustomMessage("the partial dwell reaches the disc's arc")
                .That(manager.PlaceWindowForTest!.ProgressForTest).IsStrictlyGreaterThan(0.0);

            // A held settle 150 DIP below the accepted spot (past the disc's margin, inside the
            // hold radius): the disc — and the dwell target on it — hold still. Pre-M1 this
            // settle relocated the disc and re-registered the target from under the dwell.
            var near = new PhysicalPoint(accepted.X, accepted.Y + 300); // 300 px = 150 DIP down
            present = HoldGaze(manager, near, present, 1200);
            Check.WithCustomMessage("the held settle must not move the disc")
                .That(manager.PlaceWindowRectForTest).IsEqualTo(discRect);

            // The disc is still where it was: dwelling it completes the placement.
            present = HoldGaze(manager, disc, present, 1200);
            Check.WithCustomMessage("the disc survives the held settle and completes its dwell")
                .That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Every settle outcome writes exactly its one content-free line (M3): on-card,
    /// shown, held, moved — positions in DIP at one decimal place — and the placed end line
    /// carries the move's duration and the settle tally. The injected clock makes the duration
    /// exact; the injected sink captures the lines the way the log-line tests do.</summary>
    [StaFact]
    public void EverySettleOutcome_WritesItsOneContentFreeLine_AndThePlacedEndLine()
    {
        var lines = new List<string>();
        long clock = 50_000;
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: () => clock, workAreaDip: NoteTestBench.WorkAreaProvider,
            settleInfoLog: lines.Add);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present); // session starts at clock 50_000
            var spot = new PhysicalPoint(800, 1800); // DIP (400, 900)

            // on-card: the card's text area, away from the control column.
            var card = manager.PlacementForTest(id)!.Card;
            var cardCentre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(card.Left + (card.Width / 2)),
                DpiHelper.LogicalToPhysicalY(card.Top + (card.Height / 2)));
            clock = 60_000;
            present = HoldGaze(manager, cardCentre, present, 1200);
            Check.WithCustomMessage("a settle on the card writes the on-card line")
                .That(lines).HasElementThatMatches(l => l.StartsWith("NOTE-SETTLE out=on-card x=", StringComparison.Ordinal));

            // shown: the first settle off the card raises the disc beside (400, 900).
            clock = 70_000;
            present = HoldGaze(manager, spot, present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();

            // held: a settle back on the accepted spot (d = 0.0) leaves the disc alone.
            clock = 80_000;
            present = HoldGaze(manager, spot, present, 1200);

            // moved: 250 DIP left of the accepted spot (500 physical px) relocates the disc.
            var far = new PhysicalPoint(spot.X - 500, spot.Y); // DIP (150, 900)
            clock = 90_000;
            present = HoldGaze(manager, far, present, 1200);

            // The final hold dwells the relocated disc: one more held settle (the disc's own
            // margin), then the place dwell fires and the move ends at clock 100_000.
            clock = 100_000;
            present = HoldGaze(manager, PlaceDiscCentrePhysical(manager), present, 1200);
            Check.That(manager.IsMoving).IsFalse();

            var discCentre = PlaceDiscCentrePhysical(manager);
            var discDip = new PhysicalPoint(
                DpiHelper.PhysicalToLogicalX(discCentre.X), DpiHelper.PhysicalToLogicalY(discCentre.Y));
            double discDistance = Math.Sqrt(
                Math.Pow(discDip.X - 150.0, 2) + Math.Pow(discDip.Y - 900.0, 2));
            var expected = new[]
            {
                $"NOTE-SETTLE out=on-card {DipText(cardCentre)}",
                "NOTE-SETTLE out=shown x=400.0 y=900.0",
                "NOTE-SETTLE out=held x=400.0 y=900.0 d=0.0",
                "NOTE-SETTLE out=moved x=150.0 y=900.0 ox=400.0 oy=900.0 d=250.0",
                $"NOTE-SETTLE out=held {DipText(discCentre)} d={discDistance.ToString("F1", CultureInfo.InvariantCulture)}",
                "NOTE-MOVE end=placed ms=50000 settles=5 holds=2 moves=1",
            };
            Check.WithCustomMessage($"the settle/move-end lines must appear in order, one each:\n  {string.Join("\n  ", lines)}")
                .That(lines).IsEqualTo(expected);

            Check.WithCustomMessage("no line may carry note text")
                .That(lines.Count(l => l.Contains("saved text", StringComparison.Ordinal))).IsEqualTo(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>The two other move ends write their lines too: cancelled (through the real
    /// Cancel dwell) and otherside (the judged candidate applied), each with its own session's
    /// duration and tally.</summary>
    [StaFact]
    public void TheCancelledAndOtherSideEnds_WriteTheirLines()
    {
        var lines = new List<string>();
        long clock = 1_000_000;
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: () => clock, workAreaDip: NoteTestBench.WorkAreaProvider,
            settleInfoLog: lines.Add);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present); // move 1 starts at clock 1_000_000
            var spot = new PhysicalPoint(800, 1800);
            // The Cancel cell's centre while the note is still on its original side — the cell
            // the held settle and the Cancel dwell both ride.
            var cancelCentreText = DipText(NoteTestBench.CellCentre(manager, id, 1));

            present = HoldGaze(manager, spot, present, 1200); // shown: settles=1
            Check.That(manager.PlaceShownForTest).IsTrue();

            // The Cancel dwell: the held samples settle once on the card (settles=2), then the
            // dwell completes and the move ends at clock 2_000_000.
            clock = 2_000_000;
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present, 1200);
            Check.WithCustomMessage("the cancel dwell ends the move").That(manager.IsMoving).IsFalse();

            // Move 2: pick the note up again (the Display set's slot 1 cell), settle, disc —
            // then the Other-side activation ends it at clock 4_000_000.
            clock = 3_000_000;
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present, 1200);
            Check.That(manager.IsMoving).IsTrue();
            present = HoldGaze(manager, spot, present, 1200); // shown again: settles=1
            Check.That(manager.PlaceShownForTest).IsTrue();
            clock = 4_000_000;
            manager.OtherSideForTest(id);
            Check.WithCustomMessage("the other-side activation ends the move").That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            var expected = new[]
            {
                "NOTE-SETTLE out=shown x=400.0 y=900.0",
                $"NOTE-SETTLE out=on-card {cancelCentreText}",
                "NOTE-MOVE end=cancelled ms=1000000 settles=2 holds=0 moves=0",
                "NOTE-SETTLE out=shown x=400.0 y=900.0",
                "NOTE-MOVE end=otherside ms=1000000 settles=1 holds=0 moves=0",
            };

            Check.WithCustomMessage($"the end lines must match the sessions:\n  {string.Join("\n  ", lines)}")
                .That(lines).IsEqualTo(expected);

            Check.WithCustomMessage("no line may carry note text")
                .That(lines.Count(l => l.Contains("saved text", StringComparison.Ordinal))).IsEqualTo(0);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>A physical point rendered as the log's DIP position pair ("x=… y=…").</summary>
    private static string DipText(PhysicalPoint physical) =>
        string.Format(CultureInfo.InvariantCulture, "x={0:F1} y={1:F1}",
            DpiHelper.PhysicalToLogicalX(physical.X), DpiHelper.PhysicalToLogicalY(physical.Y));

    // ── the place disc ──

    [StaFact]
    public void ThePlaceTargetSitsOnTheDrawnDiscCentre_AndTheProgressReachesItsArc()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();

            var window = manager.PlaceWindowForTest!;
            var rect = manager.PlaceWindowRectForTest;
            var discCentre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(rect.X + window.DiscCentreDip.X),
                DpiHelper.LogicalToPhysicalY(rect.Y + window.DiscCentreDip.Y));

            // The registered :place target's own centre, read off the detector snapshot — not
            // "a rect that happens to cover the point".
            var target = manager.LiveTargetRects()
                .Select(r => new PhysicalPoint(r.X + (r.Width / 2), r.Y + (r.Height / 2)))
                .FirstOrDefault(p => p == discCentre);
            Check.WithCustomMessage("the :place target must sit on the drawn disc's centre")
                .That(target).IsEqualTo(discCentre);

            // No dwell yet: the disc's arc is hidden.
            Check.That(window.ArcForTest!.Visibility).IsEqualTo(Visibility.Hidden);
            Check.That(window.ProgressForTest).IsEqualTo(0.0);

            // A partial dwell on the disc: the detector's progress reaches the disc's arc.
            for (var t = present; t <= present + 200_000; t += 100_000)
                manager.OnGazeMoved(new PhysicalPoint(discCentre.X + 10_000, discCentre.Y), t);
            for (var t = present + 250_000; t <= present + 400_000; t += 50_000)
                manager.OnGazeMoved(discCentre, t);
            present += 450_000;

            Check.WithCustomMessage("the copy of the dwell progress must reach the disc's arc")
                .That(window.ProgressForTest).IsStrictlyGreaterThan(0.0).And.IsStrictlyLessThan(1.0);
            Check.That(window.ArcForTest!.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(window.ArcForTest!.Data).IsNotNull();

            // Completing the dwell places the note: the arc reached the activation.
            present = HoldGaze(manager, discCentre, present);
            Check.That(manager.IsMoving).IsFalse();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePlaceHereLabel_IsTheGermanHierPlatzieren()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        StringTable.Activate(GermanDraft.Table);
        try
        {
            var window = new NotePlaceHereWindow(TextSize);
            InvisibleTestWindow.Prepare(window);
            Check.WithCustomMessage("the place control's label must be the German line")
                .That(window.LabelForTest).IsNotNull();
            Check.That(window.LabelForTest!.Text).IsEqualTo("Hier platzieren");
            Check.WithCustomMessage("the disc is drawn at the note buttons' diameter")
                .That(window.DiscForTest!.Width).IsEqualTo(2 * NotePlaceHereWindow.DiscRadiusDip);
        }
        finally { StringTable.ResetToEmbeddedEnglish(); }
    }

    // ── helpers ──

    /// <summary>A Display note with text and a real (headless) window: the lifecycle every Move
    /// here starts from.</summary>
    private static string NewDisplayNote(NoteManager manager, PhysicalPoint anchor, ref long present)
    {
        manager.CreateNoteAt(anchor);
        manager.Apply(new NoteAppend("saved text"));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, commit: true);
        present += 10_000;
        return id;
    }

    /// <summary>Enters Move by dwelling ↕ — the pick-up is part of what the tests exercise.</summary>
    private static string NewMovingNote(NoteManager manager, PhysicalPoint anchor, ref long present)
    {
        var id = NewDisplayNote(manager, anchor, ref present);
        present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
        Check.That(manager.IsMoving).IsTrue();
        return id;
    }

    /// <summary>Looks away past every control for 200 ms (the leave-before-arm latch), then holds
    /// a point for <paramref name="ms"/>; the hold feeds the settle tracker as well.</summary>
    private static long HoldGaze(NoteManager manager, PhysicalPoint point, long present, int ms = 1200)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (var t = present; t <= present + 220_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (var t = present + 240_000; t <= present + ms * 1000; t += 33_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 33_000;
    }

    /// <summary>Holds one point for exactly <paramref name="ms"/> (no look-away): the settle
    /// tracker's window is the one the test is building.</summary>
    private static long Hold(NoteManager manager, PhysicalPoint point, long present, int ms)
    {
        for (var t = present; t <= present + ms * 1000; t += 100_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 100_000;
    }

    /// <summary>The placement the manager's own closure derives for an anchor: the same
    /// <see cref="NoteLayout.Place"/> call <c>FinishMove</c> makes, with the card size measured
    /// from the note's text.</summary>
    private static NotePlacement ExpectedPlacement(NoteManager manager, string id, PhysicalPoint anchorPhysical)
    {
        var note = manager.ActiveNotes.First(n => n.Id == id);
        double textHeight = manager.WindowForTest(id)!.MeasureTextHeightDip(note.Text);
        var card = new Size(
            NoteLayout.CardWidthDip(TextSize),
            NoteLayout.CardHeightDip(TextSize, textHeight, NoteTestBench.WorkArea.Height));
        var placement = NoteLayout.Place(anchorPhysical, note.Side, card, NoteTestBench.WorkArea,
            Array.Empty<Rect>(), DpiHelper.ScaleX)!;
        return NoteLayout.WithTagClearance(placement, TextSize);
    }

    /// <summary>The place disc's centre in physical px, read from the manager's placed rect.</summary>
    private static PhysicalPoint PlaceDiscCentrePhysical(NoteManager manager)
    {
        var rect = manager.PlaceWindowRectForTest;
        var window = manager.PlaceWindowForTest!;
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(rect.X + window.DiscCentreDip.X),
            DpiHelper.LogicalToPhysicalY(rect.Y + window.DiscCentreDip.Y));
    }

    /// <summary>A physical point as DIP, with the production conversions the manager uses.</summary>
    private static PhysicalPoint DipPoint(PhysicalPoint physical) =>
        new(DpiHelper.PhysicalToLogicalX(physical.X), DpiHelper.PhysicalToLogicalY(physical.Y));
}
