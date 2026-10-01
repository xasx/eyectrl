namespace EyeCtrl.App.Views.Settings;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Height-based re-pagination of settings pages. The content page is a StackPanel with no
/// ScrollViewer (SettingsWindow.xaml: "no ScrollViewer — paginated"), so a page taller than
/// the content area CLIPS its last rows — an unreachable setting for a gaze-only user. This
/// re-splits a tab's rows into pages that each fit the available height.
/// Lives at `Views/`, not `Views/Settings/`: this is the one WPF-coupled member of the spec
/// namespace, and `SettingsSpecWpfPurityTests` scans `Views/Settings/**` by path.
/// </summary>
internal static class SettingsAutoPagination
{
    /// <summary>
    /// Splits <paramref name="rows"/> into pages whose accumulated row heights fit
    /// <paramref name="budget"/>. A single row taller than the budget gets its own page
    /// (never an empty page, never an unterminated loop). A non-positive budget declines:
    /// one page, rows unchanged — mirrors ContentAvailableWidth's decline-to-constrain.
    /// <para>
    /// A break next to a <see cref="SeparatorSpec"/> would strand the rule at a page edge
    /// with a missing neighbour (issue #166). Two mechanisms prevent that: the split loop
    /// skips a rule a break would push to a page TOP — before it can consume budget — and
    /// <see cref="RemoveSeparatorsStrandedAtBreaks"/> drops a rule a break leaves at a page
    /// BOTTOM. Rules the source itself puts first or last render as before, unless the page
    /// holding them holds nothing else, in which case that page is emptied and removed: a
    /// page holding only a rule is not a page.
    /// </para>
    /// </summary>
    public static List<PageSpec> ChunkByHeight(
        IReadOnlyList<SettingsRowSpec> rows, Func<SettingsRowSpec, double> heightOf, double budget)
    {
        if (budget <= 0 || double.IsInfinity(budget) || double.IsNaN(budget))
            return [new PageSpec(rows.ToList())];

        var pages = new List<List<SettingsRowSpec>>();
        var current = new List<SettingsRowSpec>();
        var used = 0.0;
        foreach (var row in rows)
        {
            var h = heightOf(row);
            if (current.Count > 0 && used + h > budget)
            {
                pages.Add(current);
                current = [];
                used = 0;
            }
            // A break-created page never opens on a rule (issue #166 m1): the separator is
            // skipped BEFORE it can consume budget, so the page below fills as if the rule
            // had never been charged. The first page is exempt — a rule the source itself
            // puts first is content, not a break artefact.
            if (current.Count == 0 && pages.Count > 0 && row is SeparatorSpec)
                continue;
            current.Add(row);
            used += h;
        }
        if (current.Count > 0 || pages.Count == 0)
            pages.Add(current);
        RemoveSeparatorsStrandedAtBreaks(pages);
        return pages.Select(p => new PageSpec(p)).ToList();
    }

    /// <summary>
    /// Drops the rules a chunk break strands at a page BOTTOM: a <see cref="SeparatorSpec"/>
    /// divides two ADJACENT rows, so when the row after the rule overflows, the rule is left
    /// above the break with a missing neighbour — a stray line (issue #166). Page TOPS need
    /// no trim here: the split loop already skips any rule that would open a break-created
    /// page. Source-order edges — the first row of the first page, the last row of the last
    /// page — are not break artefacts and render as before, unless the page holding them
    /// consists of nothing but separators: such a page is emptied and removed, because a
    /// page holding only a rule is not a page. Only ever removes rows, so it cannot break
    /// the fit invariant the chunker exists for.
    /// </summary>
    private static void RemoveSeparatorsStrandedAtBreaks(List<List<SettingsRowSpec>> pages)
    {
        for (var i = 0; i < pages.Count - 1; i++)
            while (pages[i].Count > 0 && pages[i][^1] is SeparatorSpec)
                pages[i].RemoveAt(pages[i].Count - 1);
        pages.RemoveAll(p => p.Count == 0);
        if (pages.Count == 0)
            pages.Add([]);
    }

