namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// A7 fixup-01/S1 (holistic re-check 01, probe HP8): a retried EyeCtrl that dies or hangs before
/// its first heartbeat must not strand the owner. After <see cref="FallenBackRetry"/> the machine
/// is LAUNCHING — not IdleWatch — so the existing arms cover the retried app's whole startup.
/// Each test starts from FallenBack on the REAL <see cref="SupervisionStateMachine"/> and models
/// the two composition-root steps in the same order as <c>Program</c>: RetryNow launches and
/// rebinds first, then feeds <see cref="FallenBackRetry"/>; HandleInput drops an exit whose tagged
/// pid is no longer the bound one (the production <see cref="ProcessExited.Matches"/>).
/// </summary>
public class FallenBackRetryRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
    private const int OldPid = 100;
    private const int RetryPid = 200;

    private sealed class Supervisor
    {
        public SupervisionStateMachine Machine { get; } = new(maxRetries: 3, windowSeconds: 300);
        public int? BoundPid { get; set; } = OldPid;

        public void Handle(SupervisionInput input)
        {
            // Program.HandleInput's bound-pid filter.
            if (input is ProcessExited exit && !exit.Matches(BoundPid)) return;
            Machine.Handle(input);
        }

        /// <summary>Four crashes while the old instance is bound (4 strikes, maxRetries 3).</summary>
        public void Escalate()
        {
            Handle(new LaunchRequest(T0));
            for (var i = 1; i <= 4; i++) Handle(new ProcessExited(T0.AddSeconds(i), OldPid));
            Check.That(Machine.State).IsEqualTo(SupervisorState.FallenBack);
            Check.That(Machine.ConsecutiveFailures).IsEqualTo(4);
        }

        /// <summary>RetryNow's order: launch the new instance and bind it, then feed the retry.</summary>
        public void Retry(DateTime at)
        {
            BoundPid = RetryPid;
            Handle(new FallenBackRetry(at));
            Check.That(Machine.State).IsEqualTo(SupervisorState.Launching);
        }
    }

    [Fact]
    public void AStartupDeath_AfterTheRetry_ReturnsToFallenBack_AndRaisesTheDialogAgain()
    {
        var sup = new Supervisor();
        sup.Escalate();
        sup.Retry(T0.AddSeconds(70));

        sup.Handle(new ProcessExited(T0.AddSeconds(72), RetryPid));

        Check.WithCustomMessage("the retried app's startup death must not be swallowed")
            .That(sup.Machine.State).IsEqualTo(SupervisorState.FallenBack);
        Check.WithCustomMessage("the dialog must reappear")
            .That(sup.Machine.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
        Check.That(sup.Machine.ConsecutiveFailures).IsEqualTo(5);
    }

    [Fact]
    public void AStartupHang_AfterTheRetry_IsKilled_ThenFallsBack()
    {
        var sup = new Supervisor();
        sup.Escalate();
        sup.Retry(T0.AddSeconds(70));

        var now = T0.AddSeconds(70 + 61);
        Check.WithCustomMessage("the startup grace must be measured from the retry")
            .That(SupervisionClock.StartupGraceElapsed(
                now, sup.Machine.State, sup.Machine.LaunchingSince, startupGraceSeconds: 60))
            .IsTrue();

        sup.Handle(new StartupGraceElapsed(now));

        Check.That(sup.Machine.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sup.Machine.Commands).Contains(SupervisorCommand.ForceKill);
        Check.That(sup.Machine.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void TheFirstHeartbeat_AfterTheRetry_GoesSupervising()
    {
        var sup = new Supervisor();
        sup.Escalate();
        sup.Retry(T0.AddSeconds(70));

        sup.Handle(new HeartbeatReceived(T0.AddSeconds(72)));

        Check.That(sup.Machine.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void ALateExitFromTheOldInstance_IsDropped_AndTheRetryStaysInLaunching()
    {
        var sup = new Supervisor();
        sup.Escalate();
        sup.Retry(T0.AddSeconds(70));

        // The previous instance's Exited callback drains after RetryNow already rebound.
        sup.Handle(new ProcessExited(T0.AddSeconds(72), OldPid));

        Check.WithCustomMessage("a previous instance's late exit must not strike the retry")
            .That(sup.Machine.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sup.Machine.ConsecutiveFailures).IsEqualTo(4);
        Check.That(sup.Machine.Commands).Not.Contains(SupervisorCommand.LaunchFallbackAt);
    }
}
