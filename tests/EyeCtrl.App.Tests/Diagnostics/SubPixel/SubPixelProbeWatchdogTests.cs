namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Windows.Threading;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// W7 — watchdog and Esc (build plan W7, cases (a)-(g)). Injected <c>nowMs</c> and timer factory;
/// <c>Check(session)</c> is called directly, so nothing here depends on real time.
/// </summary>
public class SubPixelProbeWatchdogTests
{
    // ---- W7 (a): 12 s without a VALID sample, from an invalid stream or from silence -------------

    [Theory]
    [InlineData(true)]   // invalid samples arrive every 30 ms
    [InlineData(false)]  // silence: no samples at all
    public void NoValidSampleFor12s_Aborts(bool invalidStream)
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();

        if (invalidStream)
        {
            for (long t = 0; t <= 12_000; t += 30)
            {
                world.Now = 1_000_000 + t;
                world.Provider.Raise(new GazePoint(500, 500, t, IsValid: false));
            }
        }

        world.Now = 1_000_000 + 12_001;
        world.Coordinator.Check(session);

        Check.WithCustomMessage(
                "Invalid samples must not reset the last-valid backstop; only valid ones may")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
        Check.That(world.Timers.Last!.Disposed).IsTrue();
    }

    // ---- W7 (b): 90 s without phase progress ------------------------------------------------------

    [Fact]
    public void NinetySecondsWithoutProgress_Aborts_AndAPhaseAdvanceResetsTheClock()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var session = world.StartShown();

        // Keep the 12 s last-valid rule quiet while the progress clock runs.
        void RefreshValid(long at)
        {
            world.Now = at;
            world.Provider.Raise(new GazePoint(500, 500, at, IsValid: true));
        }

        RefreshValid(1_000_000 + 50_000);
        session.Vm!.SimulateActivated(SubPixelProbeViewModel.IntroStartTargetId);   // a phase advance

        RefreshValid(1_000_000 + 50_000 + 89_999);
        world.Coordinator.Check(session);
        Check.WithCustomMessage("89,999 ms without progress must not abort")
            .That(world.Modal.ProbeOpen).IsTrue();

        RefreshValid(1_000_000 + 50_000 + 90_001);
        world.Coordinator.Check(session);
        Check.WithCustomMessage("The 90 s no-progress rule is gone")
            .That(world.Modal.ProbeOpen).IsFalse();
    }

    // ---- W7 (c): the 15-minute cap resets at the session-2 boundary -------------------------------

    [Fact]
    public void TheFifteenMinuteCap_IsPerSession_AndResetsWhenSession2Starts()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        var verdict = new SubPixelAbSession1Verdict(
            SubPixelAbOutcome.Convicts, SubPixelAbStaticReadout.Skipped, "session 1 CONVICTS");
        world.Coordinator.StartForTest(SubPixelProbeStartMode.ResumeSession2, verdict);
        world.Pump();
        var session = world.Session;

        void RefreshValid(long at)
        {
            world.Now = at;
            world.Provider.Raise(new GazePoint(500, 500, at, IsValid: true));
            world.Coordinator.MarkProgressForTest(session);
        }

        // Ten minutes in, the owner starts session 2: the cap restarts here.
        world.Now = 1_000_000 + 10 * 60_000;
        session.Vm!.SimulateActivated(SubPixelProbeViewModel.IntroStartTargetId);

        // Sixteen minutes after the run began, six after the boundary: still alive.
        RefreshValid(1_000_000 + 16 * 60_000);
        world.Coordinator.Check(session);
        Check.WithCustomMessage("The cap did not reset at the session-2 boundary")
            .That(world.Modal.ProbeOpen).IsTrue();

        // Sixteen minutes after the boundary: the cap itself fires.
        RefreshValid(1_000_000 + 26 * 60_000);
        world.Coordinator.Check(session);
        Check.WithCustomMessage("The 15-minute cap is gone")
            .That(world.Modal.ProbeOpen).IsFalse();
    }

    // ---- W7 (d): armed by BeginStart, before ProbeOpen, seeded, at Normal priority ----------------

    [Fact]
    public void TheWatchdogIsArmedFirst_SeededWithNow_AtNormalPriority()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Now = 500_000;

        // BeginStart runs; CaptureAndShow is posted at ContextIdle and deliberately NOT pumped.
        world.Coordinator.StartForTest();
        var session = world.Session;

        Check.WithCustomMessage("The watchdog must be created during BeginStart, not at CaptureAndShow")
            .That(world.Timers.Created).HasSize(1);
        Check.WithCustomMessage("The watchdog must run at Normal priority (review 05 E7)")
            .That(world.Timers.Created[0].Priority).IsEqualTo(DispatcherPriority.Normal);
        Check.That(world.Timers.Created[0].Timer.Enabled).IsTrue();
        Check.That(session.StartedMs).IsEqualTo(500_000);
        Check.That(session.LastValidMs).IsEqualTo(500_000);
        Check.That(session.LastProgressMs).IsEqualTo(500_000);
        Check.That(world.Modal.ProbeOpen).IsTrue();
    }

    // ---- W7 (e): the show deadline ----------------------------------------------------------------

    [Fact]
    public void WhenTheWindowNeverShows_CheckAbortsAtTheFiveSecondDeadline()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Now = 500_000;
        world.Coordinator.StartForTest();   // posted CaptureAndShow is never pumped
        var session = world.Session;

        world.Now = 500_000 + 4_999;
        world.Provider.Raise(new GazePoint(500, 500, 0, IsValid: true));   // keep 12 s rule quiet
        world.Coordinator.Check(session);
        Check.WithCustomMessage("The show deadline fired before 5 s")
            .That(world.Modal.ProbeOpen).IsTrue();

        world.Now = 500_000 + 5_001;
        world.Provider.Raise(new GazePoint(500, 500, 0, IsValid: true));
        world.Coordinator.Check(session);

        Check.WithCustomMessage("The 5 s show deadline is gone (a half-started run would strand the owner)")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Timers.Last!.Disposed).IsTrue();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    // ---- W7 (g): Esc, at Fixation, while the probe is open ----------------------------------------

    [Fact]
    public void EscAtFixation_WhileProbeOpen_EndsTheTest()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);
        world.Engine.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        Check.That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Fixation);
        world.StartShown();

        ((TestFakes.FakeEscapeHatch)world.Context.EscapeHatch).Press();
        world.Pump();

        Check.WithCustomMessage(
                "Esc at Fixation did not end the probe (a guard keyed on FreeLook would miss it)")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    [Fact]
    public void EscWithNoProbeOpen_ChangesNothing()
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);

        Check.ThatCode(() => ((TestFakes.FakeEscapeHatch)world.Context.EscapeHatch).Press())
            .DoesNotThrow();
        world.Pump();

        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
        Check.That(world.Host.Log).IsEmpty();
    }

    // ---- M5: plan rules the code follows but no test pinned --------------------------------------

    [Fact]
    public void TheWatchdogIsArmedBeforeProbeOpenIsSet()
    {
        // W1-27: "the watchdog arms first" (review 04 N2). At the instant the first step sets
        // ProbeOpen the timer must already exist.
        using var world = new ProbeTestWorld();
        int timersWhenProbeOpened = -1;
        world.Host.ReenterOnceAt("SetProbeOpen",
            () => timersWhenProbeOpened = world.Timers.Created.Count);

        world.Coordinator.StartForTest();

        Check.WithCustomMessage(
                "ProbeOpen was set before the watchdog was armed (review 04 N2: the half-started "
                + "state must always have an exit)")
            .That(timersWhenProbeOpened).IsEqualTo(1);
    }

    [Fact]
    public void CaptureAndShow_IsPostedAtContextIdle_NotAtSend()
    {
        // W1-33: the show continuation must wait for the ContextIdle pass, so every queued
        // Normal/Background item of the start runs first.
        using var world = new ProbeTestWorld();
        world.Coordinator.StartForTest();

        world.Dispatcher.Invoke(() => { }, DispatcherPriority.Background, CancellationToken.None);
        Check.WithCustomMessage("The window was created before the ContextIdle pass")
            .That(world.Host.CountOf("CreateWindow")).IsEqualTo(0);

        world.Pump();
        Check.That(world.Host.CountOf("CreateWindow")).IsEqualTo(1);
    }

    [Fact]
    public void AnInvalidSample_DoesNotRestoreTheRun()
    {
        // W7-j / plan F8: the watchdog's GazeSampled handler only stamps the last-valid time; it
        // never calls Restore, so an invalid sample can never end a live run.
        using var world = new ProbeTestWorld();
        world.StartShown();

        world.Provider.Raise(new GazePoint(500, 500, 0, IsValid: false));
        world.Pump();

        Check.WithCustomMessage("The GazeSampled handler restored on an invalid sample")
            .That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Coordinator.LiveSessionForTest).IsNotNull();
    }
}