    /// <summary>
    /// The ring preview's spec-level omission, run BEFORE <see cref="ChunkByHeight"/> because the
    /// chunker has no drop path — a lone oversized row gets its own page and is then clipped,
    /// which is the #113 failure in reverse.
    ///
    /// <para>
    /// One computation decides both things: the fit scalar (<see cref="RingPlan.ScaleFor"/>) is
    /// stamped onto the ring row, and the same scalar's legibility decides whether the row
    /// survives. The width the scalar sees is the column MINUS the caption's room
    /// (<see cref="SettingsWindow.RingPreviewCaptionGutter"/> +
    /// <see cref="SettingsWindow.RingPreviewMinCaptionWidth"/>) — the painting and its captions
    /// share the column, so a width-bound preview must scale the painting down to leave the
    /// captions readable, never draw over them (PR #175 F1). If the reserved width cannot clear
    /// the legibility floor, the preview is omitted rather than shrunk or overlapped.
    /// </para>
    ///
    /// <para>
    /// Height never decides omission: <see cref="RingPlan.ScaleFor"/> is bounded by
    /// <c>availableHeight / WindowSize</c>, so <c>WindowSize * scale ≤ availableHeight</c> holds
    /// by construction. The legibility floor is the whole predicate. (An earlier revision also
    /// carried a <c>WindowSize * scale &gt; availableHeight</c> clause with a float tolerance;
    /// it was provably dead — PR #175 F5.)
    /// </para>
    ///
    /// <para>
    /// When the preview is omitted, the jump row goes with it — a "View ring preview" row that
    /// lands on a page with no preview is a dead affordance. Every other row is returned untouched
    /// and in order, so pages holding existing rows cannot move (append-only).
    /// </para>
    /// </summary>
    /// <param name="rows">The tab's rows, in order, before chunking.</param>
    /// <param name="availableWidth">Room in the content column, in DIP.</param>
    /// <param name="availableHeight">The content cell's height budget, in DIP.</param>
    /// <returns>The rows with the ring row stamped, or with the preview's rows dropped.</returns>
    public static List<SettingsRowSpec> FitRingPreview(
        IReadOnlyList<SettingsRowSpec> rows, double availableWidth, double availableHeight)
    {
        var ring = rows.OfType<RingPreviewSpec>().FirstOrDefault();
        if (ring is null) return rows.ToList();

        var plan = ring.Plan();

        // The caption's room is reserved in the SAME computation the row reads: the painting may
        // only use what the column has left after the gutter and the narrowest readable caption
        // column. RingPreviewRow's Grid gives the painting exactly this width (Auto column) and
        // the captions the star remainder.
        var paintingWidth = availableWidth
            - SettingsWindow.RingPreviewCaptionGutter - SettingsWindow.RingPreviewMinCaptionWidth;
        var scale = plan.ScaleFor(paintingWidth, availableHeight);

        if (!RingPlan.IsLegibleAt(scale))
            return rows.Where(r => r is not (RingPreviewSpec or RingJumpSpec)).ToList();

        return rows.Select(r => r is RingPreviewSpec rr ? rr with { Scale = scale } : r).ToList();
    }

    /// <summary>
    /// The complete re-pagination production runs for an auto-paginated source: the ring
    /// preview's spec-level fit, then the height chunker — in that order, because the chunker has
    /// no drop path. Extracted so every consumer measures the pages production actually renders:
    /// <c>SettingsWindow.MaybeRepaginateByHeight</c> and the reachability gate's
    /// <c>PagesAsRendered</c> both call this, so the gate cannot drift into measuring a pipeline
    /// production no longer runs (PR #175 F2).
    /// </summary>
    /// <param name="rows">The tab's rows, in order, before pagination.</param>
    /// <param name="availableWidth">Room in the content column, in DIP.</param>
    /// <param name="availableHeight">The content cell's height budget, in DIP.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels, for row measurement.</param>
    /// <param name="dwellMs">Dwell duration, for row measurement.</param>
    /// <returns>The pages production would render, in order.</returns>
    public static List<PageSpec> Repaginate(
        IReadOnlyList<SettingsRowSpec> rows, double availableWidth, double availableHeight,
        double radiusPhysical, int dwellMs)
    {
        var fitted = FitRingPreview(rows, availableWidth, availableHeight);
        return ChunkByHeight(
            fitted,
            row => MeasureRowHeight(row, availableWidth, radiusPhysical, dwellMs),
            availableHeight);
    }

