namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

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
}
