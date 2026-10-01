namespace EyeCtrl.App.Tests.Settings;

using System.Globalization;
using EyeCtrl.App.Tests;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The sidebar's tab pager sits BELOW the tab list, and both register 60/85-DIP hit circles. On a
/// short window the tab list's chunker fills the sidebar down to the pager row, so the last tab on
/// a FULL tab page can end up close enough to the pager for the two circles to intersect — a
/// nearest-target arbitration that flips mid-dwell. Nothing measured this before.
///
/// <para>
/// Every term is derived from the live XAML through <see cref="SettingsContentFitTests"/>'s own
/// readers plus production's consts, exactly the discipline the other fit suites use:
/// <c>CloseButton</c>'s Height/Margin, <c>TabButtonPanel</c>'s and <c>TabPagerPanel</c>'s Margins,
/// <see cref="SettingsWindow.TabContainerHeight"/>/<see cref="SettingsWindow.TabButtonMargin"/>,
/// <see cref="SettingsWindow.PaginationButtonHeight"/>/<see cref="SettingsWindow.PaginationButtonMargin"/>,
/// and <c>SettingsTabPagination.TabsPerPage</c> for the page size. The margin ANCHORS are
/// re-derived, not hardcoded: the verified closed form for a full page is
/// <c>distance = 110 + (SidebarTabBudget mod pitch)</c>, so overlap ⟺ leftover &lt; 35 — the test
/// computes the anchors and asserts the distance directly, which is the form that cannot go stale.
/// </para>
///
/// <para>
/// The worst case is a FULL page, and the reason is arithmetic: the pager's centre is fixed at the
/// bottom, and a full page puts the last tab's centre at its lowest point. A partial last page
/// (fewer tabs) is strictly further away. The page count is pinned &gt; 1 so the probe cannot pass
/// vacuously on a window tall enough to show every tab (no pager at all).
/// </para>
///
/// Nothing is shown and no input is simulated. No UI category, so CI runs it.
/// </summary>
public class SettingsPagerClearanceTests
{
    /// <summary>
    /// The tab pager's hit circle must clear the last tab's by this much. Radii sum: a tab
    /// registers <see cref="SettingsWindow.TabBtnRadius"/>, a pager button
    /// <see cref="SettingsWindow.PagBtnRadius"/>.
    /// </summary>
    private static double RadiiSum => SettingsWindow.TabBtnRadius + SettingsWindow.PagBtnRadius;

    [Fact]
    public void TabPager_ClearsTheLastTab_AtThe1080pReference()
    {
        AssertClearance(screenHeightDip: 1080);
    }

    [Fact]
    public void TabPager_ClearsTheLastTab_AtA1200DipHeight()
    {
        AssertClearance(screenHeightDip: 1200);
    }

    private static void AssertClearance(double screenHeightDip)
    {
        var root = SettingsContentFitTests.Xml();
        var windowHeight = SettingsContentFitTests.WindowHeightFor(screenHeightDip);

        var closeMargin = SettingsContentFitTests.Margin(root, "CloseButton");
        var closeHeight = SettingsContentFitTests.FixedHeight(root, "CloseButton");
        var tabPanel = SettingsContentFitTests.Margin(root, "TabButtonPanel");
        var pagerPanel = SettingsContentFitTests.Margin(root, "TabPagerPanel");

        // SidebarTabBudget, every term read off the live XAML + production consts.
        var closeBlock = closeHeight + closeMargin.Top + closeMargin.Bottom;
        var pagerReserve = SettingsWindow.PaginationButtonHeight
            + 2 * SettingsWindow.PaginationButtonMargin
            + pagerPanel.Top + pagerPanel.Bottom;
        var budget = windowHeight - closeBlock - pagerReserve - tabPanel.Top - tabPanel.Bottom;

        var pitch = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;
        var perPage = SettingsTabPagination.TabsPerPage(budget, pitch);
        var tabCount = SettingsTabDriftTests.RenderedTabIds().Count;
        var pageCount = SettingsTabPagination.PageCount(tabCount, perPage);
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP display the sidebar budget ({1:F2} DIP) fits all {2} tabs on one "
                + "page, so there is no tab pager and this clearance probe would pass vacuously. "
                + "Pick a shorter display for the guard, or re-derive what it is checking.",
                screenHeightDip, budget, tabCount))
            .That(pageCount).IsStrictlyGreaterThan(1);

        // A FULL page is the worst case: the last tab's centre is at its lowest.
        var lastTabIndexOnPage = Math.Min(perPage, tabCount) - 1;
        var lastTabCentre = closeBlock + tabPanel.Top
            + lastTabIndexOnPage * pitch
            + SettingsWindow.TabButtonMargin
            + SettingsWindow.TabContainerHeight / 2;

        // The pager row is Auto-height at the sidebar's bottom: its reserve block starts at
        // windowHeight - pagerReserve, the panel's top margin sits inside it, and the button's
        // own margin + half its height put its centre here.
        var pagerCentre = windowHeight - pagerReserve + pagerPanel.Top
            + SettingsWindow.PaginationButtonMargin
            + SettingsWindow.PaginationButtonHeight / 2;

        var distance = pagerCentre - lastTabCentre;
        var radii = RadiiSum;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP display (window {1:F2}) the tab pager's hit circle centre is "
                + "{2:F2} DIP below the last tab's, but the two circles need {3:F2} (85 + 60). "
                + "The overlap is {4:F2} DIP: the nearest-target arbitration between a tab and "
                + "the pager flips mid-dwell. Budget {5:F2}, per-page {6}, full-page last tab at "
                + "{7:F2}, pager at {8:F2}. Shorten the tab list's page (the chunker), narrow "
                + "PaginationButtonHeight, or move the pager — do NOT widen this assertion.",
                screenHeightDip, windowHeight, distance, radii, radii - distance,
                budget, perPage, lastTabCentre, pagerCentre))
            .That(distance).IsGreaterOrEqualThan(radii);
    }
}
