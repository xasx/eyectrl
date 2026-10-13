namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

public class SimpleSettingsFilterTests
{
    private static SliderSpec Row(string id, bool simple = false) =>
        new(id, id, 0, 1, 0.1, 1, () => 0, _ => { }) { Simple = simple };

    [Fact]
    public void Apply_KeepsOnlySimpleRows_AndDropsEmptyPages()
    {
        var pages = new List<PageSpec>
        {
            new([Row("a"), Row("b", simple: true)]),
            new([Row("c")]),                       // no simple row -> dropped
            new([Row("d", simple: true), Row("e", simple: true)]),
        };

        var filtered = SimpleSettingsFilter.Apply(pages);

        Check.That(filtered).HasSize(2);
        Check.That(filtered[0].Rows.Select(r => r.Id)).ContainsExactly("b");
        Check.That(filtered[1].Rows.Select(r => r.Id)).ContainsExactly("d", "e");
    }

    [Fact]
    public void SimpleTabIds_AreExactlyTheCuratedTabs()
    {
        // Unordered: IsOnlyMadeOf + size, never ContainsExactly (a HashSet's enumeration
        // order is an implementation detail).
        Check.That(SimpleSettingsFilter.SimpleTabIds).IsOnlyMadeOf("Cursor", "Dwell", "Keyboard", "General");
        Check.That(SimpleSettingsFilter.SimpleTabIds).HasSize(4);
    }

    [Fact]
    public void EverySimpleTierSource_PrefersAutoPagination()
    {
        var ctx = EyeCtrl.App.Tests.Settings.Pages.TestSettingsPageContext.Build();
        var sources = new Dictionary<string, ISettingsPageSource>
        {
            ["Cursor"] = new CursorPageSource(ctx),
            ["Dwell"] = new DwellPageSource(ctx),
            ["Keyboard"] = new KeyboardPageSource(ctx),
            ["General"] = new GeneralPageSource(ctx),
        };

        foreach (var id in SimpleSettingsFilter.SimpleTabIds)
            Check.That(sources[id].PrefersAutoPagination).IsTrue();
    }
}
