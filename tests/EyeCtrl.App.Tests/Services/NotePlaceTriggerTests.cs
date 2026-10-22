namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NotePlaceTriggerTests
{
    // P3 D3's trigger tests run at the user's 2.0 like every note geometry test. The probe's
    // five sources (T1 visibility, T2 emoji mode, T3 relayout, T4 edge flip, T5 button set)
    // collapse at IKeyboardProbe.Changed, so the manager seam is probed by the RECT ARRANGEMENTS
    // those sources produce: a move-off rect (T1), grown/flipped column coverage (T2-T4), and a
    // clear-of-the-card arrangement proving the no-over-move guard (T3, T5's set change keeps the
    // card clear). T6 is driven through the messenger the manager registered with. T7 (card
    // height) and T8 (text size) cannot change the fixed T4 card size — they re-derive through
    // ApplyPlacementToNodeWindow's seam once T5's auto-height and setting land (documented at the
    // manager's Apply).
    //
    // T6 fixup-07 F2 changed what an Edit note does when a keyboard covers a side: the 24 DIP
    // clearance now holds in BOTH placement passes (measured on the window that is drawn, tag
    // strip included), so a side that only "fits" no longer wins. These tests therefore pin the
    // CLEARANCE - the card, its cells and the number tag keep the gap - rather than the side the
    // cycle happened to land on.

    [StaFact]
    public void KeyboardShow_WhileEditing_RePlaces_ClearOfTheKeyboard_Restores_AfterDone()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(1600, 1000)); // DIP (800, 500)
            manager.Apply(new NoteAppend("hello"));      // a draft to save: Done takes the straight-back exit
            var editing = manager.PlacementForTest(id)!;
            Check.That(editing.Side).IsEqualTo(NoteSide.Right);
            Check.That(editing.Window).IsEqualTo(new Rect(806, 330, 580, 340) with { Y = 330 - NoteLayout.TagProtrusionDip(NoteSettings.DefaultTextSizeDip), Height = 340 + NoteLayout.TagProtrusionDip(NoteSettings.DefaultTextSizeDip) }); // tail zone + card, DIP (P4 card: 550 text+column+gap), grown up by the number tag's protrusion

            // T1: the keyboard appears over the card's right-side window (the bench work area DIP
            // rect doubled to physical when the probe delivers it).
            probe.RectsProvider = () => new[] { new PhysicalRect(1600, 740, 660, 520) }; // DIP (800, 370, 330, 260)
            probe.IsVisible = true;
            probe.RaiseChanged();

            var moved = manager.PlacementForTest(id)!;
            var keyboard = new Rect(800, 370, 330, 260);
            Check.WithCustomMessage("the editing card must leave the covered right side")
                .That(moved.Card.IntersectsWith(keyboard)).IsFalse();
            // T6 fixup-07 F2, review-07 nit: the DRAWN window holds the card, the tail zone and
            // the tag strip, so one gap on it is stronger than the card and tag separately.
            Check.WithCustomMessage("the drawn window must keep the clearance gap from the keyboard")
                .That(GapFrom(moved.Window, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            Check.That(moved.Window.IntersectsWith(editing.Window)).IsFalse();
            Check.WithCustomMessage("the moved card stays inside the work-area inset")
                .That(moved.Window.Top >= NoteLayout.Constants.EdgeMarginDip && moved.Window.Top + moved.Window.Height <= 1200 - NoteLayout.Constants.EdgeMarginDip).IsTrue();

            // The saved side returns once the editor is gone even though the keyboard stays up.
            manager.FinishEdit(id, commit: true);
            var restored = manager.PlacementForTest(id)!;
            Check.That(restored.Side).IsEqualTo(NoteSide.Right);
            Check.That(restored.Window).IsEqualTo(editing.Window);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void GrownRects_WhileEditing_RePlaceClearOfTheKeyboard()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // T2's emoji panel grows the arrangement downward-right; the editing card sits inside.
            var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(1600, 1000)); // DIP (800, 500)
            probe.RectsProvider = () => new[] { new PhysicalRect(1612, 756, 700, 580) }; // DIP (806, 378, 350, 290)
            var before = manager.PlacementForTest(id)!;
            Check.That(before.Side).IsEqualTo(NoteSide.Right);

            probe.RaiseChanged();

            var after = manager.PlacementForTest(id)!;
            var keyboard = new Rect(806, 378, 350, 290);
            Check.WithCustomMessage("the editing card must leave the keyboard")
                .That(after.Card.IntersectsWith(keyboard)).IsFalse();
            // review-07 nit: one gap on the DRAWN window (card + tail zone + tag strip).
            Check.WithCustomMessage("the drawn window must keep the clearance gap from the keyboard")
                .That(GapFrom(after.Window, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            Check.That(after.Window.IntersectsWith(before.Window)).IsFalse();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void BothColumnsCovered_WhileEditing_RePlacesToAFreeSide()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // T4's edge flip: the covered columns sit low onscreen, the anchor mid-low. Left and
            // Right are covered, Below falls outside the inset (bottom 1180 > 1146), so Above is
            // the only free side of the cycle.
            var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(1800, 1800)); // DIP (900, 900)
            probe.RectsProvider = () => new[]
            {
                // Left column: x 454..604, y 778..1028 DIP (physical doubled) - narrow enough
                // that Above (524 DIP wide) still fits between the columns.
                new PhysicalRect(908, 1556, 300, 500),
                // Right column: x 1200..1700 DIP, same band.
                new PhysicalRect(2400, 1556, 1000, 500),
            };
            var before = manager.PlacementForTest(id)!;
            Check.That(before.Side).IsEqualTo(NoteSide.Right);

            probe.RaiseChanged();

            var after = manager.PlacementForTest(id)!;
            var leftColumn = new Rect(454, 778, 150, 250);
            var rightColumn = new Rect(1200, 778, 500, 250);
            Check.WithCustomMessage("the note must find a free spot clear of both columns")
                .That(after.Card.IntersectsWith(leftColumn) || after.Card.IntersectsWith(rightColumn)).IsFalse();
            // review-07 nit: one gap on the DRAWN window (card + tail zone + tag strip).
            Check.WithCustomMessage("the drawn window must keep the clearance gap from the left column")
                .That(GapFrom(after.Window, leftColumn)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            Check.WithCustomMessage("the drawn window must keep the clearance gap from the right column")
                .That(GapFrom(after.Window, rightColumn)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
            Check.WithCustomMessage("the Above side sits inside the work-area inset")
                .That(after.Window.Left >= NoteLayout.Constants.EdgeMarginDip && after.Window.Right <= 1920 - NoteLayout.Constants.EdgeMarginDip).IsTrue();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void ClearRearrangement_WhileEditing_KeepsTheSavedSide()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // T3's relayout that never covers the card (nor any other fitting side): the raise
            // must not over-move the already-clear editing card.
            var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(1600, 1000)); // DIP (800, 500)
            probe.RectsProvider = () => new[] { new PhysicalRect(120, 1000, 600, 120) }; // DIP (60, 500, 300, 60)
            var before = manager.PlacementForTest(id)!;

            probe.RaiseChanged();

            var after = manager.PlacementForTest(id)!;
            Check.That(after.Side).IsEqualTo(NoteSide.Right);
            Check.That(after.Window).IsEqualTo(before.Window);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void GeometryMessage_RePlacesEveryNote_InsideTheShrunkWorkArea()
    {
        var messenger = new WeakReferenceMessenger();
        var area = new Rect(0, 0, 1920, 1200);
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: () => area);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var far = manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));    // DIP (800, 500)
            var near = manager.AddNoteTargetsForTest(new PhysicalPoint(200, 400));     // DIP (100, 200)
            var farBefore = manager.PlacementForTest(far)!;
            var nearBefore = manager.PlacementForTest(near)!;

            // T6: the work area shrinks (the message the geometry watcher broadcast) — EVERY note
            // re-derives. `far`'s right side no longer fits (right edge 1116 > inset 946), `far`
            // moves to Left; `near`'s right side still fits and it stays put.
            area = new Rect(0, 0, 1000, 1200);
            messenger.Send(new DisplayGeometryChangedMessage(new DisplayGeometry(1000, 1200, 2, 2)));

            var farAfter = manager.PlacementForTest(far)!;
            Check.That(farAfter.Side).IsEqualTo(NoteSide.Left);
            Check.WithCustomMessage("the re-placed far note stays inside the shrunken inset")
                .That(farAfter.Window.Left >= 54 && farAfter.Window.Right <= 946).IsTrue();

            var nearAfter = manager.PlacementForTest(near)!;
            Check.That(nearAfter.Side).IsEqualTo(NoteSide.Right);
            Check.That(nearAfter.Window).IsEqualTo(nearBefore.Window);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void WorkAreaStaticChange_RePlacesEveryNote_ThroughTheSeam()
    {
        // fixup-01 G2: the SystemParameters half of T6 (the one the DisplayGeometryChangedMessage
        // does not cover — taskbar resize/auto-hide) had no test that could fail. The seam drives
        // the same handler with the same property name.
        var messenger = new WeakReferenceMessenger();
        var area = new Rect(0, 0, 1920, 1200);
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: () => area);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var far = manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));    // DIP (800, 500)
            manager.AddNoteTargetsForTest(new PhysicalPoint(200, 400));                // DIP (100, 200)

            area = new Rect(0, 0, 1000, 1200);
            manager.OnWorkAreaChangedForTest();

            var farAfter = manager.PlacementForTest(far)!;
            Check.WithCustomMessage("the WorkArea static must re-place every note, like the message does")
                .That(farAfter.Side).IsEqualTo(NoteSide.Left);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void TheWorkAreaStaticsAreSubscribed_AndUnsubscribedOnDispose()
    {
        // fixup-01 G2's source pin (the EventCoordinatorTests precedent): the constructor must
        // subscribe SystemParameters.StaticPropertyChanged and Dispose must unsubscribe — a
        // dropped += would leave T6's taskbar-resize half permanently deaf, a dropped -= would
        // keep every disposed manager subscribed to a global WPF static.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "NoteManager.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("NoteManager no longer subscribes SystemParameters.StaticPropertyChanged.")
            .That(source.Contains("SystemParameters.StaticPropertyChanged+=OnSystemPropertyChanged;")).IsTrue();
        Check.WithCustomMessage("NoteManager.Dispose no longer unsubscribes SystemParameters.StaticPropertyChanged.")
            .That(source.Contains("SystemParameters.StaticPropertyChanged-=OnSystemPropertyChanged;")).IsTrue();
    }
    /// <summary>The number tag's rect in SCREEN coordinates (NoteLayout returns it window-relative).</summary>
    private static Rect TagRect(NotePlacement placement, double textSizeDip)
    {
        var tag = NoteLayout.TagRect(placement, textSizeDip);
        return new Rect(placement.Window.X + tag.X, placement.Window.Y + tag.Y, tag.Width, tag.Height);
    }

    /// <summary>The clearance between two rects: zero when they touch or overlap.</summary>
    private static double GapFrom(Rect window, Rect rect)
    {
        double dx = Math.Max(rect.Left - window.Right, 0) + Math.Max(window.Left - rect.Right, 0);
        double dy = Math.Max(rect.Top - window.Bottom, 0) + Math.Max(window.Top - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
