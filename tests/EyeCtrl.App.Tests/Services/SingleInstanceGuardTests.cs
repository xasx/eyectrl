namespace EyeCtrl.App.Tests.Services;

using System.Diagnostics;
using System.IO;
using System.Threading;
using EyeCtrl.Core.Services;

public class SingleInstanceGuardTests
{
    // NOTE: a named Mutex is thread-reentrant — the owning thread can acquire it again. So the
    // second guard MUST run on a thread that is neither the owner nor a pooled thread that could
    // BE the owner. Task.Run does not guarantee that (the pool can hand the continuation to the
    // very thread that acquired the first guard, which then re-acquires reentrantly → false green
    // locally, flaky red in CI). We use a dedicated Thread and keep the owning thread alive via
    // Join, so the mutex is genuinely contended and never abandoned. Each test uses a unique name.
    private static string UniqueName() => @"Local\EyeCtrl.App.Test." + Guid.NewGuid();

    [Fact]
    public void TryAcquire_SecondThread_Fails_WhenFirstHoldsIt()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue(); // owned by THIS thread; held for the whole test

        // A second guard on a DEDICATED thread (never the owning/pool thread) must fail to acquire.
        bool secondResult = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondResult = second.TryAcquire();
        });
        thread.Start();
        Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue(); // the probe thread finished
        Check.That(secondResult).IsFalse();                        // and it could not acquire
    }

    [Fact]
    public void TryAcquire_AfterDispose_SucceedsAgain()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();
        first.Dispose();

        using var second = new SingleInstanceGuard(name);
        Check.That(second.TryAcquire()).IsTrue();
    }

    /// <summary>
    /// The kernel premise the exit fix rests on, proved against a real named mutex: a live owner
    /// thread that is hung keeps ownership when another thread closes its HANDLE
    /// (<c>CloseHandle</c> does not release ownership), so a waiting process stays blocked; only
    /// <c>ReleaseMutex</c> called on the OWNING thread frees it. That is why the mutex has to be
    /// owned by a keeper thread the exit can still signal, not by the UI thread.
    /// </summary>
    [Fact]
    public void Premise_ClosingTheOwnersHandleDoesNotReleaseTheMutex_OnlyTheOwningThreadsReleaseMutexDoes()
    {
        var name = UniqueName();
        using var acquired = new ManualResetEventSlim(false);
        using var releaseOwner = new ManualResetEventSlim(false);
        using var waiterOpened = new ManualResetEventSlim(false);
        using var waiterAcquired = new ManualResetEventSlim(false);

        Mutex? ownerHandle = null;
        var owner = new Thread(() =>
        {
            ownerHandle = new Mutex(initiallyOwned: true, name, out _);
            acquired.Set();
            releaseOwner.Wait(); // the simulated hung UI thread: it never exits on its own
            using var reopened = Mutex.OpenExisting(name);
            reopened.ReleaseMutex(); // ownership is per THREAD, so this is what frees the waiter
        })
        {
            IsBackground = true,
            Name = "mutex premise owner",
        };

        bool waiterResult = false;
        var waiter = new Thread(() =>
        {
            using var mutex = Mutex.OpenExisting(name);
            waiterOpened.Set();
            waiterResult = mutex.WaitOne(TimeSpan.FromSeconds(10));
            if (waiterResult) mutex.ReleaseMutex(); // same thread: it owns it now
            waiterAcquired.Set();
        })
        {
            IsBackground = true,
            Name = "mutex premise waiter",
        };

        try
        {
            owner.Start();
            Check.That(acquired.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            waiter.Start();
            Check.That(waiterOpened.Wait(TimeSpan.FromSeconds(5))).IsTrue();

            // The owner's HANDLE is gone but its thread is alive: CloseHandle does not release.
            ownerHandle!.Dispose();
            Check.WithCustomMessage("closing the owner's handle released the mutex")
                .That(waiterAcquired.Wait(TimeSpan.FromMilliseconds(500))).IsFalse();

            // ReleaseMutex on the OWNING thread is what frees the waiter.
            releaseOwner.Set();
            Check.That(waiterAcquired.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(waiterResult).IsTrue();
            Check.That(owner.Join(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(waiter.Join(TimeSpan.FromSeconds(5))).IsTrue();
        }
        finally
        {
            releaseOwner.Set();
        }
    }

    /// <summary>
    /// E1-proof (the keeper's reason to exist): the mutex is owned by a dedicated background
    /// keeper thread, and the WATCHDOG path — a bounded release called from a thread that is not
    /// the owner — frees a queued waiter while the startup/UI thread is still blocked forever.
    /// </summary>
    [Fact]
    public void WatchdogRelease_FreesAQueuedWaiter_WhileTheStartupThreadIsStillBlocked()
    {
        string name = UniqueName();
        var guard = new SingleInstanceGuard(name);
        using var uiHung = new ManualResetEventSlim(false);
        using var startupAcquired = new ManualResetEventSlim(false);
        bool startupResult = false;
        var ui = new Thread(() =>
        {
            startupResult = guard.TryAcquire();
            startupAcquired.Set();
            uiHung.Wait(); // the stuck UI thread never returns on its own
        })
        {
            IsBackground = true,
            Name = "simulated stuck UI thread",
        };

        using var waiterOpened = new ManualResetEventSlim(false);
        using var waiterAcquired = new ManualResetEventSlim(false);
        bool waiterResult = false;
        bool waiterSawAbandonment = false;
        var waiter = new Thread(() =>
        {
            using var mutex = Mutex.OpenExisting(name);
            waiterOpened.Set();
            try
            {
                waiterResult = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                // The owner thread died without releasing. Production treats this as acquired, but
                // E1 is about a LIVE hung thread and a graceful ReleaseMutex — an abandonment here
                // means the release never really happened.
                waiterResult = true;
                waiterSawAbandonment = true;
            }

            if (waiterResult) mutex.ReleaseMutex();
            waiterAcquired.Set();
        })
        {
            IsBackground = true,
            Name = "restart replacement waiter",
        };

        try
        {
            ui.Start();
            Check.That(startupAcquired.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(startupResult).IsTrue();

            waiter.Start();
            Check.That(waiterOpened.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            Check.WithCustomMessage("the waiter acquired the mutex while the first instance held it")
                .That(waiterAcquired.Wait(TimeSpan.FromMilliseconds(250))).IsFalse();

            // The watchdog path: signal the keeper with the same bounded wait the exit uses, from
            // a thread that does NOT own the mutex and while the startup thread is hung.
            var elapsed = Stopwatch.StartNew();
            Check.That(guard.Release(SingleInstanceGuard.ReleaseWait)).IsTrue();
            Check.That(waiterAcquired.Wait(TimeSpan.FromSeconds(1))).IsTrue();
            elapsed.Stop();

            Check.That(waiterResult).IsTrue();
            Check.WithCustomMessage("the waiter was freed by abandonment, not by ReleaseMutex")
                .That(waiterSawAbandonment).IsFalse();
            Check.That(uiHung.IsSet).IsFalse(); // proven: the release did not need the hung thread
            Check.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            uiHung.Set();
            guard.Dispose();
            waiter.Join(TimeSpan.FromSeconds(5));
            ui.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// E1-proof: the normal exit releases through the keeper (the last OnExit step), and a waiter
    /// queued behind the instance acquires.
    /// </summary>
    [Fact]
    public void NormalExit_DisposeReleasesThroughTheKeeper_AndAQueuedWaiterAcquires()
    {
        string name = UniqueName();
        var guard = new SingleInstanceGuard(name);
        Check.That(guard.TryAcquire()).IsTrue();

        using var waiterOpened = new ManualResetEventSlim(false);
        using var waiterAcquired = new ManualResetEventSlim(false);
        bool waiterResult = false;
        bool waiterSawAbandonment = false;
        var waiter = new Thread(() =>
        {
            using var mutex = Mutex.OpenExisting(name);
            waiterOpened.Set();
            try
            {
                waiterResult = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                waiterResult = true;
                waiterSawAbandonment = true;
            }

            if (waiterResult) mutex.ReleaseMutex();
            waiterAcquired.Set();
        })
        {
            IsBackground = true,
            Name = "queued restart replacement",
        };

        try
        {
            waiter.Start();
            Check.That(waiterOpened.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(waiterAcquired.Wait(TimeSpan.FromMilliseconds(250))).IsFalse();

            guard.Dispose(); // the exit's last step

            Check.That(waiterAcquired.Wait(TimeSpan.FromSeconds(1))).IsTrue();
            Check.That(waiterResult).IsTrue();
            Check.WithCustomMessage("the waiter was freed by abandonment, not by ReleaseMutex")
                .That(waiterSawAbandonment).IsFalse();
        }
        finally
        {
            guard.Dispose();
            waiter.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// E1-proof: after the keeper's release, a fresh launch (the exact
    /// <c>new Mutex(initiallyOwned: true, name, out createdNew)</c> startup shape) owns the mutex
    /// again. <c>createdNew</c> may be false while handles remain; ownership is the assertion.
    /// </summary>
    [Fact]
    public void AfterTheKeeperReleases_AFreshLaunchOwnsTheMutex()
    {
        string name = UniqueName();
        using (var first = new SingleInstanceGuard(name))
        {
            Check.That(first.TryAcquire()).IsTrue();
            first.Dispose();
        }

        bool owned = false;
        var fresh = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
                // createdNew==true means it owns it outright; otherwise it must still be free.
                owned = createdNew || mutex.WaitOne(TimeSpan.FromSeconds(2));
                if (owned) mutex.ReleaseMutex();
            }
            catch
            {
                owned = false;
            }
        })
        {
            IsBackground = true,
            Name = "fresh launch",
        };

        fresh.Start();
        Check.That(fresh.Join(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(owned).IsTrue();
    }

    /// <summary>
    /// E1-proof: ownership lives on one dedicated BACKGROUND keeper thread — never on the caller's
    /// thread — which is what lets the watchdog release it.
    /// </summary>
    [Fact]
    public void TryAcquire_OwnsTheMutexOnANamedBackgroundKeeperThread_NotTheCallerThread()
    {
        string name = UniqueName();
        using var guard = new SingleInstanceGuard(name);
        Check.That(guard.TryAcquire()).IsTrue();

        Thread? keeper = guard.KeeperThread;
        Check.That(keeper).IsNotNull();
        Check.That(keeper!.Name).IsEqualTo(SingleInstanceGuard.KeeperThreadName);
        Check.That(keeper.IsBackground).IsTrue();
        Check.That(keeper.IsAlive).IsTrue();
        Check.That(keeper.ManagedThreadId).IsNotEqualTo(Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// F2 of fixup-01 (review-01): the abandoned-mutex path counts as OWNED. Every crash, kill and
    /// FailFast that skips the keeper goes through it; <see cref="ExitStuckReport.Terminate"/>'s
    /// fallback doc relies on it. The dead owner's handle stays open so the kernel object (and its
    /// abandoned ownership) survives, exactly as a force-killed process leaves it.
    /// </summary>
    [Fact]
    public void AbandonedMutex_IsTreatedAsAcquired()
    {
        string name = UniqueName();
        Mutex? held = null;
        var owner = new Thread(() =>
        {
            held = new Mutex(initiallyOwned: true, name, out _); // never released; handle kept open
        }) { IsBackground = true, Name = "abandoning owner" };
        owner.Start();
        Check.That(owner.Join(TimeSpan.FromSeconds(5))).IsTrue(); // the owner ended → abandoned

        using var guard = new SingleInstanceGuard(name);
        try
        {
            Check.That(guard.TryAcquire(TimeSpan.FromSeconds(2))).IsTrue();
        }
        finally
        {
            GC.KeepAlive(held);
            held?.Dispose();
        }
    }

    /// <summary>
    /// F6 of fixup-01 (review-01): an unexpected failure creating the mutex still must not start a
    /// second instance, but it must leave a trace instead of taking the silent "already running"
    /// path. A name with an invalid separator makes the constructor throw an IOException.
    /// </summary>
    [Fact]
    public void TryAcquire_AnUnexpectedMutexFailure_IsReported_AndCountsAsNotAcquired()
    {
        Exception? reported = null;
        using var guard = new SingleInstanceGuard(
            @"bad\name", ex => reported = ex);

        Check.That(guard.TryAcquire()).IsFalse();
        Check.That(reported).IsNotNull();
        Check.WithCustomMessage("the unexpected creation failure was not reported as an IOException")
            .That(reported is IOException).IsTrue();
    }

    /// <summary>
    /// F6: the documented "already running / no access" exit stays silent — no callback. A second
    /// guard against a live owner's name reports nothing, so a legitimate duplicate launch does
    /// not pretend to have hit an unexpected failure.
    /// </summary>
    [Fact]
    public void TryAcquire_AContendedLiveName_ReportsNoUnexpectedFailure()
    {
        string name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        Exception? reported = null;
        using var second = new SingleInstanceGuard(name, ex => reported = ex);
        Check.That(second.TryAcquire()).IsFalse();
        Check.That(reported).IsNull();
    }
}
