namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// Review 02's regression probes (fixup 02), adopted verbatim in intent: R5 pins F1 (one jitter
/// sample just outside the circle must not re-arm a latched ✕ — the release needs 150 ms of
/// continuous leave), R6 pins F2 (a Done latches EVERY registered target under the gaze, so a
/// neighbour's ✕ cannot sit live under the held gaze).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class Review02ProbeTests
{
    public Review02ProbeTests() => DpiHelper.Initialize(1.0, 1.0);

    // R5: one jitter sample just outside slot 0's circle after Done re-arms ✕; the held gaze
    // then deletes the saved note 600 ms later.
    [StaFact]
    public void R5_OneSampleExcursionAfterDone_ThenHeldGaze_DeletesTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.SetKeyboardProbe(new NoteManagerTests.FakeKeyboardProbe { IsVisible = false });
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            var slot0 = Centre(anchor, 0);
            double r = DpiHelper.LogicalToPhysicalX(EyeCtrl.Core.LayoutConstants.NoteCellDip / 2);
            long t = 0;
            for (; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse();
            manager.OnGazeMoved(new PhysicalPoint(slot0.X + r + 2, slot0.Y), t); t += 33_000; // one jitter sample
            for (; t <= 1_800_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage($"one sample {r + 2:F0} px out re-armed ✕ and the held gaze deleted the note")
                .That(manager.ActiveNotes).HasSize(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // R6: a second note pinned close to the first. After B's Done, A's ✕ (same screen spot,
    // not latched because only the FINISHING note is disarmed) is live under the held gaze.
    [StaFact]
    public void R6_DoneOnB_HeldGaze_DoesNotDeleteNeighbourA()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.SetKeyboardProbe(new NoteManagerTests.FakeKeyboardProbe { IsVisible = false });
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(new PhysicalPoint(anchor.X + 20, anchor.Y + 10)); // A, 10x5 DIP away
            manager.AddEditingNoteTargetsForTest(anchor);                                   // B
            manager.Apply(new NoteAppend("second"));
            var slot0 = Centre(anchor, 0);
            long t = 0;
            for (; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse();
            for (; t <= 1_800_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage("the held gaze on B's Done deleted neighbour A through A's live ✕")
                .That(manager.ActiveNotes).HasSize(2);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static PhysicalPoint Centre(PhysicalPoint anchor, int slot) =>
        NoteTestBench.CellCentreFromAnchor(anchor, slot);
}
