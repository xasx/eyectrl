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
    /// A break that lands on either side of a <see cref="SeparatorSpec"/> strands the rule at
    /// a page edge with a missing neighbour; those are dropped — see
    /// <see cref="RemoveSeparatorsStrandedAtBreaks"/>.
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
            current.Add(row);
            used += h;
        }
        if (current.Count > 0 || pages.Count == 0)
            pages.Add(current);
        RemoveSeparatorsStrandedAtBreaks(pages);
        return pages.Select(p => new PageSpec(p)).ToList();
    }

    /// <summary>
    /// A <see cref="SeparatorSpec"/> divides two ADJACENT rows, so a chunk break on either
    /// side of one leaves the rule at a page edge with a missing neighbour — a stray line at
    /// a page's top or bottom (issue #166). The break is forced by height and cannot be moved
    /// to keep the rule interior, and the break already separates the two sections visually,
    /// so a stranded separator is DROPPED: a leading separator on every page but the first
    /// and a trailing one on every page but the last. Source-order edges (the first row of
    /// the first page, the last row of the last page) are not break artefacts and render as
    /// they always have. Pages the trim empties vanish; if that leaves nothing (an
    /// all-separator list split between two breaks), one empty page is emitted, mirroring the
    /// empty-input contract. Only ever removes rows, so it cannot break the fit invariant the
    /// chunker exists for.
    /// </summary>
    private static void RemoveSeparatorsStrandedAtBreaks(List<List<SettingsRowSpec>> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
                while (pages[i].Count > 0 && pages[i][0] is SeparatorSpec)
                    pages[i].RemoveAt(0);
            if (i < pages.Count - 1)
                while (pages[i].Count > 0 && pages[i][^1] is SeparatorSpec)
                    pages[i].RemoveAt(pages[i].Count - 1);
        }
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
