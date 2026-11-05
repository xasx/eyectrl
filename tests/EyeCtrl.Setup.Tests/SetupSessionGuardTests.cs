namespace EyeCtrl.Setup.Tests;

using System.Runtime.ExceptionServices;
using System.Threading;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// C9 S6e fixup-05: one Setup session at a time. The sitting's double launch produced two Q8
/// windows whose dwell target ids collided; the guard is the reason a second window cannot exist.
/// Every mutex name here is GUID-suffixed (AGENTS.md): Windows mutex names are machine-global and
/// this repository runs many test processes in parallel worktrees.
/// </summary>
public sealed class SetupSessionGuardTests
{
    private static readonly SetupPaths Paths =
        new(@"C:\Program Files", @"C:\Users\test\AppData\Roaming", @"C:\Users\test\AppData\Local");

    private static string NewName() => @"Local\eyectrl_setup_guard_test_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FourVerbsAreGuarded_AndNothingElseIs()
    {
        foreach (var job in new[]
                 {
                     SetupJob.Update, SetupJob.Rollback, SetupJob.Uninstall, SetupJob.Repair,
                 })
        {
            Check.WithCustomMessage($"{job} changes the install and must be guarded.").That(SetupSessionGuard.Guards(job)).IsTrue();
        }

        foreach (var job in new[]
                 {
                     SetupJob.Status, SetupJob.Help,
                     SetupJob.InternalPrepareUpdate, SetupJob.InternalApplyUpdate,
                     SetupJob.InternalPrepareRunCopy, SetupJob.InternalApplyRollback,
                     SetupJob.InternalRevert, SetupJob.InternalUninstall, SetupJob.InternalRepair,
                 })
        {
            Check.WithCustomMessage($"{job} must never take the session (read-only, or a child of a guarded verb).")
                .That(SetupSessionGuard.Guards(job)).IsFalse();
        }
    }

    [Fact]
    public void ASecondProcess_CannotTakeASessionTheFirstHolds()
    {
        var name = NewName();
        using var first = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
        Check.That(first).IsNotNull();

        var second = RunOnThread(() => SetupSessionGuard.TryEnter(TimeSpan.Zero, name));
        Check.WithCustomMessage("A second process took the session while the first held it.")
            .That(second).IsNull();

        first!.Dispose();

        var third = RunOnThread(() =>
        {
            using var session = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
            return session is not null;
        });
        Check.WithCustomMessage("The session did not become free after the holder released it.")
            .That(third).IsTrue();
    }

    [Fact]
    public void TheAppProbe_SeesTheLiveSession_AndClearsWhenItEnds()
    {
        var name = NewName();
        Check.That(SetupSessionGuard.IsSessionLive(name)).IsFalse();

        var session = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
        Check.That(session).IsNotNull();
        Check.WithCustomMessage("The hand-off probe did not see the held session.").That(SetupSessionGuard.IsSessionLive(name)).IsTrue();

        session!.Dispose();
        Check.WithCustomMessage("The hand-off probe still reports a session after it ended.").That(SetupSessionGuard.IsSessionLive(name)).IsFalse();
    }

    [Fact]
    public void ARunCopy_WaitsForItsLauncher_ThenTakesOver()
    {
        var name = NewName();
        using var launcher = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
        Check.That(launcher).IsNotNull();

        var takeover = false;
        ExceptionDispatchInfo? failure = null;
        var child = new Thread(() =>
        {
            try
            {
                using var session = SetupSessionGuard.TryEnter(SetupSessionGuard.HandoffTakeoverWait, name);
                takeover = session is not null;
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };
        child.Start();

        // The child is blocked while the parent still holds the session (the real hand-off starts
        // the run copy a few milliseconds before the parent returns from Run).
        Thread.Sleep(150);
        Check.WithCustomMessage("The run copy did not wait for its launcher; it took the session at once.")
            .That(child.IsAlive).IsTrue();

        launcher!.Dispose();
        child.Join();
        failure?.Throw();

        Check.WithCustomMessage("The run copy never took over from its launcher.").That(takeover).IsTrue();
    }

    [Fact]
    public void ARunCopyWaits_ButEveryOtherStartRefusesImmediately()
    {
        var uninstall = SetupCommandLine.Parse(["uninstall"]);
        var status = SetupCommandLine.Parse(["status"]);
        var runCopy = Paths.RunSetupExePath("abc123");
        var installed = Paths.InstalledSetupExePath;

        Check.That(SetupSessionGuard.AcquisitionWait(uninstall, Paths, runCopy))
            .IsEqualTo(SetupSessionGuard.HandoffTakeoverWait);
        Check.That(SetupSessionGuard.AcquisitionWait(uninstall, Paths, installed))
            .IsEqualTo(TimeSpan.Zero);
        Check.That(SetupSessionGuard.AcquisitionWait(uninstall, Paths, processPath: null))
            .IsEqualTo(TimeSpan.Zero);
        Check.That(SetupSessionGuard.AcquisitionWait(status, Paths, runCopy))
            .IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void ASecondInstance_ExitsBeforeTheComposition_AndLeavesTheFirstAlone()
    {
        var name = NewName();
        using var first = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
        Check.That(first).IsNotNull();

        var log = new RecordingSetupLog();
        var command = SetupCommandLine.Parse(["uninstall"]);
        var refused = RunOnThread(() =>
        {
            var entered = Program.TryEnterSession(
                command, Paths, Paths.InstalledSetupExePath, log, name, out var session);
            try
            {
                return entered;
            }
            finally
            {
                session?.Dispose();
            }
        });

        Check.WithCustomMessage("A second Setup instance entered the composition while the first held the session.")
            .That(refused).IsFalse();
        Check.That(log.Lines).HasElementThatMatches(line => line.Contains("another Setup session is already running"));
    }

    [Fact]
    public void AnUnguardedVerb_EntersWithoutTheSession()
    {
        // status is read-only: it must never be refused while an update or uninstall runs.
        var log = new RecordingSetupLog();
        var command = SetupCommandLine.Parse(["status"]);

        var entered = Program.TryEnterSession(
            command, Paths, Paths.InstalledSetupExePath, log, NewName(), out var session);

        Check.That(entered).IsTrue();
        Check.That(session).IsNull();
        Check.That(log.Lines).IsEmpty();
    }

    [Fact]
    public void AnAbandonedSession_IsTakenOver_NotLockedOut()
    {
        // A Setup that crashed while holding the mutex must not block every later run.
        var name = NewName();
        var held = RunOnThread(() =>
        {
            var mutex = new Mutex(initiallyOwned: false, name);
            var owned = mutex.WaitOne(TimeSpan.Zero);
            // Return while still owning it: the thread exits, the OS abandons the mutex.
            return (Mutex: mutex, Owned: owned);
        });

        Check.That(held.Owned).IsTrue();

        var takeover = RunOnThread(() =>
        {
            using var session = SetupSessionGuard.TryEnter(TimeSpan.Zero, name);
            return session is not null;
        });

        Check.WithCustomMessage("A mutex abandoned by a crashed process locked the session forever.")
            .That(takeover).IsTrue();

        GC.KeepAlive(held.Mutex);
    }

    /// <summary>Runs a blocking probe on a plain thread (no thread-pool starvation, no xUnit1031).</summary>
    private static T RunOnThread<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
