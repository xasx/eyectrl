namespace EyeCtrl.App.Tests.Settings;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;

/// <summary>
/// The simple-mode reachability gate: every row the curation kept has to stay AIMABLE after the
/// filtered set is re-chunked.
///
/// <para>
/// <see cref="SimpleModeCurationTests"/> pins WHICH rows simple mode shows; this pins WHERE they
/// land. Simple mode re-runs <see cref="SettingsAutoPagination.Repaginate"/> over the curated
/// subset (a shorter list re-chunks differently than the full one — fewer rows per page is
/// expected, a row pushed past the fold is not), and for a gaze-only user a registered-but-
/// unreachable target is a setting that has silently ceased to exist — the same invariant
/// <see cref="HandChunkedPageFitTests"/> holds for the full row set, here for the curated one.
/// </para>
/// <para>
/// The budget and column are NOT invented here: they are the one-argument internal derivations
/// <see cref="HandChunkedPageFitTests"/>' own sweep runs at — <c>ContentColumnWidth(root)</c> and
/// <c>ContentHeightBudget(root)</c> at its 1920×1080 reference display — which read the same four
/// SettingsWindow.xaml elements through SettingsContentFitTests' own readers plus production's
/// pagination consts. No term of the budget is restated, and none is omitted.
/// </para>
/// <para>
/// KNOWN GAP, recorded not solved (owner ruling, 2026-10-11, measured this task): the brief
/// mandated the 800×600 DIP narrow display (SettingsContentFitTests' NarrowDisplayWidthDip /
/// NarrowDisplayHeightDip), and at it the gate is UNSATISFIABLE by the current rows, in
/// production geometry — not just in this probe. <c>ContentColumnWidth(root, 800)</c> is the
/// 416.00 DIP stacked column, <c>ContentHeightBudget(root, 600)</c> is 256.00 DIP, and
/// <c>SettingsWindow.ContentAvailableHeight</c> computes the same 256 with no clamp; the wrapped
/// composite rows arrange 353.80–493.80 DIP deep, putting TEN curated circles below the fold
/// (Cursor c_color_2/3 357.80, c_ring_style_1 353.80 / c_ring_style_2 493.80; Keyboard k_layout_2
/// 353.80, k_lang_2/3 329.86 / k_lang_4 469.86; General g_language_2 353.80, g_theme_2 329.86).
/// Even the optimistic 344.00 DIP variant (SettingsContentFitTests' own reachable arithmetic,
/// without the pagination button block) leaves seven. That is the same class of display-size gap
/// HandChunkedPageFitTests' DisplayWidthDip remarks record and scope out BY DISPLAY SIZE — its
/// paragraph is the precedent for pinning this gate at the reference display rather than
/// weakening the circle rule or exempting targets. Fixing the narrow display needs production
/// row work (compact combo shapes, or splittable composites), which is outside this plan's test
/// tasks; until then the gap is named here rather than hidden.
/// </para>
/// <para>
/// Placement and the circle-in-cell check are <see cref="HandChunkedPageFitTests"/>' Place probe,
/// copied: that file is off-limits to wider visibility this task, and the repo's convention keeps
/// test helpers private per class. The copy builds through production's single construction path
/// (<see cref="SettingsWindow.CreateControl"/>), constrains and attaches the way the render path
/// does, carries the window font, and reads every circle's centre off the ARRANGED tree — never
/// from DesiredSize, which WPF clamps to the measure constraint.
/// </para>
/// </summary>
public class SimpleModeReachabilityTests
{
    /// <summary>
    /// <inheritdoc cref="HandChunkedPageFitTests.DisplayWidthDip" path="/summary"/>
    /// The one-argument budget derivations below default to this display and its 1080 counterpart.
    /// </summary>
    private const double DisplayWidthDip = 1920;

    /// <inheritdoc cref="HandChunkedPageFitTests.DisplayHeightDip"/>
    private const double DisplayHeightDip = 1080;

    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;
    private const int DwellMs = 800;

