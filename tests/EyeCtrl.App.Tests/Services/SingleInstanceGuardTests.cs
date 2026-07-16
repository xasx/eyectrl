namespace EyeCtrl.App.Tests.Services;

using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;

public class SingleInstanceGuardTests
{
    // NOTE: a named Mutex is thread-reentrant — the owning thread can acquire it again.
    // So "two guards on the same test thread both return true" is expected OS behavior,
    // NOT a bug. To test real contention we must acquire the second guard from a
    // DIFFERENT thread. Each test uses a unique mutex name so tests don't interfere.
    private static string UniqueName() => @"Local\EyeCtrl.App.Test." + Guid.NewGuid();

    [Fact]
    public async Task TryAcquire_SecondThread_Fails_WhenFirstHoldsIt()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Check.That(first.TryAcquire()).IsTrue();
        try
        {
            // A second guard on a separate thread must fail to acquire.
            bool secondResult = await Task.Run(() =>
            {
                using var second = new SingleInstanceGuard(name);
                return second.TryAcquire();
            });
            Check.That(secondResult).IsFalse();
        }
        finally { first.Dispose(); }
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
