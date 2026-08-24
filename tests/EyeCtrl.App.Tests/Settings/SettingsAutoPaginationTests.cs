namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using System.Windows;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;

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

    private static ToggleSpec Toggle(string id) =>
        new(id, id, () => false, _ => { });

    // Five options so the combo's option WrapPanel has room to wrap at the 416 DIP width.
    private static ComboBoxSpec Combo(string id) =>
        new(id, id, new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }, null,
            () => "Alpha", _ => { });

    private static ColorPickerSpec Picker(string id) =>
        new(id, id, () => "#FFFFFF", _ => { }, RadiusLogical: 50);

    private static ButtonSpec Button(string id) =>
        new(id, id, () => { }, ButtonKind.Default, Width: 200, Height: 80);

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

    // The test above pins the fit invariant for SliderSpec rows only; this one pins it across
    // every row kind the renderer can produce. Each control measures its own way — a combo's
    // options and a picker's swatches wrap in their panels, a button carries an explicit
    // size — and the equivalence the chunker relies on (a page's measured height is the sum
    // of its individually measured rows) must hold for all of them.
    [StaFact]
    public void EveryChunkedPage_FitsTheBudget_MixedRowTypes()
    {
        var rows = Enumerable.Range(0, 3).SelectMany(i => new SettingsRowSpec[]
        {
            Slider($"s{i}"),
            Toggle($"t{i}"),
            Combo($"c{i}"),
            Picker($"p{i}"),
            Button($"b{i}"),
            new LabelSpec(() => $"l{i}"),
        }).ToList();
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

    // Mixed-page rhythm: a standalone ButtonSpec row is a list ROW like a toggle or a slider row.
    // It must sit on the column's left axis — Stretch + the explicit Width centres in WPF, which
    // drew the 300 DIP buttons at x=130 while every other row kind starts at x=0 — and it must
    // occupy ONE UNIFORM ROW PITCH, the same box every other row kind gets.
    //
    // The measured height is 150, not the 124 this test pinned before. The number moved on
    // purpose, and the reason is hit-circle clearance between ADJACENT rows rather than
    // aesthetics. The old box was 100 + a 24 DIP bottom margin, which put the button's hit
    // circle 50 DIP from the row top while a labelled row puts its control's circle at 75 (a 100
    // DIP control centred in a 150 DIP box). A labelled row followed by a button row therefore
    // left the two centres only (150-75) + 50 = 125 DIP apart — 25 DIP of clearance between two
    // 100 DIP circles, against the 40 the colour-swatch grid leaves — and every settings tab ends
    // with a "Reset ..." ButtonSpec, so that was the tightest gap on the page and it sat directly
    // above a destructive action. The button is now centred in a LabelledRowMinHeight box, so its
    // circle is at 75 like everything else and every pitch is 150.
    //
    // What did NOT change: the button's Width, its left alignment, its fill, and the 100 DIP
    // WYSIWYG floor — the drawn button is the same size, it is the surrounding box that grew.
    // Pinned through CreateControl, the one construction path render and measurement share, so
    // the measured 150 can never drift from what renders. SettingsRowLayoutTests pins the same
    // number from the other side, and SettingsRowPitchTests measures the arranged clearance this
    // exists to buy.
    [StaFact]
    public void StandaloneButtonRow_SitsOnTheColumnAxis_AndFillsOneRowPitch()
    {
        var row = new ButtonSpec("b", "Do it", () => { }, ButtonKind.Default, Width: 300, Height: 100);

        // 416.0 is the narrowest supported content column — the same budget the height
        // assertions in this file use. A ButtonSpec is not a labelled row, so the width cannot
        // change what is built here; it is the real number rather than a placeholder anyway.
        var control = (DwellButton)SettingsWindow.CreateControl(
            row, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs, 416.0);

        Check.That(control.Root.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Left);

        // 25 per side for this 100 DIP button, because the inset is derived as
        // (LabelledRowMinHeight - Height) / 2 — the app also ships 90 and 70 DIP buttons, and a
        // flat 25 would leave those at a 140 and a 120 DIP pitch. Derived here too, so this pins
        // the RULE rather than one of its three answers.
        var inset = (SettingsWindow.LabelledRowMinHeight - 100) / 2;
        Check.That(control.Root.Margin).IsEqualTo(new Thickness(4, inset, 4, inset));

        Check.That(SettingsAutoPagination.MeasureRowHeight(row, 416.0, RadiusPhysical, DwellMs))
            .IsEqualTo(150.0);
    }

    // LabelSpec rows wrap by default (status text must be fully readable); the separator rule
    // opts out because its pinned rendering is a clipped single line.
    [StaFact]
    public void LabelRow_ForwardsTheWrapFlag()
    {
        DwellLabel Render(LabelSpec spec) =>
            (DwellLabel)SettingsWindow.CreateControl(
                spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
                416.0);

        Check.That(Render(new LabelSpec(() => "status")).Root.TextWrapping)
            .IsEqualTo(System.Windows.TextWrapping.Wrap);
        Check.That(Render(new LabelSpec(() => "────", Wrap: false)).Root.TextWrapping)
            .IsEqualTo(System.Windows.TextWrapping.NoWrap);
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

    // The seam: static tabs and Models opt in; QuickActions stays out (its picker pages carry
    // per-page qa_back_{page} ids that flattening would break). Models navigates by view state
    // (GoTo), so its page boundaries carry no semantics and its unbounded registry rows belong
    // in the chunker.
    [Fact]
    public void QuickActions_DoesNotOptIn()
    {
        var qa = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources", "QuickActionsPageSource.cs"));
        Check.WithCustomMessage("QuickActionsPageSource must NOT set PrefersAutoPagination = true: "
                + "its picker pages carry per-page qa_back_{page} ids that flattening would break.")
            .That(qa.Contains("PrefersAutoPagination => true")).IsFalse();

        // Symmetric positive assertion: Models opts IN — its registry rows are unbounded and
        // previously clipped out of reach for a gaze-only user.
        var models = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources", "ModelsPageSource.cs"));
        Check.WithCustomMessage("ModelsPageSource must set PrefersAutoPagination => true: its "
                + "page 1 is unbounded (one row per registry entry) and the height chunker is "
                + "what keeps the last rows reachable.")
            .That(models.Contains("PrefersAutoPagination => true")).IsTrue();
    }

    // Drift guard for the _sources/_tabDefs seam: _sources is hand-maintained, one line per
    // tab, and MaybeRepaginateByHeight looks the current tab up with TryGetValue — a tab added
    // to _tabDefs without a matching _sources entry quietly misses, and that tab silently
    // falls back to count-based paging (a setting can clip out of reach for a gaze-only
    // user). Nothing red anywhere; only this check catches it.
    [Fact]
    public void EveryRenderedTab_HasASourcesEntry()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

        foreach (var id in SettingsTabDriftTests.RenderedTabIds())
        {
            Check.WithCustomMessage(
                    "SettingsWindow._sources has no entry for tab \"" + id + "\" — a tab id "
                    + "missing its _sources assignment silently falls back to count-based "
                    + "paging, the exact failure this feature exists to prevent. Add the "
                    + "_sources[\"" + id + "\"] = ... line (or repoint this check at the new "
                    + "seam) — do not delete it.")
                .That(source.Contains("_sources[\"" + id + "\"] =")).IsTrue();
        }
    }

    // The height reserve must count everything the pagination row renders at: the button height,
    // the buttons' own vertical margins, and PaginationPanel's vertical margins. Under-reserving
    // by even the 8 DIP of button margin lets a page that exactly fills the chunk budget overflow
    // the content cell — the one invariant this whole mechanism exists to hold.
    [Fact]
    public void PaginationReserveHeight_AccountsForButtonAndPanelMargins()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var body = ExtractProperty(source, "private double PaginationReserveHeight =>");

        Check.WithCustomMessage(
                "PaginationReserveHeight no longer counts 2 * PaginationButtonMargin — the "
                + "pagination buttons' own vertical margin, which the rendered Auto row includes. "
                + "Repoint this check at the new expression, do not delete it.")
            .That(body).Contains("2 * PaginationButtonMargin");
        Check.WithCustomMessage(
                "PaginationReserveHeight no longer references PaginationPanel.Margin.Top — the "
                + "panel's top margin is part of the row's rendered height. Repoint this check at "
                + "the new expression, do not delete it.")
            .That(body).Contains("PaginationPanel.Margin.Top");
        Check.WithCustomMessage(
                "PaginationReserveHeight no longer references PaginationPanel.Margin.Bottom — the "
                + "panel's bottom margin is part of the row's rendered height. Repoint this check "
                + "at the new expression, do not delete it.")
            .That(body).Contains("PaginationPanel.Margin.Bottom");

        // The count is only true if MakePagButton actually applies that margin; if its margin
        // changed, the reserve's 2 * PaginationButtonMargin term must change with it.
        var makePag = ExtractMethod(source, "private Border MakePagButton(string id, string text)");
        Check.WithCustomMessage(
                "MakePagButton no longer sizes its margin from PaginationButtonMargin — the const "
                + "exists so the button margin and the reserve cannot drift apart. Restore "
                + "new Thickness(PaginationButtonMargin) (or update both).")
            .That(makePag).Contains("new Thickness(PaginationButtonMargin)");
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string ExtractProperty(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf(';', start);
        return source[start..end];
    }
}
