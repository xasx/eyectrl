namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// The simple-mode visibility layer: keeps the rows whose spec opted into the curated subset,
/// drops pages left empty. Pure — the pipeline calls it between BuildPages() and
/// MaybeRepaginateByHeight, sources never know it exists, and every per-source gate keeps
/// testing Build() unfiltered (spec §5.4).
/// </summary>
internal static class SimpleSettingsFilter
{
    /// <summary>
    /// The tabs simple mode shows. A static set, deliberately NOT a bool on the window's
    /// _tabDefs tuple: SettingsTabDriftTests and SettingsTabLabelFitTests regex-parse that
    /// tuple with exactly four members. Lives here (the filter's file) so the T1 drift test
    /// needs nothing from the sidebar model (spec §5.2, ledger L9).
    /// </summary>
    internal static readonly IReadOnlySet<string> SimpleTabIds = new HashSet<string>
    {
        "Cursor", "Dwell", "Keyboard", "General"
    };

    internal static IReadOnlyList<PageSpec> Apply(IReadOnlyList<PageSpec> pages) =>
        pages
            .Select(p => new PageSpec(p.Rows.Where(r => r.Simple).ToArray()))
            .Where(p => p.Rows.Count > 0)
            .ToArray();
}