    /// <summary>Sub-pixel slack for geometry comparisons (HandChunkedPageFitTests' Tolerance).</summary>
    private const double Tolerance = 0.01;

    /// <summary>One dwell target, as its arranged centre in content-panel coordinates.</summary>
    private sealed record Target(string DwellId, double CentreX, double CentreY);

    [StaFact]   // Place runs WPF layout (Measure/Arrange/TransformToAncestor)
    public void EverySimpleRow_IsReachable_OnTheReferenceDisplay()
    {
        var root = SettingsContentFitTests.Xml();
        var columnWidth = HandChunkedPageFitTests.ContentColumnWidth(root, DisplayWidthDip);
        var budget = HandChunkedPageFitTests.ContentHeightBudget(root, DisplayHeightDip);

        var missing = new List<string>();
        var unreachable = new List<string>();
        var emptyTabs = new List<string>();
        var pagesPlaced = 0;

        foreach (var tab in SimpleSettingsFilter.SimpleTabIds)
        {
            var ctx = TestSettingsPageContext.Build();
            var rows = SimpleSettingsFilter.Apply(SourceFor(ctx, tab).Build())
                .SelectMany(p => p.Rows).ToList();

            // The pipeline production runs for a simple-mode tab: the curated rows, re-chunked
            // against the live budget (SettingsWindow.MaybeRepaginateByHeight's two steps).
            var pages = SettingsAutoPagination.Repaginate(
                rows, columnWidth, budget, RadiusPhysical, DwellMs);

            if (pages.Count < 1)
            {
                emptyTabs.Add(tab);
                continue;
            }

            // Membership side of reachability: every interactive row id the curation kept must
            // come out of the chunker on some page. LabelSpec rows have no dwell id and are
            // ignored (their Id is "").
            var outputIds = pages.SelectMany(p => p.Rows)
                .Where(r => r.Id.Length > 0).Select(r => r.Id).ToHashSet();
            missing.AddRange(rows
                .Where(r => r.Id.Length > 0 && !outputIds.Contains(r.Id))
                .Select(r => $"{tab}:{r.Id}"));

            foreach (var page in pages)
            {
                var (_, targets) = Place(page, columnWidth);
                pagesPlaced++;

                foreach (var t in targets)
                {
                    var left = t.CentreX - RadiusPhysical;
                    var right = t.CentreX + RadiusPhysical;
                    var top = t.CentreY - RadiusPhysical;
                    var bottom = t.CentreY + RadiusPhysical;
                    if (left < -Tolerance || right > columnWidth + Tolerance
                        || top < -Tolerance || bottom > budget + Tolerance)
                        unreachable.Add(string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"{tab}: \"{t.DwellId}\" circle x {left:F2}..{right:F2}, "
                            + $"y {top:F2}..{bottom:F2}"));
                }
            }
        }

        var context = $"(simple tabs over {{{string.Join(", ", SimpleSettingsFilter.SimpleTabIds.OrderBy(t => t, StringComparer.Ordinal))}}}: " +
            $"budget {budget:F2} DIP, {DisplayWidthDip:F0}x{DisplayHeightDip:F0} display, " +
            $"{columnWidth:F2} DIP content column, circle radius {RadiusPhysical:F2}, {pagesPlaced} pages placed)";

        Check.WithCustomMessage(
                $"These simple tabs repaginated to zero pages {context}. The curation kept no row " +
                "for them, so simple mode would show an empty tab. A missing row is a curation " +
                "bug — fix the source's Simple flags, not this assertion.")
            .That(emptyTabs).IsEmpty();

        Check.WithCustomMessage(
                $"These curated rows did not survive repagination {context}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing)
                + $"{Environment.NewLine}SettingsAutoPagination.ChunkByHeight has no drop path, so " +
                "a row disappearing here means the filtered set handed the chunker something it " +
                "silently discarded.")
            .That(missing).IsEmpty();

