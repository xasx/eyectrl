namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// W1 — Restore. The recording fake host (C14) with suppression forwarded to a real headless
/// <see cref="EyeCtrl.Core.GazeEngine"/>; continuations and <c>Check</c> are invoked directly
/// (build plan W1, cases (a)-(g)).
///
/// <para>
/// Every case starts with the keyboard visible and <c>FixationSuppressor.Keyboard</c> held on the
/// real engine (review 05 F2), so a case can end by releasing only that claim and reading
/// <c>SuppressFixation</c> once: false means the probe left no claim of its own behind.
/// </para>
/// </summary>
public class SubPixelProbeRestoreTests
{
    // ---- W1 (a): a throw at each BeginStart / CaptureAndShow step --------------------------------

    public static TheoryData<string, bool> StartSteps()
    {
        var data = new TheoryData<string, bool>();
        foreach (var step in new[]
                 {
                     "SetProbeOpen", "HideRing", "AcquireProbeSuppression", "DeactivateToolbar",
                     "HideKeyboard", "SuspendSummon", "AssertToolbarInactive", "AnyMenuVisible",
                     "CaptureBackdrop", "CreateViewModel", "CreateWindow", "Show",
                 })
        {
            data.Add(step, true);
            data.Add(step, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StartSteps))]
    public void AfterAThrowAtEachStep_EveryEffectIsUndone(string step, bool ringVisible)
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.CursorVm.IsVisible = ringVisible;
        world.Host.RingVisible = ringVisible;

        if (step == "Show")
        {
            world.Host.WindowSetup = w =>
                w.OnShow = () => throw new InvalidOperationException("injected throw at Show");
        }
        else
        {
            world.Host.ThrowOnceAt = step;
        }

        world.Coordinator.StartForTest();
        world.Pump();

        Check.WithCustomMessage($"{step}: ProbeOpen must be cleared")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.WithCustomMessage($"{step}: the ring must be shown iff the CursorVM says so")
            .That(world.Host.RingVisible).IsEqualTo(ringVisible);
        Check.WithCustomMessage($"{step}: the keyboard must still be visible (review 05 F2)")
            .That(world.Host.KeyboardVisible).IsTrue();
        Check.WithCustomMessage($"{step}: the summon must be back to its pre-run state")
            .That(world.Host.SummonSuspended).IsFalse();
        Check.WithCustomMessage($"{step}: the toolbar must be reasserted exactly once")
            .That(world.Host.CountOf("ReassertToolbarVisibility")).IsEqualTo(1);
        Check.WithCustomMessage($"{step}: no window may be left shown")
            .That(world.Host.Windows).ContainsOnlyElementsThatMatch(w => !w.IsShown);
        Check.WithCustomMessage($"{step}: the session slot must be cleared")
            .That(world.Coordinator.LiveSessionForTest).IsNull();

