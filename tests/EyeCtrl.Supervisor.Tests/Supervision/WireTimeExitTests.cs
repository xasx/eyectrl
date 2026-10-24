namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using System.Collections.Generic;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// A7/L3 (A3 review F4): the wire-time race guard must POST <see cref="ProcessExited"/> rather than
/// feed it inline. The path is the composition root's, so — like
/// <see cref="SupervisionLifecycleScenarioTests"/> — the retry's ORDER is modelled exactly as
/// <c>SupervisorApp.RetryNow</c> drives it (launch, wire, close the episode's dialog, feed
/// <see cref="FallenBackRetry"/>) against the REAL <see cref="SupervisionStateMachine"/>; the guard
/// itself is the production <see cref="WireTimeExit"/>.
/// </summary>
public class WireTimeExitTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeProcess(bool hasExited) : IProcessHandle
    {
        public int Id => 4242;
        public bool HasExited => hasExited;
        public string? ExePath => null;
        public DateTime? StartTime => null;
        public event Action? Exited { add { } remove { } }
        public void Kill(bool entireProcessTree) { }
        public void Dispose() { }
    }

    /// <summary>
    /// The part of <c>SupervisorApp.HandleInput</c> this bug passes through: the machine, the
    /// bound-pid filter, and the FallenBack transition that opens an episode's dialog (and the
    /// retry's close of it). The dialogs are counted, not shown.
    /// </summary>
    private sealed class SupervisorModel
    {
        public SupervisionStateMachine Machine { get; } = new(maxRetries: 3, windowSeconds: 120);
        public DateTime Now { get; set; } = T0;
        public int? BoundPid { get; private set; } = 100;
        public List<int?> FedExitPids { get; } = new();
        public int DialogsOpened { get; private set; }
        public int DialogsOpen { get; private set; }

        public void Handle(SupervisionInput input)
        {
            if (input is ProcessExited exit)
            {
                if (!exit.Matches(BoundPid)) return; // Program.HandleInput's bound-pid filter
                FedExitPids.Add(exit.Pid);
            }

            Machine.Handle(input);
            if (Machine.State == SupervisorState.FallenBack &&
                Machine.Commands.Contains(SupervisorCommand.LaunchFallbackAt))
            {
                DialogsOpened++;
                DialogsOpen++;
            }
        }

        /// <summary>RetryNow's <c>_pc.Launch</c>: the retried instance becomes the bound one.</summary>
        public void Rebind(int pid) => BoundPid = pid;

        /// <summary>RetryNow's <c>dialog.Close()</c> for the episode it is retrying.</summary>
        public void CloseEpisodeDialog() => DialogsOpen--;
    }

    [Fact]
    public void AWireTimeExitOfTheRetry_LandsAfterTheRetry_AndReEscalates()
    {
        // A7/L3 + S1: the retried process already exited at wire time. The exit is POSTED, so the
        // retry's FallenBackRetry (-> Launching) lands first; the posted exit then takes the
        // Launching arm and re-escalates, which is the ordinary startup-death path: the dialog
        // reappears. A synchronous feed would re-escalate while still in FallenBack, before the
        // retry moved the machine, leaving a dialog over a launch that is already dead.
        var sup = new SupervisorModel();
        sup.Handle(new AdoptRequest(T0));                       // IdleWatch -> Supervising
        for (var i = 0; i < 4; i++) sup.Handle(new ProcessExited(sup.Now, sup.BoundPid));
        Check.That(sup.Machine.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sup.DialogsOpened).IsEqualTo(1);
        Check.That(sup.DialogsOpen).IsEqualTo(1);

        var posted = new Queue<Action>();
        // RetryNow's order (Program.cs:707-731): launch and rebind, wire the new process, close
        // this episode's dialog, then feed FallenBackRetry.
        sup.Rebind(4242);
        var pid = WireTimeExit.PostIfExited(
            new FakeProcess(hasExited: true), posted.Enqueue, sup.Handle, () => sup.Now);
        Check.WithCustomMessage("the already-exited process must be detected at wire time")
            .That(pid).IsEqualTo(4242);

        sup.CloseEpisodeDialog();
        sup.Handle(new FallenBackRetry(sup.Now));

        Check.WithCustomMessage("the retry's Launching must land before the exit is handled")
            .That(sup.Machine.State).IsEqualTo(SupervisorState.Launching);
        Check.WithCustomMessage("the wire-time exit must not re-enter the FallenBack transition")
            .That(sup.DialogsOpened).IsEqualTo(1);

        // The dispatcher pump: what BeginInvoke posted runs at Normal, before the next 1 s tick.
        while (posted.Count > 0) posted.Dequeue()();

        Check.WithCustomMessage("the retried app's startup death must re-open the dialog")
            .That(sup.DialogsOpened).IsEqualTo(2);
        Check.That(sup.DialogsOpen).IsEqualTo(1); // episode 1's dialog was closed; this is its re-show
        Check.That(sup.Machine.State).IsEqualTo(SupervisorState.FallenBack);
        Check.WithCustomMessage("the posted exit must carry the bound pid")
            .That(sup.FedExitPids).Contains(4242);
    }

    [Fact]
    public void AProcessThatIsStillRunningAtWireTime_PostsNothing()
    {
        var posted = new Queue<Action>();
        var fed = new List<SupervisionInput>();
        var pid = WireTimeExit.PostIfExited(
            new FakeProcess(hasExited: false), posted.Enqueue, fed.Add, () => T0);

        Check.That(pid).IsNull();
        Check.That(posted.Count).IsEqualTo(0);
        Check.That(fed).IsEmpty();
    }

    [Fact]
    public void AnUnboundProcess_PostsNothing()
    {
        var posted = new Queue<Action>();
        var fed = new List<SupervisionInput>();
        var pid = WireTimeExit.PostIfExited(
            null, posted.Enqueue, fed.Add, () => T0);

        Check.That(pid).IsNull();
        Check.That(posted.Count).IsEqualTo(0);
        Check.That(fed).IsEmpty();
    }
}
