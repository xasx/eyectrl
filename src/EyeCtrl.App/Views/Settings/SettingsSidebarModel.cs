namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Pure sidebar reconciliation for the settings window (spec §5.6): everything the sidebar
/// needs — visible ids, page count, the post-snap page index, the slice, pager presence, and
/// the snap decision — derived from (all tab ids, the simple set, the mode, the sidebar
/// budget, the current tab), so the one switch decision with user-visible consequences is
/// headless-testable. The window applies the outputs; it never re-derives them. No WPF types:
/// the purity census walks this file.
/// </summary>
internal static class SettingsSidebarModel
{
    internal sealed record SidebarLayout(
        IReadOnlyList<string> VisibleIds,
        int TabsPerPage,
        int PageCount,
        int PageIndex,
        (int Start, int Count) Slice,
        bool PagerPresent,
        string? SnapTo);

    internal static SidebarLayout Compute(
        IReadOnlyList<string> allIds,
        IReadOnlySet<string> simpleIds,
        bool simple,
        double sidebarBudget,
        double tabSlot,
        string currentTab)
    {
        var visible = simple ? allIds.Where(id => simpleIds.Contains(id)).ToArray() : allIds.ToArray();
        var perPage = SettingsTabPagination.TabsPerPage(sidebarBudget, tabSlot);
        var pageCount = SettingsTabPagination.PageCount(visible.Length, perPage);
        // The snap: the current tab must be visible after the switch; the leader of the target
        // set (Cursor in both) is the stable target. The page index is derived from the
        // POST-snap tab — a clamp would strand the current tab off-page (round-3 I4).
        var snapTo = visible.Contains(currentTab) || visible.Length == 0
            ? null
            : visible[0];
        var effective = snapTo ?? currentTab;
        var index = Array.IndexOf(visible, effective);
        var pageIndex = index < 0 ? 0 : SettingsTabPagination.PageOf(index, perPage);
        var slice = SettingsTabPagination.PageSlice(visible.Length, perPage, pageIndex);
        return new SidebarLayout(visible, perPage, pageCount, pageIndex, slice, pageCount > 1, snapTo);
    }
}
