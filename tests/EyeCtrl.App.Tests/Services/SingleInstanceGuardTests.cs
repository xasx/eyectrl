namespace EyeCtrl.App.Tests.Services;

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
}
