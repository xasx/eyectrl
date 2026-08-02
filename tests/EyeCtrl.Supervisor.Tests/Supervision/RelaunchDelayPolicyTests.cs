namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class RelaunchDelayPolicyTests
{
    [Fact]
    public void RestartOriginatedLaunch_IsImmediate()
    {
        // CleanExitPending -> Launching is Task 2's restart branch: the ONLY path that
        // reaches LaunchApp without booking a strike. The user is waiting for their only
        // UI to come back; a crash-loop brake is the wrong instrument.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.Launching, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void HungRestartForceKill_KeepsItsBackoff()
    {
        // THE case that makes the state PAIR necessary. The app announced a restart and
        // then failed to exit inside the 5s deadline, so DeadlineElapsed force-killed it
        // (SupervisionStateMachine.cs:135-137) and GoRecovering emitted ForceKill +
        // LaunchApp (:153-154) — with prevState still CleanExitPending. That is a real
        // crash recovery: it books a strike, and stripping its backoff would produce the
        // tight relaunch loop backoff exists to prevent.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.Recovering, TimeSpan.FromSeconds(15)))
             .IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void HungRestartThatEscalates_KeepsItsBackoff()
    {
        // Same transition, but the strike tripped the crash window so GoRecovering went
        // to FallenBack instead of Recovering. Still not a restart.
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.CleanExitPending, SupervisorState.FallenBack, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void RecoveryLaunch_KeepsItsBackoff()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.Supervising, SupervisorState.Recovering, TimeSpan.FromSeconds(15)))
             .IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void InitialLogonLaunch_KeepsItsZeroDelay()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.IdleWatch, SupervisorState.Launching, TimeSpan.Zero))
             .IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void FallenBackRelaunch_KeepsItsBackoff()
    {
        Check.That(RelaunchDelayPolicy.For(
                SupervisorState.FallenBack, SupervisorState.FallenBack, TimeSpan.FromSeconds(60)))
             .IsEqualTo(TimeSpan.FromSeconds(60));
    }
}
