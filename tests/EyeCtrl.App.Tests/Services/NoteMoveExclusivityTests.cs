namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

[Collection("DpiHelper Tests (App)")]
public class NoteMoveExclusivityTests
{
    public NoteMoveExclusivityTests() => DpiHelper.Initialize(1.0, 1.0);

    private sealed class Timer : INoteUndoTimer
    {
        public bool IsRunning { get; private set; }
        public void Start(System.Action onTick) => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }

    private static NoteManager Make(WeakReferenceMessenger m) =>
        new(m, hitTester: InactiveNoteHitTester.Instance, clockMs: () => 0, undoTimer: new Timer(),
            workAreaDip: NoteTestBench.WorkAreaProvider);

    private static string Saved(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    /// <summary>Reviewer probe R10 (fixup-01 M1): Move must stay exclusive. A re-registration
    /// during A's Move (here the keyboard probe's T1–T5 raise) used to re-register every note
    /// — making B's card controls live while B sits dimmed — and StartMove(B) used not to end
    /// A's Move, leaving two notes in Move at once. Proof mutants: remove the Move-only branch
    /// in ReRegisterAllTargets, or the LeaveMove in StartMove; each turns this red.</summary>
    [StaFact]
    public void DuringAMove_OtherNotesStayFrozen_AndASecondMoveEndsTheFirst()
    {
        var m = new WeakReferenceMessenger();
        using var manager = Make(m);
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = Saved(manager, "A", new PhysicalPoint(800, 600));
            var b = Saved(manager, "B", new PhysicalPoint(2400, 1600));
            manager.StartMove(a);
            int duringMove = manager.LiveTargetRects().Count;

            probe.IsVisible = true;
            probe.RaiseChanged(); // T1 while moving → ReRegisterAllTargets
            int afterProbe = manager.LiveTargetRects().Count;

            manager.StartMove(b);
            var stateA = manager.ActiveNotes.First(n => n.Id == a).State;

            Check.WithCustomMessage(
                    $"live targets during A's Move: {duringMove} → after a probe raise: {afterProbe}; "
                    + $"after StartMove(B) A is {stateA}")
                .That(afterProbe == duringMove && stateA != NoteState.Move).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>fixup-01 m1 (review): Other side must apply the candidate the offer JUDGED —
    /// here the keyboard's avoid rects push the next side (Left) and Below off the cycle onto
    /// Above, so a fresh un-avoided re-derive at dwell time would apply Left instead.
    /// Proof mutant: re-deriving with an empty avoid set in OtherSideForMoving turns this red.</summary>
    [StaFact]
    public void OtherSide_AppliesThePlacementTheOfferJudged()
    {
        var m = new WeakReferenceMessenger();
        var area = new System.Windows.Rect(0, 0, 1920, 1200);
        using var manager = new NoteManager(m, hitTester: InactiveNoteHitTester.Instance, clockMs: () => 0,
            undoTimer: new Timer(), workAreaDip: () => area);
        var probe = new NoteManagerTests.FakeKeyboardProbe
        {
            IsVisible = true,
            RectsProvider = () => new[] { new PhysicalRect(800, 1000, 1000, 240) }, // DIP (400, 500, 500, 120)
        };
        manager.SetKeyboardProbe(probe);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // The moving note sits at DIP (800, 500): its next side (Left) window and the Below
            // window intersect the keyboard's avoid rect, so the cycle lands on Above and the
            // offer registers.
            var id = manager.AddMovingNoteTargetsForTest(new PhysicalPoint(1600, 1000));
            var judged = manager.JudgedOtherSideForTest;
            Check.WithCustomMessage("the offer must have judged an alternative under the avoid set")
                .That(judged).IsNotNull();
            Check.That(judged!.Side).IsEqualTo(NoteSide.Above);

            manager.OtherSideForTest(id);

            Check.WithCustomMessage("the Other-side dwell applied its judged side")
                .That(manager.PlacementForTest(id)!.Side).IsEqualTo(judged!.Side);
            Check.WithCustomMessage("the Other-side dwell applied its judged window rect — the "
                    + "judged candidate plus the number tag's top strip (T6 fixup-01)")
                .That(manager.PlacementForTest(id)!.Window)
                .IsEqualTo(NoteLayout.WithTagClearance(judged!, NoteSettings.DefaultTextSizeDip).Window);
            Check.That(manager.ActiveNotes[0].Side).IsEqualTo(NoteSide.Above);
            Check.WithCustomMessage("the dwell ends the move")
                .That(manager.IsMoving).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