    /// <summary>
    /// Measures one row's rendered height the way the content panel would lay it out: the
    /// real control is constructed via <see cref="SettingsWindow.CreateControl"/> against a
    /// no-op registrar, attached to a detached StackPanel pinned to
    /// <paramref name="availableWidth"/> (so labels wrap exactly as they will on screen) and
    /// carrying the window's font, then measured. Nothing is shown and the live dwell
    /// detector is never touched.
    /// </summary>
    public static double MeasureRowHeight(
        SettingsRowSpec row, double availableWidth, double radiusPhysical, int dwellMs) =>
        MeasureRows([row], availableWidth, radiusPhysical, dwellMs);

    /// <summary>Measures a whole page's rendered height under the same conditions.</summary>
    public static double MeasurePageHeight(
        PageSpec page, double availableWidth, double radiusPhysical, int dwellMs) =>
        MeasureRows(page.Rows, availableWidth, radiusPhysical, dwellMs);

    private static double MeasureRows(
        IReadOnlyList<SettingsRowSpec> rows, double availableWidth, double radiusPhysical, int dwellMs)
    {
        var scratch = new StackPanel { Width = availableWidth };
        // The SettingsWindow is an OverlayWindow, whose constructor sets FontFamily =
        // Tokens.FontStandard and lets it inherit down. Without replicating that, labels
        // measure in the system font and report heights the application never produces
        // (same lesson as SettingsContentFitTests).
        TextElement.SetFontFamily(scratch, Tokens.FontStandard);

        var attached = new List<object>();
        try
        {
            foreach (var row in rows)
            {
                // availableWidth goes to CreateControl too, not just to the scratch panel: a
                // labelled row is assembled two-column or stacked according to it, and those two
                // forms have different heights. Measuring the wrong one is measuring a page that
                // never renders.
                switch (SettingsWindow.CreateControl(
                    row, new NoopDwellRegistrar(), radiusPhysical, dwellMs, availableWidth))
                {
                    case DwellControl c:
                        // Mirror the render path exactly: Add(DwellControl) applies
                        // ConstrainToContentWidth before attaching, so a control squeezed
                        // on a narrow display must measure under the same squeeze —
                        // otherwise "measured == rendered" holds only for as long as
                        // every root happens to carry an explicit Height.
                        SettingsWindow.ConstrainToContentWidth(c.Root, availableWidth);
                        c.Attach(scratch); attached.Add(c); break;
                    case IDwellComposite comp:
                        comp.Attach(scratch); attached.Add(comp); break;
                    case DwellLabel label:
                        label.Attach(scratch); attached.Add(label); break;
                    default:
                        throw new InvalidOperationException(
                            $"Unhandled control type for row spec: {row.GetType().Name}");
                }
            }

            scratch.Measure(new Size(availableWidth, double.PositiveInfinity));
            return scratch.DesiredSize.Height;
        }
        finally
        {
            foreach (var item in attached.OfType<IDwellComposite>()) item.Detach(scratch);
            foreach (var item in attached.OfType<DwellControl>()) item.Detach(scratch);
            foreach (var item in attached.OfType<DwellLabel>()) item.Detach(scratch);
        }
    }

    /// <summary>
    /// A registrar that records nothing: measurement constructs real controls, and their
    /// Attach registers dwell targets — against this, so the live detector is never touched.
    /// </summary>
    internal sealed class NoopDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radiusPhysical,
            int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
