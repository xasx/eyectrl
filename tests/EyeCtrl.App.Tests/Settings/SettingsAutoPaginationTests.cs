namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;

public class SettingsAutoPaginationTests
{
    private static SettingsRowSpec Row(string id) => new LabelSpec(() => id);

    // Rows are measured by an injected function, so the chunking arithmetic is testable
    // without WPF. Heights here are synthetic.
    [Fact]
    public void ChunkByHeight_SplitsWhenTheNextRowWouldOverflow()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b"), Row("c") };
        var heights = new Dictionary<SettingsRowSpec, double>
        {
            [rows[0]] = 40, [rows[1]] = 40, [rows[2]] = 40
        };

        var pages = SettingsAutoPagination.ChunkByHeight(rows, r => heights[r], budget: 100);

        Check.That(pages).HasSize(2);
        Check.That(pages[0].Rows).HasSize(2);
        Check.That(pages[1].Rows).HasSize(1);
    }

    [Fact]
    public void ChunkByHeight_ASingleOversizedRow_GetsItsOwnPageAndNeverLoops()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("huge"), Row("c") };
        var heights = new Dictionary<SettingsRowSpec, double>
        {
            [rows[0]] = 40, [rows[1]] = 500, [rows[2]] = 40
        };

        var pages = SettingsAutoPagination.ChunkByHeight(rows, r => heights[r], budget: 100);

        // Order preserved, no empty pages, the oversized row isolated.
        Check.That(pages).HasSize(3);
        Check.That(pages[1].Rows).HasSize(1);
        Check.That(pages.SelectMany(p => p.Rows)).HasSize(3);
    }

    [Fact]
    public void ChunkByHeight_ExactFit_IsNotSplit()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b") };
        var pages = SettingsAutoPagination.ChunkByHeight(rows, _ => 50, budget: 100);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).HasSize(2);
    }

    [Fact]
    public void ChunkByHeight_DegenerateBudget_PassesRowsThroughAsOnePage()
    {
        var rows = new List<SettingsRowSpec> { Row("a"), Row("b"), Row("c") };
        var pages = SettingsAutoPagination.ChunkByHeight(rows, _ => 50, budget: 0);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).HasSize(3);
    }

    [Fact]
    public void ChunkByHeight_EmptyRows_YieldsOneEmptyPage()
    {
        var pages = SettingsAutoPagination.ChunkByHeight(
            Array.Empty<SettingsRowSpec>(), _ => 50, budget: 100);

        Check.That(pages).HasSize(1);
        Check.That(pages[0].Rows).IsEmpty();
    }
}
