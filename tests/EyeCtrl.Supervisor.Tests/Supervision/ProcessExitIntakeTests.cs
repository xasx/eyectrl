namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// A7 fixup-02: the exit intake. A previous binding's late exit is dropped (fixup-01/S1), and a
/// single exit reported by BOTH feeders — the controller callback and the wire-time guard — is
/// counted once (D1). The behavioural consequence of the duplicate is pinned in
/// <see cref="FallenBackRetryRecoveryTests.ASingleExitReportedByBothFeeders_CountsOnce_AndShowsOneDialog"/>.
/// </summary>
public class ProcessExitIntakeTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void AnExitForTheBoundProcess_IsAcceptedOnce()
    {
        var intake = new ProcessExitIntake();
        var bound = new FakeHandle(42);

        Check.That(intake.Accepts(new ProcessExited(T0, 42), bound)).IsTrue();
        Check.WithCustomMessage("a repeat for the same binding is the same exit")
            .That(intake.Accepts(new ProcessExited(T0.AddMilliseconds(1), 42), bound)).IsFalse();
    }

    [Fact]
    public void AnExitForAnotherProcess_IsDropped_AndDoesNotConsumeTheBoundPid()
    {
        var intake = new ProcessExitIntake();
        var bound = new FakeHandle(42);

        Check.That(intake.Accepts(new ProcessExited(T0, 41), bound)).IsFalse();
        Check.WithCustomMessage("the foreign exit must not shadow the bound process's own exit")
            .That(intake.Accepts(new ProcessExited(T0, 42), bound)).IsTrue();
    }

    [Fact]
    public void ANewBinding_StartsANewExitIdentity_EvenForAReusedPid()
    {
        var intake = new ProcessExitIntake();
        var first = new FakeHandle(42);
        var second = new FakeHandle(42); // Windows reused the pid for the new instance

        Check.That(intake.Accepts(new ProcessExited(T0, 42), first)).IsTrue();
        Check.WithCustomMessage("a new bound handle is a new process, not a duplicate")
            .That(intake.Accepts(new ProcessExited(T0.AddSeconds(1), 42), second)).IsTrue();
    }

    [Fact]
    public void AnUntaggedExit_HasNoIdentity_AndIsAccepted()
    {
        var intake = new ProcessExitIntake();
        var bound = new FakeHandle(42);

        Check.That(intake.Accepts(new ProcessExited(T0), bound)).IsTrue();
        Check.That(intake.Accepts(new ProcessExited(T0.AddMilliseconds(1)), bound)).IsTrue();
    }

    [Fact]
    public void AnUnboundController_DropsATaggedExit()
    {
        var intake = new ProcessExitIntake();

        Check.That(intake.Accepts(new ProcessExited(T0, 42), boundProcess: null)).IsFalse();
    }
}
