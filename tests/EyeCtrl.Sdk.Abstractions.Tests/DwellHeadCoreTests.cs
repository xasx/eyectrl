namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Sdk.Abstractions;

public class DwellHeadCoreTests
{
    [Fact]
    public void NextId_UsesPrefixAndIncrements()
    {
        var core = new DwellHeadCore("wpf_");
        Check.That(core.NextId()).IsEqualTo("wpf_1");
        Check.That(core.NextId()).IsEqualTo("wpf_2");
    }

    [Fact]
    public async Task NextId_IsUniqueUnderConcurrency()
    {
        var core = new DwellHeadCore("x_");
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => { for (var i = 0; i < 250; i++) ids.Add(core.NextId()); })));
        Check.That(ids.Distinct().Count()).IsEqualTo(2000);
    }

    [Fact]
    public void EnsureStarted_IsIdempotent()
    {
        var core = new DwellHeadCore("t_");
        core.EnsureStarted();
        core.EnsureStarted();
        core.Client.Dispose();
    }

    [Fact]
    public void Client_IsUsableBeforeEnsureStarted()
    {
        // This assembly ships. Client was a get-only auto-property initialised eagerly, so a
        // consumer touching it before EnsureStarted got a usable object; instance scoping moved
        // construction later so the latched pipe name could be passed in. Creating it on demand is
        // what keeps that from becoming a breaking change — an interim version threw here.
        var core = new DwellHeadCore("t_");

        Check.That(core.Client).IsNotNull();
        core.Client.Dispose();
    }

    [Fact]
    public void Client_IsTheSameInstanceEveryTime()
    {
        // Lazy creation must not hand out a fresh client per access: adapters are registered on the
        // instance returned here, and a second client would silently drop them.
        var core = new DwellHeadCore("t_");

        var first = core.Client;

        Check.That(core.Client).IsSameReferenceAs(first);
        first.Dispose();
    }
}
