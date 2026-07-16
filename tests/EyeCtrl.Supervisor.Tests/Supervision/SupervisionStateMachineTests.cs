namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class SupervisionStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    // Spec: IDLE_WATCH is the entry state, not Launching — so supervised + StartOnBoot-off
    // stays idle instead of launching. Only a LaunchRequest (logon+StartOnBoot, or tray
    // "Launch EyeCtrl now") or an AdoptRequest (manually-launched app's first heartbeat)
    // leaves idle.
    [Fact]
    public void StartsIn_IdleWatch()
    {
        var sm = NewMachine();
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void LaunchRequest_FromIdle_GoesLaunching_CommandsLaunchApp()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Launching_FirstHeartbeat_GoesSupervising()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(10)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void AdoptRequest_FromIdle_GoesSupervising_NoLaunchCommand()
    {
        // Manual-launch adopt path: app we didn't launch sends a heartbeat.
        var sm = NewMachine();
        sm.Handle(new AdoptRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Supervising_HeartbeatStale_GoesRecovering_CommandsForceKill()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new HeartbeatStale(T0.AddSeconds(22)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void CrashLoop_BeyondMaxRetries_GoesFallenBack()
    {
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        for (int i = 1; i <= 4; i++)
            sm.Handle(new ProcessExited(T0.AddSeconds(i))); // strikes 1..4 (> maxRetries 3 -> escalate)
        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void SurvivedWindow_ResetsStrikeAndBackoffCounters()
    {
        // Spec reset rule (no infinite-restart hole): surviving a FULL crash window (120s)
        // in Supervising resets both the strike counter and the backoff counter.
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));   // strike 1, backoff -> 5s
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.FromSeconds(5));
        sm.Handle(new LaunchRequest(T0.AddSeconds(10)));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(11)));
        // Now survive > 120s cleanly:
        sm.Handle(new SurvivedWindow(T0.AddSeconds(200)));
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero); // backoff reset to immediate
        // And a subsequent single crash should not escalate (strikes reset):
        sm.Handle(new ProcessExited(T0.AddSeconds(201)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void CleanExit_AuthenticatedWithinDeadline_GoesIdleWatch_NoRelaunch()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6))); // exits within deadline
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void CleanExit_NotExitedWithinDeadline_GoesRecovering()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new DeadlineElapsed(T0.AddSeconds(11))); // > 5s, still alive
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void UnannouncedProcessExit_GoesRecovering_NotIdle()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new ProcessExited(T0.AddSeconds(5))); // no CleanExitRequested
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void CleanExit_UnverifiedClient_Ignored_DoesNotSuppressRecovery()
    {
        // Spec: an unverified/unbound sender's shuttingDown:true must NOT
        // suppress crash recovery. HeartbeatPipeServer only sets ClientVerified after
        // ClientIdentityBinder succeeds. An unverified request leaves us Supervising.
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: false));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising); // unchanged
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.NotifyCleanExit);
    }

    private static SupervisionStateMachine NewMachine(int maxRetries = 3, int windowSeconds = 120)
        => new(maxRetries, windowSeconds, hangThresholdSeconds: 20, startupGraceSeconds: 60, windowResetSeconds: windowSeconds);
}
