namespace EyeCtrl.App.Tests.Settings;

using System.IO;
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

public class SettingsAutoPaginationFitTests
{
    private const double RadiusPhysical = 50;  // match RenderSpec's logical 50 at 100% DPI
    private const int DwellMs = 500;

    private static SliderSpec Slider(string id) =>
        new(id, id, 0, 100, 1, 0, () => 0, _ => { });

    // The structural guarantee this whole task exists for: every page the chunker emits,
    // re-measured through the production measurement path at the same budget, fits —
    // except a page holding exactly one oversized row, which is by design.
    [StaFact]
    public void EveryChunkedPage_FitsTheBudgetItWasChunkedAgainst()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Slider($"s{i}")).ToList();
        const double budget = 400.0;
        const double width = 416.0;  // the narrowest content column SettingsContentFitTests measures

        var pages = SettingsAutoPagination.ChunkByHeight(
            rows, r => SettingsAutoPagination.MeasureRowHeight(r, width, RadiusPhysical, DwellMs), budget);

        Check.That(pages.Count).IsStrictlyGreaterThan(1);           // the budget really forces a split
        Check.That(pages.SelectMany(p => p.Rows)).HasSize(rows.Count);  // nothing lost
        foreach (var page in pages)
        {
            var h = SettingsAutoPagination.MeasurePageHeight(page, width, RadiusPhysical, DwellMs);
            if (page.Rows.Count > 1)
                Check.That(h).IsLessOrEqualThan(budget);
        }
    }

    // Negative control in the spirit of SettingsContentFitTests: an unchunked tall list must
    // NOT fit, proving the measurement above actually measures.
    [StaFact]
    public void UnchunkedTenSliderRows_DoNotFitTheSmallBudget()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Slider($"s{i}")).ToList();
        var onePage = new PageSpec(rows);
        var h = SettingsAutoPagination.MeasurePageHeight(onePage, 416.0, RadiusPhysical, DwellMs);

        Check.That(h).IsStrictlyGreaterThan(400.0);
    }
}

public class SettingsAutoPaginationWiringTests
{
    // SettingsWindow can't be constructed headlessly (8 ctor deps), so — in the spirit of
    // SettingsContentFitTests.TheContentAdd_StillAppliesTheConstraint — pin by source read
    // that RebuildContent actually routes through the chunker.
    [Fact]
    public void RebuildContent_CallsTheRepaginationStep()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var body = ExtractMethod(source, "private void RebuildContent()");

        Check.WithCustomMessage(
                "RebuildContent no longer calls MaybeRepaginateByHeight — the auto-pagination "
                + "this plan added. Repoint this check at the new call, do not delete it.")
            .That(body).Contains("MaybeRepaginateByHeight");
    }

    // The seam: static tabs opt in; the two navigation-state sources stay out.
    [Fact]
    public void QuickActionsAndModels_DoNotOptIn()
    {
        foreach (var name in new[] { "QuickActionsPageSource", "ModelsPageSource" })
        {
            var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Views", "Settings", "Sources", name + ".cs"));
            Check.WithCustomMessage(name + " must NOT set PrefersAutoPagination = true: its "
                    + "page boundaries carry navigation semantics.")
                .That(source.Contains("PrefersAutoPagination => true")).IsFalse();
        }
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
