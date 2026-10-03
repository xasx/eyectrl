namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Arithmetic for paginating the settings sidebar's tab list. The sidebar is a plain vertical
/// <c>StackPanel</c> with no scrolling, so on any window shorter than close-button + 15 tab
/// slots the bottom tabs are clipped out of reach for a gaze user — found
/// 2026-08-17 when tabs 10-11 (General, Quick Actions) were unreachable. The window therefore
/// splits the tab list into pages sized to the height the sidebar actually has and pages
/// through them with dwell buttons; this class is the pure math that split, so it stays
/// testable without a window (same split as <see cref="SettingsAutoPagination"/> for content).
/// </summary>
internal static class SettingsTabPagination
{
    /// <summary>
    /// How many tab slots fit <paramref name="budget"/> DIP of sidebar height. Degenerate
    /// budgets yield one — never zero: a zero would make the last tabs structurally
    /// unreachable, which is the bug this class exists to fix.
    /// </summary>
    public static int TabsPerPage(double budget, double slotHeight)
    {
        if (budget <= 0 || double.IsInfinity(budget) || double.IsNaN(budget)) return 1;
        if (slotHeight <= 0 || double.IsInfinity(slotHeight) || double.IsNaN(slotHeight)) return 1;
        return Math.Max(1, (int)Math.Floor(budget / slotHeight));
    }

    /// <summary>How many pages <paramref name="tabCount"/> tabs need; at least one.</summary>
    public static int PageCount(int tabCount, int perPage) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, tabCount) / (double)Math.Max(1, perPage)));

    /// <summary>The page holding tab <paramref name="tabIndex"/> (0-based, dense pages).</summary>
    public static int PageOf(int tabIndex, int perPage) =>
        Math.Max(0, tabIndex) / Math.Max(1, perPage);

    /// <summary>
    /// The [start, start+count) slice of tabs shown on <paramref name="page"/>. The last page
    /// can be shorter; an out-of-range page yields an empty slice, never a wrap-around.
    /// </summary>
    public static (int Start, int Count) PageSlice(int tabCount, int perPage, int page)
    {
        perPage = Math.Max(1, perPage);
        var start = Math.Clamp(Math.Max(0, page) * perPage, 0, Math.Max(0, tabCount));
        return (start, Math.Clamp(tabCount - start, 0, perPage));
    }
}
