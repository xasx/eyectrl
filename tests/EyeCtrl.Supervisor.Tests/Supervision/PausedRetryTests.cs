namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class PausedRetryTests
{
    private static readonly DateTime T0 = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PausedApp_WithFreshHeartbeats_StaysSupervising()
    {
        // A paused app (isPaused=true) still sends heartbeats with fresh lastFrameUtc
        // (pause gates SendInput, not the gaze stream). It must NOT be considered hung.
        var sm = new SupervisionStateMachine(3, 120, 20, 60, 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1))); // -> Supervising
        // Repeated fresh heartbeats (paused or not) keep it Supervising — no HeartbeatStale input fires.
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(4)));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(7)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void PausedApp_StaleFrames_StillTreatedAsDead()
    {
        // Even a paused app whose gaze stream ACTUALLY stops (frames stale) must be killed —
        // pause gates SendInput, not the gaze stream. The supervisor's HeartbeatStale/GazeStreamDead
        // inputs (driven by HeartbeatFreshnessEvaluator on the heartbeat's lastFrameUtc, NOT on isPaused)
        // still fire for a paused app with stale frames.
        var sm = new SupervisionStateMachine(3, 120, 20, 60, 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new GazeStreamDead(T0.AddSeconds(30))); // frames stale while connected
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }
}
