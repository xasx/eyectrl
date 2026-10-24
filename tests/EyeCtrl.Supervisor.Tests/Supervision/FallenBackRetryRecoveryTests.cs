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
/// the composition-root steps in the same order as <c>Program</c>: RetryNow launches and rebinds
/// first, then feeds <see cref="FallenBackRetry"/>; HandleInput vets every exit with the production
/// <see cref="ProcessExitIntake"/> (foreign pid or a repeat for the same binding is dropped).
/// </summary>
public class FallenBackRetryRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
    private const int FirstPid = 100;
    private const int RetryPid = 200;

    private sealed class FakeHandle(int id) : IProcessHandle
    {
        public int Id => id;
        public bool HasExited => true;
        public string? ExePath => null;
        public DateTime? StartTime => null;
        public event Action? Exited { add { } remove { } }
        public void Kill(bool entireProcessTree) { }
        public void Dispose() { }
    }

    private sealed class Supervisor
    {
        public SupervisionStateMachine Machine { get; } = new(maxRetries: 3, windowSeconds: 300);
        public int DialogsOpened { get; private set; }

        private readonly ProcessExitIntake _intake = new();
        private IProcessHandle? _bound = new FakeHandle(FirstPid);
        private int _nextPid = FirstPid + 1;

        public void Handle(SupervisionInput input)
        {
            // Program.HandleInput's exit intake.
            if (input is ProcessExited exit && !_intake.Accepts(exit, _bound)) return;
            Machine.Handle(input);
            if (Machine.State == SupervisorState.FallenBack &&
                Machine.Commands.Contains(SupervisorCommand.LaunchFallbackAt))
            {
                DialogsOpened++;
            }
        }

        /// <summary>Four crashes, each on the then-bound instance (4 strikes, maxRetries 3).</summary>
        public void Escalate()
        {
            Handle(new LaunchRequest(T0));
            for (var i = 1; i <= 4; i++)
            {
                Handle(new ProcessExited(T0.AddSeconds(i), _bound!.Id));
                if (Machine.State is SupervisorState.Recovering or SupervisorState.Launching)
                    _bound = new FakeHandle(_nextPid++); // the recovery relaunch binds a new process
            }

            Check.That(Machine.State).IsEqualTo(SupervisorState.FallenBack);
            Check.That(Machine.ConsecutiveFailures).IsEqualTo(4);
        }

        /// <summary>RetryNow's order: launch the new instance and bind it, then feed the retry.</summary>
        public void Retry(DateTime at)
        {
            _bound = new FakeHandle(RetryPid);
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
        sup.Handle(new ProcessExited(T0.AddSeconds(72), FirstPid));

        Check.WithCustomMessage("a previous instance's late exit must not strike the retry")
            .That(sup.Machine.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sup.Machine.ConsecutiveFailures).IsEqualTo(4);
        Check.That(sup.Machine.Commands).Not.Contains(SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void ASingleExitReportedByBothFeeders_CountsOnce_AndShowsOneDialog()
    {
        // A7 fixup-02/D1: if the retried process dies between Bind and WireProcessExited, the
        // controller callback and the wire-time guard both report the same pid. The second report
        // must not book another strike or open a second, identical dialog.
        var sup = new Supervisor();
        sup.Escalate();
        Check.That(sup.DialogsOpened).IsEqualTo(1);
        sup.Retry(T0.AddSeconds(70));

        sup.Handle(new ProcessExited(T0.AddSeconds(72), RetryPid)); // OnBoundProcessExited
        sup.Handle(new ProcessExited(T0.AddSeconds(72), RetryPid)); // WireTimeExit, same pid

        Check.WithCustomMessage("the duplicate must not book a second strike")
            .That(sup.Machine.ConsecutiveFailures).IsEqualTo(5);
        Check.WithCustomMessage("the dialog must be re-shown once, not twice")
            .That(sup.DialogsOpened).IsEqualTo(2);
        Check.That(sup.Machine.State).IsEqualTo(SupervisorState.FallenBack);
    }
}