        Check.WithCustomMessage(
                $"These dwell circles are NOT wholly inside the content cell {context}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, unreachable)
                + $"{Environment.NewLine}The content area has no ScrollViewer, so a circle past " +
                "the fold cannot be looked at — the curated row is registered and unreachable, " +
                "which for a gaze-only user is the same as gone. The row that overflowed is one " +
                "the chunker believes fits but which ARRANGES taller than it MEASURES — fix the " +
                "row's measurement/arrangement parity rather than the chunker, and do not relax " +
                "this into a page-height check (see HandChunkedPageFitTests' remarks).")
            .That(unreachable).IsEmpty();

        // The sweep, not just one tab of it: a tab set that produced no page at all would make
        // every assertion above pass over nothing.
        Check.WithCustomMessage(
                $"No simple-mode page was measured at all {context}. Every tab repaginated to " +
                "zero pages, so the assertions above proved nothing.")
            .That(pagesPlaced).IsStrictlyGreaterThan(0);
    }

    private static ISettingsPageSource SourceFor(SettingsPageContext ctx, string tab) => tab switch
    {
        "Cursor" => new CursorPageSource(ctx),
        "Dwell" => new DwellPageSource(ctx),
        "Keyboard" => new KeyboardPageSource(ctx),
        "General" => new GeneralPageSource(ctx),
        _ => throw new ArgumentException(
            $"{tab} joined SimpleTabIds without a reachability case - teach SourceFor about it")
    };

    /// <summary>
    /// <see cref="HandChunkedPageFitTests"/>' Place probe, copied (it is private and this task
    /// may not widen it): builds the page through production's single construction path, stacks it
    /// the way ContentPanel does, arranges it, and reports where every registered dwell circle's
    /// centre landed.
    /// </summary>
    /// <param name="page">The page to place.</param>
    /// <param name="columnWidth">The content column's width in DIP.</param>
    /// <returns>The page's arranged height, and its dwell targets in page order.</returns>
    private static (double PageHeight, List<Target> Targets) Place(PageSpec page, double columnWidth)
    {
        // Pinned to the column's width and carrying the window's font: SettingsWindow is an
        // OverlayWindow, whose ctor sets FontFamily = Tokens.FontStandard and lets it inherit
        // down. Without that, labels measure in the system font and every position is one the
        // application never produces.
        var host = new StackPanel { Width = columnWidth };
        TextElement.SetFontFamily(host, Tokens.FontStandard);

        var built = new List<object>();
        foreach (var spec in page.Rows)
        {
            var control = SettingsWindow.CreateControl(
                spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusPhysical, DwellMs,
                columnWidth);

            switch (control)
            {
                case DwellControl single:
                    SettingsWindow.ConstrainToContentWidth(single.Root, columnWidth);
                    single.Attach(host);
                    break;
                case IDwellComposite composite:
                    composite.Attach(host);
                    break;
                case DwellLabel label:
                    // Registers no dwell target — it contributes height, never a circle.
                    label.Attach(host);
                    break;
                default:
                    Check.WithCustomMessage(
                            $"A {spec.GetType().Name} built a {control.GetType().Name}, which this "
                            + "probe cannot place. Teach it the new control kind — a row kind it "
                            + "drops is a row kind whose circles nothing checks.")
                        .That(false).IsTrue();
                    break;
            }

            built.Add(control);
        }

        host.Measure(new Size(columnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(new Size(columnWidth, host.DesiredSize.Height)));
        host.UpdateLayout();

        var targets = new List<Target>();
        foreach (var control in built)
        {
            List<DwellControl> circles = control switch
            {
                IDwellComposite composite => composite.Children.ToList(),
                DwellControl single => [single],
                _ => [],
            };

            foreach (var circle in circles)
            {
                var box = circle.Root
                    .TransformToAncestor(host)
                    .TransformBounds(new Rect(circle.Root.RenderSize));
                targets.Add(new Target(
                    circle.DwellId, box.Left + (box.Width / 2), box.Top + (box.Height / 2)));
            }
        }

        return (host.ActualHeight, targets);
    }
}
