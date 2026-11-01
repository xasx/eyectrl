namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Sdk.Abstractions;

public class DwellHeadCoreTests
{
    [Fact]
    public void NextId_UsesPrefixAndIncrements()
    {
        var core = new DwellHeadCore("wpf_", $"eyectrl_dwell_test_{Guid.NewGuid():N}");
        using var _ = core.Client;
        Check.That(core.NextId()).IsEqualTo("wpf_1");
        Check.That(core.NextId()).IsEqualTo("wpf_2");
    }

    [Fact]
    public async Task NextId_IsUniqueUnderConcurrency()
    {
        var core = new DwellHeadCore("x_", $"eyectrl_dwell_test_{Guid.NewGuid():N}");
        using var _ = core.Client;
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => { for (var i = 0; i < 250; i++) ids.Add(core.NextId()); })));
        Check.That(ids.Distinct().Count()).IsEqualTo(2000);
    }

    [Fact]
    public void EnsureStarted_IsIdempotent()
    {
        var core = new DwellHeadCore("t_", $"eyectrl_dwell_test_{Guid.NewGuid():N}");
        Check.That(core.Client.StartCountForTests).IsEqualTo(0);

        core.EnsureStarted();
        Check.That(core.Client.StartCountForTests).IsEqualTo(1);

        core.EnsureStarted();
        Check.That(core.Client.StartCountForTests).IsEqualTo(1);

        core.Client.Dispose();
    }
}
