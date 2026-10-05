namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;

public class RollingFaultBudgetTests
{
    [Fact]
    public void TryConsume_AllowsExactlyMaxFaults_ThenRefuses()
    {
        var budget = new RollingFaultBudget(3, TimeSpan.FromSeconds(60), nowMs: () => 0);

        Check.That(budget.TryConsume()).IsTrue();  // 1
        Check.That(budget.TryConsume()).IsTrue();  // 2
        Check.That(budget.TryConsume()).IsTrue();  // 3 — the budget boundary itself
        Check.That(budget.TryConsume()).IsFalse(); // 4th inside the window: exhausted
        Check.That(budget.TryConsume()).IsFalse(); // and it stays exhausted
    }

    [Fact]
    public void TryConsume_FaultsExpire_OnlyAfterStrictlyMoreThanTheWindow()
    {
        long now = 0;
        var budget = new RollingFaultBudget(1, TimeSpan.FromSeconds(60), nowMs: () => now);

        Check.That(budget.TryConsume()).IsTrue();

        now = 60_000; // exactly one window later: the old fault still counts
        Check.That(budget.TryConsume()).IsFalse();

        now = 60_001; // strictly past: the old fault expired
        Check.That(budget.TryConsume()).IsTrue();
    }

    [Fact]
    public void TryConsume_RecoversAfterAQuietWindow()
    {
        // The point of the budget: a burst of transient faults must not permanently poison the
        // process — after the window passes with no new faults, containment resumes.
        long now = 0;
        var budget = new RollingFaultBudget(2, TimeSpan.FromSeconds(60), nowMs: () => now);

        Check.That(budget.TryConsume()).IsTrue();
        Check.That(budget.TryConsume()).IsTrue();
        Check.That(budget.TryConsume()).IsFalse();

        now = 120_000;
        Check.That(budget.TryConsume()).IsTrue();
    }
}

public class DispatcherCrashPolicyTests
{
    [Fact]
    public void SupervisedAndPresent_AlwaysDies_EvenWithAFreshBudget()
    {
        // The crash-to-die contract: with supervision enabled AND a Supervisor actually answering,
        // its respawn owns recovery — the dispatcher fault must NEVER be contained.
        var budget = new RollingFaultBudget(10, TimeSpan.FromSeconds(60), nowMs: () => 0);

        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: true, supervisorPresent: true, budget)).IsFalse();
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: true, supervisorPresent: true, budget)).IsFalse();
    }

    [Fact]
    public void SupervisedButAbsent_UsesTheStandaloneBudget_ThenDies()
    {
        // Ticket 07: enabled on disk with nobody answering (the post-reboot world) must NOT die
        // for a respawn that will not come — the standalone budget contains the fault instead. A
        // hard-looping UI still dies at exhaustion rather than wedging input invisibly.
        var budget = new RollingFaultBudget(2, TimeSpan.FromSeconds(60), nowMs: () => 0);

        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: true, supervisorPresent: false, budget)).IsTrue();
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: true, supervisorPresent: false, budget)).IsTrue();
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: true, supervisorPresent: false, budget)).IsFalse();
    }

    [Fact]
    public void Standalone_KeepsRunningWhileTheBudgetLasts_ThenDies()
    {
        var budget = new RollingFaultBudget(2, TimeSpan.FromSeconds(60), nowMs: () => 0);

        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: false, supervisorPresent: false, budget)).IsTrue();
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: false, supervisorPresent: false, budget)).IsTrue();
        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: false, supervisorPresent: false, budget)).IsFalse();
    }

    [Fact]
    public void DisabledEvenWithAPresentSupervisor_UsesTheStandaloneBudget()
    {
        // Ticket 07: the on-disk flag is the decision. A Supervisor that is still answering while
        // supervision is OFF (standing down, or a stale delivery just inside the window) must not
        // re-arm kill-for-respawn behind the user's back.
        var budget = new RollingFaultBudget(1, TimeSpan.FromSeconds(60), nowMs: () => 0);

        Check.That(DispatcherCrashPolicy.ShouldKeepRunning(
            supervisionEnabled: false, supervisorPresent: true, budget)).IsTrue();
    }
}