        world.ReleaseSeededKeyboardClaim();
        Check.WithCustomMessage(
                $"{step}: a probe suppression claim survived Restore — releasing the seeded keyboard "
                + "claim still leaves fixation suppressed")
            .That(world.Engine.SuppressFixation).IsFalse();
    }

    // ---- W1 (b): RestoreAfterProbe throws; the recorded order and the later steps -----------------

    [Fact]
    public void WhenKeyboardRestoreThrows_TheRecordedOrderHolds_AndLaterStepsStillRun()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();
        world.Host.ClearLog();
        world.Host.ThrowOnceAt = "RestoreKeyboard";

        world.Coordinator.Restore(session);

        Check.WithCustomMessage(
                "Restore's order is close -> teardown -> suppression -> ProbeOpen -> summon -> "
                + "keyboard -> toolbar -> ring, and a throwing keyboard step must not skip the rest.")
            .That(world.Host.Names).ContainsExactly(
                "WindowClosed", "DisposeViewModel",
                "ReleaseProbeSuppression", "SetProbeOpen", "ResumeSummon",
                "RestoreKeyboard", "ReassertToolbarVisibility", "ShowRingIfVisible");

        world.ReleaseSeededKeyboardClaim();
        Check.That(world.Engine.SuppressFixation).IsFalse();
    }

    // ---- W1 (c1): the capture re-enters EndTest ---------------------------------------------------

    [Fact]
    public void WhenCaptureReenters_C5AndC6AreNeverConstructed()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.ReenterOnceAt("CaptureBackdrop",
            () => world.Coordinator.Restore(world.Session));

        world.Coordinator.StartForTest();
        world.Pump();

        Check.WithCustomMessage("The after-capture check must stop the show sequence")
            .That(world.Host.CountOf("CreateViewModel")).IsEqualTo(0);
        Check.That(world.Host.CountOf("CreateWindow")).IsEqualTo(0);
        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Timers.Last!.Disposed).IsTrue();

        world.ReleaseSeededKeyboardClaim();
        Check.That(world.Engine.SuppressFixation).IsFalse();
    }

    // ---- W1 (c2): the C5 / C6 construction re-enters EndTest -------------------------------------

    [Fact]
    public void WhenViewModelConstructionReenters_C5IsDisposed_AndC6NeverConstructed()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.ReenterOnceAt("CreateViewModel",
            () => world.Coordinator.Restore(world.Session));

        world.Coordinator.StartForTest();
        world.Pump();

        Check.WithCustomMessage("The after-construct check must tear the leaked view model down")
            .That(world.Host.CountOf("DisposeViewModel")).IsEqualTo(1);
        Check.WithCustomMessage("The window must never be constructed for an aborted run")
            .That(world.Host.CountOf("CreateWindow")).IsEqualTo(0);
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void WhenWindowConstructionReenters_C6IsClosedAndNeverShown_AndC5IsDisposed()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.ReenterOnceAt("CreateWindow",
            () => world.Coordinator.Restore(world.Session));

        world.Coordinator.StartForTest();
        world.Pump();

        Check.That(world.Host.Windows).HasSize(1);
        Check.WithCustomMessage("The after-construct branch must close the window")
            .That(world.Host.Windows[0].ClosedObserved).IsTrue();
        Check.That(world.Host.Windows[0].IsShown).IsFalse();
        Check.WithCustomMessage("The after-construct branch must tear C5 down")
            .That(world.Host.CountOf("DisposeViewModel")).IsEqualTo(1);

        // The branch's own order is teardown-then-close; the catch's Restore would close FIRST.
        // Without this assertion, a missing branch is masked by the NRE path restoring anyway.
        var names = world.Host.Names.ToList();
        Check.WithCustomMessage("The branch did not run: the window was closed before C5 was torn down")
            .That(names.IndexOf("DisposeViewModel")).IsStrictlyLessThan(names.IndexOf("WindowClosed"));
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    // ---- W1 (c3): Show re-enters, the close during Show is swallowed ------------------------------

    [Fact]
    public void WhenShowReenters_AndTheDoubleSwallowsTheCloseDuringShow_TheWindowStillEndsClosed()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.WindowSetup = w =>
        {
            w.SwallowCloseDuringShow = true;
            w.OnShow = () => world.Coordinator.Restore(world.Session);
        };

        world.Coordinator.StartForTest();
        world.Pump();

        Check.That(world.Host.Windows).HasSize(1);
        Check.WithCustomMessage(
                "The after-Show CloseWindow must repair a close swallowed during Show() (review 05 F3)")
            .That(world.Host.Windows[0].ClosedObserved).IsTrue();
        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    // ---- W1 (c4): a BeginStart step re-enters EndTest ---------------------------------------------

    [Theory]
    [InlineData("HideKeyboard")]
    [InlineData("HideRing")]
    public void WhenAStartStepReenters_NoLaterStepRuns_AndNothingStaysHeld(string step)
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.ReenterOnceAt(step, () => world.Coordinator.Restore(world.Session));

        world.Coordinator.StartForTest();
        world.Pump();

        Check.WithCustomMessage($"{step}: step 6 must not run after the re-entrant end")
            .That(world.Host.CountOf("SuspendSummon")).IsEqualTo(0);
        Check.That(world.Host.SummonSuspended).IsFalse();
        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("CreateWindow")).IsEqualTo(0);

        world.ReleaseSeededKeyboardClaim();
        Check.That(world.Engine.SuppressFixation).IsFalse();
    }

    // ---- W1 (d): a late window close from run 1 must not end run 2 --------------------------------

    [Fact]
    public void ALateCloseFromRun1_DoesNotEndRun2_AndRun2RestoresFully()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);

        // Run 1's window swallows its Close, so its Closed event is still pending when run 2 starts
        // (the "fires late" of W1 (d)); run 2's window behaves normally.
        int windowIndex = 0;
        world.Host.WindowSetup = w =>
        {
            if (windowIndex++ == 0) w.SwallowAllCloses = true;
        };

        world.StartShown();
        var window1 = world.Host.Windows[0];
        world.Coordinator.Restore(world.Session);
        Check.That(window1.ClosedObserved).IsFalse();   // the late event is still to come

        var session2 = world.StartShown();
        var window2 = world.Host.Windows[1];

        // Run 1's C6 Closed fires late, during run 2.
        window1.FireClosed();

        Check.WithCustomMessage("Run 1's late close ended run 2 (the callback must capture its own session)")
            .That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Coordinator.LiveSessionForTest).IsSameReferenceAs(session2);
        Check.That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(world.Vms[1])).IsTrue();

        world.Coordinator.Restore(session2);

        Check.WithCustomMessage("Run 2's restore was skipped (Restored must live on the session)")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(window2.ClosedObserved).IsTrue();
        Check.That(world.Host.RingVisible).IsTrue();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    // ---- W1 (e1): the keyboard holds the summon; HideForProbe throws ------------------------------

    [Fact]
    public void WhenTheKeyboardHoldsTheSummon_AndHideThrows_NoSummonResumeIsRecorded()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.KeyboardVisible = true;
        world.Host.SummonSuspended = true;   // the keyboard's own showing suspended the summon
        world.Host.ThrowOnceAt = "HideKeyboard";

        world.Coordinator.StartForTest();
        world.Pump();

        Check.WithCustomMessage(
                "The summon was not suspended by the probe, so Restore must not resume it "
                + "(review 05 F2; only the call log can tell)")
            .That(world.Host.Names).Not.Contains("ResumeSummon");
        Check.That(world.Host.SummonSuspended).IsTrue();
        Check.That(world.Host.KeyboardVisible).IsTrue();   // HideForProbe threw before hiding
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    // ---- W1 (e2): the fake window's Close re-enters EndTest ---------------------------------------

    [Fact]
    public void WhenTheWindowCloseReenters_RestoreReleasesAndTearsDownExactlyOnce()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();
        world.Host.ClearLog();

        world.Coordinator.Restore(session);

        // The window's own Closed -> EndTest re-entry is the production path (review 05 F4).
        Check.WithCustomMessage("A double release was recorded (the Restored guard failed)")
            .That(world.Host.CountOf("ReleaseProbeSuppression")).IsEqualTo(1);
        Check.WithCustomMessage("A second teardown ran (idempotence failed)")
            .That(world.Host.CountOf("DisposeViewModel")).IsEqualTo(1);
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    // ---- W1 (f): Restore from a worker thread marshals first --------------------------------------

    [Fact]
    public void RestoreFromAWorkerThread_ChangesNothingUntilTheDispatcherRuns()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();
        world.Host.ClearLog();

        var worker = new Thread(() => world.Coordinator.Restore(session));
        worker.Start();
        worker.Join();

        Check.WithCustomMessage("Restore touched the app before marshalling to the UI thread")
            .That(world.Host.Log).IsEmpty();
        Check.That(world.Modal.ProbeOpen).IsTrue();

        world.Pump();

        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.WithCustomMessage("Every host call of a marshalled Restore must run on the UI thread")
            .That(world.Host.Log).ContainsOnlyElementsThatMatch(c => c.CheckAccess);
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    // ---- W1 (g): teardown after Restore, including on the abort branches --------------------------

    [Fact]
    public void AfterRestore_TheTimerIsStopped_TheHandlerRemoved_AndMessagesReachNoC5()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();
        var vm = session.Vm!;
        var timer = world.Timers.Last!;
        Check.That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(vm)).IsTrue();

        world.Coordinator.Restore(session);

        Check.WithCustomMessage("The watchdog timer must be disposed").That(timer.Disposed).IsTrue();
        Check.WithCustomMessage("C5 must no longer receive gaze messages")
            .That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(vm)).IsFalse();

        long before = session.LastValidMs;
        world.Now += 5_000;
        world.Provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        Check.WithCustomMessage("The GazeSampled handler must be detached")
            .That(session.LastValidMs).IsEqualTo(before);
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    [Fact]
    public void TheAbortBranchesOfTheShowSequence_AlsoTearDownTheTimerAndUnregisterC5()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Host.ReenterOnceAt("CreateViewModel",
            () => world.Coordinator.Restore(world.Session));

        world.Coordinator.StartForTest();
        world.Pump();

        Check.That(world.Timers.Last!.Disposed).IsTrue();
        Check.That(world.Host.VmsSeen).HasSize(1);
        Check.That(world.Messenger.IsRegistered<GazeCursorMovedMessage>(world.Host.VmsSeen[0])).IsFalse();
    }
}
