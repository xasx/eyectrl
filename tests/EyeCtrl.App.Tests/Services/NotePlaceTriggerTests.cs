namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
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
            Check.That(editing.Window).IsEqualTo(new Rect(806, 378, 310, 244)); // tail zone + card, DIP

            // T1: the keyboard appears over the card's right-side window (the bench work area DIP
            // rect doubled to physical when the probe delivers it).
            probe.RectsProvider = () => new[] { new PhysicalRect(1600, 740, 660, 520) }; // DIP (800, 370, 330, 260)
            probe.IsVisible = true;
            probe.RaiseChanged();

            var moved = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the editing card must leave the covered right side").That(moved.Side).IsEqualTo(NoteSide.Left);
            Check.That(moved.Window.IntersectsWith(new Rect(800, 370, 330, 260))).IsFalse();
            Check.That(moved.Window.IntersectsWith(editing.Window)).IsFalse();
            Check.WithCustomMessage("the moved card stays inside the work-area inset")
                .That(moved.Window.Top >= 54 && moved.Window.Top + moved.Window.Height <= 1146).IsTrue();

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
            Check.That(after.Side).IsEqualTo(NoteSide.Left);
            Check.That(after.Window.IntersectsWith(new Rect(806, 378, 350, 290))).IsFalse();
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
            var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(1600, 1800)); // DIP (800, 900)
            probe.RectsProvider = () => new[]
            {
                // Left column: x 454..644, y 778..1028 DIP (physical doubled).
                new PhysicalRect(908, 1556, 380, 500),
                // Right column: x 1000..1700 DIP, same band.
                new PhysicalRect(2000, 1556, 1400, 500),
            };
            var before = manager.PlacementForTest(id)!;
            Check.That(before.Side).IsEqualTo(NoteSide.Right);

            probe.RaiseChanged();

            var after = manager.PlacementForTest(id)!;
            Check.That(after.Side).IsEqualTo(NoteSide.Above);
            Check.WithCustomMessage("the Above side sits inside the work-area inset")
                .That(after.Window.Left >= 54 && after.Window.Right <= 1866).IsTrue();
            Check.That(after.Window.IntersectsWith(new Rect(454, 778, 190, 250))).IsFalse();
            Check.That(after.Window.IntersectsWith(new Rect(1000, 778, 700, 250))).IsFalse();
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
}
