namespace EyeCtrl.App.Tests.Settings;

using System.IO;
using EyeCtrl.App.Views.Settings;

public class SettingsTabPaginationTests
{
    // The sidebar is a plain vertical StackPanel — no scrolling — so on any window shorter than
    // ~1744 DIP (close button + 15 x 108-DIP tab slots), the bottom tabs are clipped out of
    // reach for a gaze user. Found 2026-08-17: tabs 10-11 (General, Quick Actions) unreachable.
    // These tests pin the arithmetic of the fix: split the tab list into pages sized to the
    // height the sidebar actually has.
    [Fact]
    public void TabsPerPage_FitsTheBudget_MinimumOne()
    {
        // 948 DIP of room at the documented 108-DIP slot: 8 full tabs.
        Check.That(SettingsTabPagination.TabsPerPage(budget: 948, slotHeight: 108)).IsEqualTo(8);

        // A budget that fits less than one slot still yields one tab — never zero, or the last
        // tabs become structurally unreachable (same decline-to-one as ChunkByHeight's own page).
        Check.That(SettingsTabPagination.TabsPerPage(budget: 40, slotHeight: 108)).IsEqualTo(1);

        // Degenerate budget (window height unknown yet): one tab per page, not a crash.
        Check.That(SettingsTabPagination.TabsPerPage(budget: 0, slotHeight: 108)).IsEqualTo(1);
        Check.That(SettingsTabPagination.TabsPerPage(budget: double.NaN, slotHeight: 108)).IsEqualTo(1);
    }

    [Fact]
    public void PageCount_CoversEveryTab_Exactly()
    {
        Check.That(SettingsTabPagination.PageCount(tabCount: 11, perPage: 8)).IsEqualTo(2);
        Check.That(SettingsTabPagination.PageCount(tabCount: 11, perPage: 3)).IsEqualTo(4);   // 3+3+3+2
        Check.That(SettingsTabPagination.PageCount(tabCount: 11, perPage: 11)).IsEqualTo(1);
        // Degenerate: no tabs at all still yields one (empty) page, mirroring ChunkByHeight.
        Check.That(SettingsTabPagination.PageCount(tabCount: 0, perPage: 8)).IsEqualTo(1);
    }

    [Fact]
    public void PageOf_IsTheSliceContainingTheTab()
    {
        Check.That(SettingsTabPagination.PageOf(tabIndex: 0, perPage: 8)).IsEqualTo(0);
        Check.That(SettingsTabPagination.PageOf(tabIndex: 7, perPage: 8)).IsEqualTo(0);
        Check.That(SettingsTabPagination.PageOf(tabIndex: 8, perPage: 8)).IsEqualTo(1);
        Check.That(SettingsTabPagination.PageOf(tabIndex: 10, perPage: 8)).IsEqualTo(1);
    }

    // The last page can hold fewer tabs than the others (11 = 3+3+3+2): rendering must slice,
    // not assume a full page.
    [Fact]
    public void PageSlice_LastPageIsShorter_NeverPastTheEnd()
    {
        Check.That(SettingsTabPagination.PageSlice(tabCount: 11, perPage: 3, page: 0))
            .IsEqualTo((0, 3));
        Check.That(SettingsTabPagination.PageSlice(tabCount: 11, perPage: 3, page: 3))
            .IsEqualTo((9, 2));
        // Out-of-range page: empty slice, not a crash or a wrap-around.
        Check.That(SettingsTabPagination.PageSlice(tabCount: 11, perPage: 3, page: 4))
            .IsEqualTo((11, 0));
    }
}

public class SettingsTabPaginationWiringTests
{
    private static string WindowSource() => File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

    // Source-scan pin (the window can't be constructed headlessly — its XAML needs app-level
    // resources): BuildTabButtons must derive the tab-page size through the sidebar model call,
    // not a hardcoded tab count, or a future display geometry silently re-clips tabs.
    [Fact]
    public void BuildTabButtons_SizesPagesFromTheChunker()
    {
        var body = ExtractMethod(WindowSource(), "private void BuildTabButtons()");

        Check.WithCustomMessage(
                "BuildTabButtons no longer routes its paging through ComputeSidebarLayout — "
                + "a hardcoded count re-clips the bottom tabs on short windows. Repoint the "
                + "check at the new derivation, do not delete it.")
            .That(body.Contains("ComputeSidebarLayout(")).IsTrue();
    }

