namespace EyeCtrl.App.Tests.Services;

using System.Threading;
using EyeCtrl.Core.Services;

/// <summary>
/// The restart child must queue behind the outgoing instance rather than exiting on
/// contention, so ownership hands over the moment the parent's OnExit releases it.
///
/// A named Mutex is thread-reentrant, so — as in SingleInstanceGuardTests — the
/// contending guard must run on a DEDICATED thread. A pooled thread can be the very
/// one that acquired first, which would re-acquire reentrantly and give a false pass.
/// Each test uses a unique name.
/// </summary>
public class SingleInstanceHandoffTests
{
    private static string UniqueName() => @"Local\EyeCtrl.App.Handoff." + Guid.NewGuid();

    [Fact]
    public void WaitingAcquire_IsReallyQueued_BeforeTheHolderReleases()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = false;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            acquired = second.TryAcquire(TimeSpan.FromSeconds(10));
        }) { IsBackground = true };
        thread.Start();

        try
        {
            // Still running after 500 ms: the keeper is blocked in its bounded WaitOne. A fail-fast
            // acquire (the restart child stops queueing, the r2 mutant) has already returned false
            // and ended the thread by now. Joining is the wait that proves the thread really got
            // into TryAcquire; the old "started" event fired before it, so a late thread could
            // arrive after the release and pass even with a fail-fast acquire (review-01 F1).
            Check.That(thread.Join(TimeSpan.FromMilliseconds(500))).IsFalse();

            first.Dispose(); // the parent's OnExit

            Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue();
            Check.That(acquired).IsTrue();
        }
        finally
        {
            first.Dispose();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void WaitingAcquire_GivesUp_WhenTheHolderNeverReleases()
    {
        // The parent hung during shutdown. Better one instance than two fighting over
        // the gaze device, the settings file and the dwell pipe.
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            acquired = second.TryAcquire(TimeSpan.FromMilliseconds(300));
        });
        thread.Start();

        Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(acquired).IsFalse();
    }

    [Fact]
    public void ZeroTimeout_BehavesLikeTheParameterlessForm()
    {
        // The double-click guard must keep failing fast — this is the regression that
        // would let two instances start from a stray launch.
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();

        var acquired = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            acquired = second.TryAcquire(TimeSpan.Zero);
        });
        thread.Start();

        Check.That(thread.Join(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(acquired).IsFalse();
    }
}
