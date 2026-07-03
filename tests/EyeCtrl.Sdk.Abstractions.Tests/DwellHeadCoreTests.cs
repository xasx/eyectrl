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
}