    [Fact]
    public void RenderTabPage_SlicesTheVisibleDefs()
    {
        var body = ExtractMethod(WindowSource(), "private void RenderTabPage(");
        Check.WithCustomMessage(
                "RenderTabPage must slice the model-visible defs, never the full list — a hidden tab "
                + "must not be attached or registered (the gaze-trap rule extends to mode hiding).")
            .That(body).Contains("_visibleDefs");
    }

    [Fact]
    public void BuildTabButtons_NeverFiltersTheFaceLoop()
    {
        // Round-2 N1's real home: filtering the face loop crashes simple-to-advanced with
        // KeyNotFoundException (the process dies with no Supervisor present). The loop's literal
        // foreach is already green today; these Not-Contains are the teeth — no filter may creep in.
        var body = ExtractMethod(WindowSource(), "private void BuildTabButtons(");
        Check.WithCustomMessage("The face loop must not read the visible defs.")
            .That(body).Not.Contains("_visibleDefs");
        Check.WithCustomMessage("The face loop must not read the simple set.")
            .That(body).Not.Contains("SimpleTabIds");
        Check.WithCustomMessage("The face loop must not read the mode flag.")
            .That(body).Not.Contains("SimpleSettingsUi");
        Check.WithCustomMessage("No 'if (!visible) continue;' filter may be added to the face loop.")
            .That(body).Not.Contains("continue");
    }

    [Fact]
    public void BuildTabButtons_RegistersTheCloseButton()
    {
        // Plan-review I4: no test anywhere pins this; an executor "rewiring the tail" must not
        // drop it — the gaze-only owner would lose the only dwell that closes Settings.
        var body = ExtractMethod(WindowSource(), "private void BuildTabButtons(");
        Check.That(body).Contains("Register(\"close\"");
    }

    [Fact]
    public void ComputeSidebarLayout_FeedsTheRealBudgetAndSlot()
    {
        // Plan-review I7: the page math must still derive from the live sidebar budget, not a
        // constant smuggled through the model call.
        var body = ExtractMethod(WindowSource(), "private SettingsSidebarModel.SidebarLayout ComputeSidebarLayout(");
        Check.That(body).Contains("SidebarTabBudget");
        Check.That(body).Contains("TabContainerHeight + 2 * TabButtonMargin");
        // Impl review F2 (mutation n): the model must also receive the LIVE mode flag. Feeding
        // it a constant false passed 2270/0 while giving simple mode all 15 sidebar tabs — 11
        // of which render blank because the content filter empties them.
        //
        // ExtractMethod note: the method is EXPRESSION-bodied, so the "\n    }" anchor lands on
        // the next block-bodied method's closing brace and the slice spills past the `;`. That
        // is harmless here — the spill (ApplySidebarLayout) contains no other SimpleSettingsUi
        // occurrence, so the pin can only pass through ComputeSidebarLayout's own argument.
        Check.WithCustomMessage(
                "ComputeSidebarLayout must feed the model the LIVE mode flag "
                + "(_settings.General.SimpleSettingsUi), never a constant. Mutation n (impl "
                + "review) passed false and survived 2270/0: simple mode showed all 15 sidebar "
                + "tabs, 11 of them blank (the content filter empties non-curated tabs).")
            .That(body).Contains("SimpleSettingsUi");
    }

    [Fact]
    public void ModelCompute_UsesTheChunker()
    {
        // Rooted at the repo root like every source scan in this class (the test host's cwd is
        // the bin directory — a relative path throws FileNotFoundException). Green on contact:
        // the model exists from Task 6; this pin guards future rewrites (plan-review r2 N2).
        var body = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "Settings", "SettingsSidebarModel.cs"));
        Check.WithCustomMessage("SettingsSidebarModel.Compute must derive paging from SettingsTabPagination.")
            .That(body).Contains("SettingsTabPagination.TabsPerPage");
    }

    // Hidden tabs must not keep live dwell targets: an unregistered-looking tab that still
    // registers a hit circle is a gaze trap (a dwell "succeeds" on invisible chrome).
    [Fact]
    public void RenderTabPage_UnregistersTabsItHides()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var body = ExtractMethod(source, "private void RenderTabPage()");

        Check.WithCustomMessage(
                "RenderTabPage no longer unregisters the dwell targets of the tabs it hides — "
                + "an off-page tab with a live hit circle can steal a dwell. Restore the "
                + "Unregister loop (or repoint this check at the new mechanism).")
            .That(body.Contains("Unregister")).IsTrue();
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
