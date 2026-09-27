namespace EyeCtrl.App.Views.Settings;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
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
