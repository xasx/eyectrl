namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NoteMoveAcceptanceTests
{
    // P3 D4's Move acceptance, driven by fake gaze the way a user's stream behaves: dwell the
    // ↕ control to pick the note up, wander (a moving gaze never settles — the tracker window
    // restarts on every out-of-radius sample), accept a settle (the Place-here window appears
    // beside the *centroid*, not the last gaze), and dwell it to re-anchor. The card holds on
    // its placement while moving (it never chases the gaze); geometry asserts go through the
    // stored placement (PlacementForTest), the same closure the dwells derive from.
    //
    // The dwell threshold here is the manager's own: the hold pumps run 800 ms after a short
    // look-away, which re-arms the leave-before-arm latch and then accumulates the full dwell —
    // the same cadence that arms the ✓/✕ dwells below in NoteManagerTests.

    [StaFact]
    public void DwellMove_BringsTheNoteIntoMove_WithOtherSideAndCancel()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewDisplayNote(manager, new PhysicalPoint(1600, 1000), ref present); // DIP (800, 500)
            var before = manager.PlacementForTest(id)!;

            // The ↕ column cell is the card's slot 1; the display target set is ✎↕✕.
            var moveCell = NoteTestBench.CellCentre(manager, id, 1);
            present = HoldGaze(manager, moveCell, present);

            Check.WithCustomMessage("the ↕ dwell must pick the note up").That(manager.IsMoving).IsTrue();
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Move);
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
            Check.WithCustomMessage("a pick-up must not move the card")
                .That(before.Window).IsEqualTo(manager.PlacementForTest(id)!.Window);
            Check.WithCustomMessage("the move set is Other side + Cancel (no place yet)")
                .That(manager.LiveTargetRects()).HasSize(2);
            Check.That(manager.PlaceShownForTest).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void WanderingGaze_WhileMoving_ShowsNoPlaceHere()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);

            // ±100 DIP jumps around a free spot: every sample restarts the tracker window
            // (radius 60 DIP), so it never accumulates the 500 ms settle — and the wander
            // crosses no control cell, so no dwell fires either.
            present = Wander(manager, new PhysicalPoint(800, 1800), present);

            Check.That(manager.IsMoving).IsTrue();
            Check.WithCustomMessage("a wandering gaze must never anchor a place-here")
                .That(manager.PlaceShownForTest).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void OffCardSettle_ShowsPlaceHere_BesideTheCentroid()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);

            // Settle 600 ms at DIP (400, 900): inside the settle radius (60 DIP) and the settle
            // time, off the card — an accepted settle. The :place target joins the move set.
            var settle = new PhysicalPoint(800, 1800);
            present = HoldGaze(manager, settle, present, 1200);

            Check.WithCustomMessage("an off-card settle must anchor place-here")
                .That(manager.PlaceShownForTest).IsTrue();
            Check.WithCustomMessage("the place-here anchor is the settle's centroid, not the last gaze")
                .That(manager.PlaceCentroidForTest).IsEqualTo(settle);
            Check.That(manager.IsMoving).IsTrue();
            Check.WithCustomMessage("the move set gains the :place target")
                .That(manager.LiveTargetRects()).HasSize(3);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void SettleOnTheCard_IsIgnored_AsLookingAtTheNoteYouHold()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var cardCentre = DpiOfCentre(manager.PlacementForTest(id)!.Card);

            present = HoldGaze(manager, cardCentre, present, 1200);

            Check.That(manager.IsMoving).IsTrue();
            Check.WithCustomMessage("a settle on the card itself must not ask to place")
                .That(manager.PlaceShownForTest).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void SettleInsideThePlaceMargin_HoldsTheWindowStill()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var window = manager.PlaceWindowRectForTest;
            var centroid = manager.PlaceCentroidForTest!;

            // A fresh settle on a point in the margin ring — inside the window's detour radius
            // (60 DIP) but outside the window itself and outside its :place hit circle (30 DIP
            // around the window's middle), so the dwell on :place cannot fire. The window must
            // hold still there, or every fresh settle would walk the anchor around.
            present = HoldGaze(manager, new PhysicalPoint(1200, 1900), present, 1200); // DIP (600, 950)

            Check.WithCustomMessage("a settle inside the margin must hold the window")
                .That(manager.PlaceWindowRectForTest).IsEqualTo(window);
            Check.That(manager.PlaceCentroidForTest).IsEqualTo(centroid);
            Check.That(manager.IsMoving).IsTrue();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void DwellPlaceHere_ReAnchorsAtTheAcceptedCentroid()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var anchor = manager.PlaceCentroidForTest!;

            // The :place dwell re-anchors the note on the ACCEPTED centroid (the dwell started
            // on the window itself, so the "last gaze" defence of the activation never buys the
            // wrong spot here: the dwell sits on its target either way).
            present = HoldGaze(manager, DpiOfCentre(manager.PlaceWindowRectForTest), present);

            Check.WithCustomMessage("the place dwell must return the note to Display")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsMoving).IsFalse();
            Check.WithCustomMessage("the note re-anchors at the accepted centroid")
                .That(manager.ActiveNotes[0].Anchor).IsEqualTo(anchor);
            Check.WithCustomMessage("the place-here window must be gone after the re-anchor")
                .That(manager.PlaceShownForTest).IsFalse();
            Check.WithCustomMessage("the display set (✎↕✕) is back")
                .That(manager.LiveTargetRects()).HasSize(3);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void CancelDwell_RestoresTheDisplayUntouched()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            var before = manager.PlacementForTest(id)!;

            // The Cancel cell is the move set's slot 1 — the exact spot the picked-up ↕ left the
            // gaze on, latched: the look-away in HoldGaze re-arms it, the hold fires it.
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);

            Check.WithCustomMessage("the Cancel dwell must return the note to Display")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsMoving).IsFalse();
            Check.WithCustomMessage("cancel leaves the placement exactly where it was")
                .That(manager.PlacementForTest(id)).IsEqualTo(before);
            Check.That(manager.PlaceShownForTest).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void DwellOtherSide_MovesToTheNextSide_RespectingTheExclusion()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            Check.That(manager.PlacementForTest(id)!.Side).IsEqualTo(NoteSide.Right);
            var displayWindow = manager.PlacementForTest(id)!.Window;

            // The Other side cell is the move set's slot 0; the dwell applies the side cycle's
            // next fitting side at once and returns to Display there (never the current side —
            // the exclusion rule — which is why the placement flips to Left).
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 0), present);

            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsMoving).IsFalse();
            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the NoteSide cycle's next fitting side must be Left")
                .That(after.Side).IsEqualTo(NoteSide.Left);
            Check.WithCustomMessage("a side change must not shift the card vertically")
                .That(after.Window.Top).IsEqualTo(displayWindow.Top);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void OtherSide_HidesAgainstTheKeyboard_AndReturnsWhenItFrees()
    {
        // P3 D3 row T1 on the Move state: the ⊢ availability derives over the placement's
        // avoid rects, so a keyboard covering the Other side's window hides the control, and the
        // probe's raise re-derives it the moment the keyboard frees the room back. Mutation:
        // take the avoid-rects argument out of the move-availability derivation → the covered
        // control stays registered, red.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            Check.WithCustomMessage("the move set starts complete").That(manager.LiveTargetRects()).HasSize(2);
            var glyph = OtherSideGlyph(manager.WindowForTest(id)!);
            Check.That(glyph).IsNotNull();
            Check.That(glyph!.Visibility).IsEqualTo(System.Windows.Visibility.Visible);

            // The probe's physical rect covers the Right-side note's Other (Left) window exactly
            // — DIP (484, 378, 310, 244) — and never the card's own window (x 806..1116 DIP).
            probe.RectsProvider = () => new[] { new PhysicalRect(968, 756, 620, 488) };
            probe.IsVisible = true;
            probe.RaiseChanged();

            Check.WithCustomMessage("a keyboard-covered Other side must not register")
                .That(manager.LiveTargetRects()).HasSize(1);
            Check.WithCustomMessage("the drawn ⇄ must drain with its target")
                .That(glyph.Visibility).IsEqualTo(System.Windows.Visibility.Collapsed);

            probe.IsVisible = false;
            probe.RectsProvider = () => Array.Empty<PhysicalRect>();
            probe.RaiseChanged();

            Check.WithCustomMessage("Other side returns when the keyboard frees the room")
                .That(manager.LiveTargetRects()).HasSize(2);
            Check.That(glyph.Visibility).IsEqualTo(System.Windows.Visibility.Visible);
            Check.That(manager.IsMoving).IsTrue();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // ── pump helpers ──

    // NOTE: every pump in a test must run on ONE monotonic clock — the settle tracker drops a
    // timestamp older than its window's last sample, and the dwell detector does the same — so
    // each helper threads and returns the live cursor instead of restarting at 0.

    /// <summary>A Display note with text and a real (unshown-parked, review-01-grade) window:
    /// the exact lifecycle the acceptance dwells drive. Returns the cursor past its pumps.</summary>
    private static string NewDisplayNote(NoteManager manager, PhysicalPoint anchor, ref long present)
    {
        manager.CreateNoteAt(anchor);
        manager.Apply(new NoteAppend("x"));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, commit: true);
        present += 10_000;
        return id;
    }

    /// <summary>Enters the Move state by dwelling the ↕ control itself — the pick-up is part of
    /// the acceptance, not a seam. Returns the cursor past its pumps.</summary>
    private static string NewMovingNote(NoteManager manager, PhysicalPoint anchor, ref long present)
    {
        var id = NewDisplayNote(manager, anchor, ref present);
        present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
        Check.That(manager.IsMoving).IsTrue();
        return id;
    }

    /// <summary>Look away past every control for 200 ms (the ✓/✕ cadence: enough evidence for
    /// the leave-before-arm latch to release), then hold past the 600 ms dwell. While the note
    /// is moving, the hold also feeds the settle tracker — cells sit inside the card, so cell
    /// dwells settle onto the card's ignored path and can never spuriously anchor place-here.
    /// Returns the cursor 33 ms past the pump's last sample.</summary>
    private static long HoldGaze(NoteManager manager, PhysicalPoint point, long present, int ms = 1200)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (var t = present; t <= present + 220_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (var t = present + 240_000; t <= present + ms * 1000; t += 33_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 33_000;
    }

    /// <summary>±100 DIP jumps around a spot: outside the settle radius (60 DIP) on every
    /// sample, so the tracker window restarts forever and never settles. Returns the cursor.</summary>
    private static long Wander(NoteManager manager, PhysicalPoint around, long present)
    {
        for (var t = present; t <= present + 900_000; t += 33_000)
        {
            var dx = t / 66_000 % 2 == 0 ? -200 : 200;
            manager.OnGazeMoved(new PhysicalPoint(around.X + dx, around.Y + dx), t);
        }
        return present + 933_000;
    }

    private static PhysicalPoint DpiOfCentre(Rect dip)
        => new(
            DpiHelper.LogicalToPhysicalX(dip.Left + dip.Width / 2),
            DpiHelper.LogicalToPhysicalY(dip.Top + dip.Height / 2));

    /// <summary>The ⇄ glyph's TextBlock in a note window's drawn tree, or null.</summary>
    private static System.Windows.Controls.TextBlock? OtherSideGlyph(EyeCtrl.App.Overlays.NoteWindow window)
    {
        if (window.Content is not System.Windows.Controls.Canvas canvas) return null;
        foreach (var child in canvas.Children)
            if (child is System.Windows.Controls.TextBlock { Text: "⇄" } label)
                return label;
        return null;
    }
}
