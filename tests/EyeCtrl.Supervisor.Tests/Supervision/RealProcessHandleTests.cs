namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using System.IO;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// <see cref="RealProcessHandle"/> against a REAL slow child process (a <c>cmd /c ping</c> that
/// would run 30 s, always killed in bounded-wait finally blocks) — the Exited event the whole
/// crash-detection chain stands on. <see cref="ProcessControllerTests"/> exercises
/// <see cref="ProcessController"/> through fake <see cref="IProcessHandle"/>s, so nothing else
/// observes the real wiring: if the EnableRaisingEvents-before-Exited-subscribe ordering or the
/// event forwarding around it regresses, a real app crash never raises <see cref="IProcessHandle.Exited"/>,
/// the supervisor never books a strike, never escalates to the fallback AT, and the gaze-only user
/// is left with a dead app and no recovery. The non-throwing ExePath/StartTime are also what keep
/// <see cref="ProcessController.Adopt"/> from tearing down the supervisor's dispatcher.
/// </summary>
public class RealProcessHandleTests
{
    /// <summary>Slow child whose only job is to stay alive until killed: 30 ping rounds ≈ 30 s.</summary>
    private static (RealProcessHandle Handle, int Pid) NewSlowChild()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c ping -n 30 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi);
        Check.That(process).IsNotNull();
        return (new RealProcessHandle(process!), process!.Id);
    }

    /// <summary>Id, HasExited, ExePath and StartTime must all be readable from a live binding —
    /// these are exactly the properties ProcessController.Adopt copies without guarding.</summary>
    [Fact]
    public void LiveChild_ReportsIdHasExitedExePathAndStartTime()
    {
        var (handle, pid) = NewSlowChild();
        try
        {
            Check.That(handle.Id).IsEqualTo(pid);
            Check.That(handle.HasExited).IsFalse();
            // MainModule can still throw for a process that is only milliseconds old (image not
            // yet fully mapped — seen under parallel test load), which the handle degrades to
            // null. Give the child a bounded window to become queryable, then require the path:
            // a LIVE, settled process must report it.
            var exePath = WaitUntil(() => handle.ExePath, path => path != null, TimeSpan.FromSeconds(10));
            Check.That(exePath).IsNotNull();
            Check.That(handle.StartTime).IsNotNull();
            // NFluent has no DateTime comparator — assert the relation directly (conventions).
            Check.That(handle.StartTime!.Value <= DateTime.UtcNow.AddMinutes(1)).IsTrue();
        }
        finally
        {
            try { handle.Kill(entireProcessTree: true); } catch { }
            try { handle.Dispose(); } catch { }
        }
    }

    /// <summary>Bounded poll for a value expected to reach <paramref name="condition"/>; returns
    /// the last observed value so the caller's assert names the real outcome.</summary>
    private static T? WaitUntil<T>(Func<T?> read, Func<T?, bool> condition, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        var value = read();
        while (!condition(value) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            value = read();
        }
        return value;
    }

    /// <summary>Backstop for the finally blocks: if a test body throws before its own Kill runs,
    /// the child must not outlive the test — kill it out of band, tolerating an already-exited
    /// process (review r1 F9: Kill sat inside the try with only Dispose in finally).</summary>
    private static void KillLeftover(int pid)
    {
        try
        {
            using var leftover = Process.GetProcessById(pid);
            if (!leftover.HasExited)
                leftover.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    /// <summary>Kill(entireProcessTree:true) must raise Exited (on the pool, via
    /// EnableRaisingEvents) and flip HasExited. If Exited never fires, a real crash is never
    /// booked as a strike and recovery never happens.</summary>
    [Fact]
    public async Task Kill_RaisesExited_AndFlipsHasExited()
    {
        var (handle, pid) = NewSlowChild();
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.Exited += () => exited.TrySetResult(true);
        try
        {
            handle.Kill(entireProcessTree: true);

            var completed = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Check.WithCustomMessage("the Exited event must fire after Kill.")
                .That(completed == exited.Task).IsTrue();
            Check.That(handle.HasExited).IsTrue();
        }
        finally
        {
            KillLeftover(pid);
            try { handle.Dispose(); } catch { }
        }
    }

    /// <summary>After exit, reading ExePath must degrade to null instead of throwing: MainModule
    /// throws on an exited process (and on bitness/access mismatches), and the guard here is what
    /// keeps ProcessController.Adopt — called from a Dispatcher callback — non-throwing. Right
    /// after Kill the OS may still serve the module list for a few ms, so the assertion is that
    /// the path DEGRADES to null within a bounded window — and that reading it never throws.</summary>
    [Fact]
    public void ExePath_AfterExit_DegradesToNullInsteadOfThrowing()
    {
        var (handle, pid) = NewSlowChild();
        try
        {
            handle.Kill(entireProcessTree: true);
            Check.That(handle.HasExited).IsTrue();

            string? afterExit = "unset";
            var degraded = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                afterExit = handle.ExePath; // the call itself must never throw
                if (afterExit == null) { degraded = true; break; }
                Thread.Sleep(50);
            }

            Check.WithCustomMessage($"ExePath still reported {afterExit} 10 s after exit.")
                .That(degraded).IsTrue();
            Check.That(afterExit).IsNull();
        }
        finally
        {
            KillLeftover(pid);
            try { handle.Dispose(); } catch { }
        }
    }

    /// <summary>Dispose must be idempotent, and a handle disposed while its child still runs must
    /// not fire Exited when that child later exits out of band — a late callback would book a
    /// strike for a process the supervisor already released.</summary>
    [Fact]
    public void Dispose_IsIdempotent_AndALaterExitFiresNoExitedCallback()
    {
        var (handle, pid) = NewSlowChild();
        var exitedCount = 0;
        handle.Exited += () => Interlocked.Increment(ref exitedCount);
        try
        {
            handle.Dispose();
            handle.Dispose(); // second call must not throw

            // The child is still alive (Dispose releases handles, it does not kill): kill it out
            // of band and let it REALLY exit, then verify no callback fired.
            using var killer = Process.GetProcessById(pid);
            killer.Kill(entireProcessTree: true);
            Check.That(killer.WaitForExit(15_000)).IsTrue();

            // A still-attached Exited would land on the pool almost immediately after exit; give
            // it that window. The guard is the delivery assertion below, not the wait itself.
            Thread.Sleep(300);
            Check.That(exitedCount).IsEqualTo(0);
        }
        finally
        {
            KillLeftover(pid);
            try { handle.Dispose(); } catch { }
        }
    }
}
