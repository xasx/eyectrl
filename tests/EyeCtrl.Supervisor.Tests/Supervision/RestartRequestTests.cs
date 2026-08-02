namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// A user-requested restart is a clean exit that is followed by a launch. It must not
/// look like a crash in any respect: no strike, no consumed crash budget, no step
/// toward the fallback-AT escalation.
/// </summary>
public class RestartRequestTests
{
    private static readonly DateTime T0 = new(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc);

    private static SupervisionStateMachine Supervising()
    {
        var sm = NewMachine();
        sm.Handle(new AdoptRequest(T0));
        return sm;
    }

    [Fact]
    public void Restart_AfterExit_GoesLaunching_AndCommandsLaunchApp()
    {
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        Check.That(sm.State).IsEqualTo(SupervisorState.CleanExitPending);

        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Restart_BooksNoStrike()
    {
        // The whole point: restarting to apply a setting must not spend crash budget,
        // or a few theme changes plus one real crash would escalate to the fallback AT.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.ConsecutiveFailures).IsEqualTo(0);
    }

    [Fact]
    public void PlainCleanExit_StillGoesIdleWatch_WithNoLaunch()
    {
        // Regression guard on the existing contract — the restart branch must be opt-in.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Restart_FromAnUnverifiedClient_IsIgnored()
    {
        // Same rule as clean exit: an untrusted sender must not be able to drive the
        // supervised process's lifecycle.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: false, RestartRequested: true));

        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);

        sm.Handle(new ProcessExited(T0.AddSeconds(6)));

        // Unannounced exit from Supervising -> recovery, exactly as if no request had come.
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(1);
    }

    [Fact]
    public void Restart_ThatNeverExits_IsForceKilledAndRecovered()
    {
        // The app claimed it was restarting and then hung. The existing 5s deadline
        // must still apply — a restart is not a licence to stop watching.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new DeadlineElapsed(T0.AddSeconds(11)));

        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void RestartIntent_DoesNotSurviveIntoTheNextCleanExit()
    {
        // The flag must be per-request. A restart, then later a genuine quit, must
        // leave the supervisor idle rather than relaunching an app the user closed.
        var sm = Supervising();
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6)));       // -> Launching
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(20)));  // -> Supervising

        sm.Handle(new CleanExitRequested(T0.AddSeconds(30), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(31)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    private static SupervisionStateMachine NewMachine(int maxRetries = 3, int windowSeconds = 120)
        => new(maxRetries, windowSeconds);
}
