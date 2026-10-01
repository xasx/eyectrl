namespace EyeCtrl.App.Tests.Settings;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using EyeCtrl.App.Tests;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The sidebar's tab pager sits at the sidebar's TOP since Phase 2 A3 — a vertical stack
/// (tab_prev above tab_next) between the close button and the tab list — and all three
/// register overlapping-capable hit circles: 85 DIP for the close button and each tab
/// (<see cref="SettingsWindow.TabBtnRadius"/>), 60 for a pager button
/// (<see cref="SettingsWindow.PagBtnRadius"/>). Nothing about the layout keeps them apart
/// except arithmetic, and since Phase 1 the tab pager rides DwellCategory.Pagination while
/// the close button and the tabs ride Tab — DIFFERENT detectors accrue CONCURRENTLY, so
/// nearest-wins no longer masks an overlap. Every pair must have real circle clearance.
///
/// <para>
/// Every term is derived from the live XAML through <see cref="SettingsContentFitTests"/>'s
/// own readers plus production's consts, exactly the discipline the other fit suites use:
/// <c>CloseButton</c>'s Height/Width/Margin and right alignment, <c>TabPagerPanel</c>'s and
/// <c>TabButtonPanel</c>'s Margins, the sidebar's RowDefinitions,
/// <see cref="SettingsWindow.TabContainerHeight"/>/<see cref="SettingsWindow.TabButtonMargin"/>,
/// <see cref="SettingsWindow.PaginationButtonWidth"/>/<see cref="SettingsWindow.PaginationButtonHeight"/>,
/// and <see cref="SettingsWindow.TabPagerButtonThickness"/> — the exact margin the buttons
/// are built with, whose vertical term keeps the stack's own two circles clear. The layout
/// ANCHORS the derivation
/// stands on — which grid row each block owns, the stack's orientation and left alignment —
/// are read from the XAML and asserted, not assumed: the debate's placement signal
/// (close centre (182, 58), pager clear at y ≥ 143) was itself derived from this XAML and is
/// re-derived here rather than trusted.
/// </para>
///
/// <para>
/// The sidebar RESERVE is read from production — <see cref="SettingsWindow.TabPagerStackReserve"/>
/// — never restated: a test-side copy of the formula pins nothing (phase-2 review r1 M1: a
/// one-button undercount survived the whole suite against a restated formula). The suite
/// instead derives the stack's REAL height from what <c>BuildTabPagePager</c> actually builds
/// (button count source-scanned × the button block) and pins production's reserve to it, and
/// pins the whole sidebar — close block, real stack, tab panel margins, a full page of tabs —
/// inside the window at both reference heights. With the pager ABOVE the list, the closest
/// TAB is the page's FIRST one, at a position that does not depend on the window height, so
/// that clearance pair is height-free; the reference heights remain as the non-vacuity guard
/// (page count &gt; 1 — on a window tall enough to fit every tab there is no pager and the
/// clearance would hold vacuously).
/// </para>
///
/// Nothing is shown and no input is simulated. No UI category, so CI runs it.
/// </summary>
public class SettingsPagerClearanceTests
{
    /// <summary>
    /// The clearance any two of these circles need: a tab and the close button register
    /// <see cref="SettingsWindow.TabBtnRadius"/>, a pager button
    /// <see cref="SettingsWindow.PagBtnRadius"/>.
    /// </summary>
    private static double RadiiSum => SettingsWindow.TabBtnRadius + SettingsWindow.PagBtnRadius;

    [Fact]
    public void TabPager_ClearsTheCloseButton()
    {
        // Height-free: both blocks sit at the sidebar's top, above everything whose position
        // depends on the window height. Load-bearing since Phase 1 — the pager accrues under
        // the Pagination detector while close accrues under Tab, CONCURRENTLY, so an overlap
        // would let one dwell steal the other mid-gaze instead of arbitrating.
        var root = SettingsContentFitTests.Xml();
        AssertLayoutAnchors(root);

        AssertClearance(CloseCentre(root), UpperPagerCentre(root),
            "close button", "upper pager button (tab_prev)");
    }

    [Fact]
    public void TabPager_ClearsTheFirstTab_AtThe1080pReference()
    {
        AssertTabClearance(screenHeightDip: 1080);
    }

    [Fact]
    public void TabPager_ClearsTheFirstTab_AtA1200DipHeight()
    {
        AssertTabClearance(screenHeightDip: 1200);
    }

    /// <summary>
    /// Phase-2 review r1 M1: production's sidebar reserve must equal the stack height the
    /// LAYOUT actually produces — one button block per button
    /// <c>BuildTabPagePager</c> adds (source-scanned, not assumed), plus the panel's own
    /// margins from the live XAML. A reserve that under-counts the stack chunks more tabs per
    /// page than fit below it, pushing the last tab's centre off-window where gaze cannot
    /// reach it — the mutant that survived against a restated formula.
    /// </summary>
    [Fact]
    public void TheSidebarReserve_MatchesTheRealStackHeight()
    {
        var root = SettingsContentFitTests.Xml();
        var pagerPanel = SettingsContentFitTests.Margin(root, "TabPagerPanel");

        var derivedStack = DerivedStackHeight(root);
        var reserve = SettingsWindow.TabPagerStackReserve(pagerPanel.Top, pagerPanel.Bottom);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "TabPagerStackReserve reports {0:F2} DIP but the layout's real pager stack is "
                + "{1:F2} DIP ({2} buttons x {3:F0} + panel margins {4:F0}/{5:F0}). The budget "
                + "chunks the tab list against the reserve, so an under-count packs tabs into "
                + "space the stack occupies — the last tab renders off-window, unreachable by "
                + "gaze. Fix the reserve derivation (it must count every button "
                + "BuildTabPagePager adds); do NOT change this assertion.",
                reserve, derivedStack, PagerButtonCount(),
                PagerButtonBlockHeight(), pagerPanel.Top, pagerPanel.Bottom))
            .That(reserve).IsEqualTo(derivedStack);
    }

    private static void AssertTabClearance(double screenHeightDip)
    {
        var root = SettingsContentFitTests.Xml();
        AssertLayoutAnchors(root);

        var windowHeight = SettingsContentFitTests.WindowHeightFor(screenHeightDip);

        // SidebarTabBudget, every term read off the live XAML + production consts. The pager
        // term is PRODUCTION's reserve — the number the tab list is actually chunked against.
        var closeBlock = SettingsContentFitTests.FixedHeight(root, "CloseButton")
            + SettingsContentFitTests.Margin(root, "CloseButton").Top
            + SettingsContentFitTests.Margin(root, "CloseButton").Bottom;
        var pagerPanel = SettingsContentFitTests.Margin(root, "TabPagerPanel");
        var reserve = SettingsWindow.TabPagerStackReserve(pagerPanel.Top, pagerPanel.Bottom);
        var budget = windowHeight
            - closeBlock
            - reserve
            - SettingsContentFitTests.Margin(root, "TabButtonPanel").Top
            - SettingsContentFitTests.Margin(root, "TabButtonPanel").Bottom;

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

        // Window fit (review r1 M1): close block + the REAL stack height + the tab panel's
        // margins + a FULL page of tabs must fit the window. Deliberately the layout-derived
        // stack, not production's reserve: an under-counted reserve would price more tabs per
        // page than the real stack leaves room for, and self-consistently pass.
        var stackHeight = DerivedStackHeight(root);
        var tabPanel = SettingsContentFitTests.Margin(root, "TabButtonPanel");
        var sidebarHeight = closeBlock + stackHeight
            + tabPanel.Top + tabPanel.Bottom
            + perPage * pitch;
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP display (window {1:F2}) the sidebar needs {2:F2} DIP — close "
                + "block {3:F0} + real pager stack {4:F2} + tab panel margins {5:F0}/{6:F0} + a "
                + "full page of {7} tabs ({8:F0}) — but the window is only {1:F2} DIP tall. The "
                + "last tab's centre is off-window: unreachable by gaze. The chunker priced the "
                + "page against a reserve that does not match the real stack; fix the reserve, "
                + "do NOT widen this assertion.",
                screenHeightDip, windowHeight, sidebarHeight, closeBlock, stackHeight,
                tabPanel.Top, tabPanel.Bottom, perPage, perPage * pitch))
            .That(sidebarHeight).IsLessOrEqualThan(windowHeight);

        // With the pager ABOVE the list, the closest tab is the page's FIRST one — its
        // position is fixed (every block above it is), so this pair does not need the
        // full-page worst case the bottom-pager layout priced. The closest pager button is
        // the stack's LOWER one.
        AssertClearance(LowerPagerCentre(root), FirstTabCentre(root, closeBlock, stackHeight),
            "lower pager button (tab_next)", "first tab of the list");
    }

    // ────── Derivation helpers — every number comes from the live XAML or a production const ──────

    /// <summary>
    /// Asserts the layout the derivations below stand on: close / pager stack / tab list own
    /// sidebar rows 0/1/2, and the stack is vertical and left-aligned. If the XAML moves, this
    /// fails BEFORE the clearance numbers silently stop meaning anything.
    /// </summary>
    private static void AssertLayoutAnchors(XElement root)
    {
        var sidebar = SettingsContentFitTests.Named(root, "CloseButton").Parent;
        Check.WithCustomMessage(
                "CloseButton's parent is gone; the sidebar grid is what the row anchors below "
                + "are read from.")
            .That(sidebar).IsNotNull();

        Check.WithCustomMessage(
                $"The sidebar declares RowDefinitions=\"{sidebar!.Attribute("RowDefinitions")?.Value}\", "
                + "expected \"Auto, Auto, *\" — close button, pager stack, tab list (Phase 2 A3).")
            .That((string?)sidebar.Attribute("RowDefinitions")).IsEqualTo("Auto, Auto, *");

        Check.That((string?)SettingsContentFitTests.Named(root, "CloseButton").Attribute("Grid.Row"))
            .IsEqualTo("0");
        Check.That((string?)SettingsContentFitTests.Named(root, "TabPagerPanel").Attribute("Grid.Row"))
            .IsEqualTo("1");
        Check.That((string?)SettingsContentFitTests.Named(root, "TabButtonPanel").Attribute("Grid.Row"))
            .IsEqualTo("2");

        var pager = SettingsContentFitTests.Named(root, "TabPagerPanel");
        Check.WithCustomMessage(
                "TabPagerPanel is no longer a vertical left-aligned stack; the clearance "
                + "derivations below assume that shape. Re-derive them for the new layout "
                + "rather than loosening these anchors.")
            .That((string?)pager.Attribute("Orientation")).IsEqualTo("Vertical");
        Check.That((string?)pager.Attribute("HorizontalAlignment")).IsEqualTo("Left");
    }

    /// <summary>The close button's centre, from its right alignment, margin and fixed size.</summary>
    private static (double X, double Y) CloseCentre(XElement root)
    {
        var close = SettingsContentFitTests.Named(root, "CloseButton");
        var margin = SettingsContentFitTests.Margin(root, "CloseButton");
        var width = double.Parse(close.Attribute("Width")!.Value, CultureInfo.InvariantCulture);
        var height = SettingsContentFitTests.FixedHeight(root, "CloseButton");

        return (
            SettingsContentFitTests.SidebarWidth(root) - margin.Right - width / 2,
            margin.Top + height / 2);
    }

    /// <summary>
    /// The UPPER pager button's centre: the close button is above the stack, so this is the
    /// pager button closest to it. The centre offsets derive from
    /// <see cref="SettingsWindow.TabPagerButtonThickness"/> — the exact margin the buttons are
    /// built with — so swapping the thickness's shape fails here instead of silently moving
    /// the circles into the close button's (review r2 M1).
    /// </summary>
    private static (double X, double Y) UpperPagerCentre(XElement root)
    {
        var pagerPanel = SettingsContentFitTests.Margin(root, "TabPagerPanel");
        var closeBlock = SettingsContentFitTests.FixedHeight(root, "CloseButton")
            + SettingsContentFitTests.Margin(root, "CloseButton").Top
            + SettingsContentFitTests.Margin(root, "CloseButton").Bottom;
        var thickness = SettingsWindow.TabPagerButtonThickness;

        return (
            pagerPanel.Left + thickness.Left + SettingsWindow.PaginationButtonWidth / 2,
            closeBlock + pagerPanel.Top + thickness.Top + SettingsWindow.PaginationButtonHeight / 2);
    }

    /// <summary>The pager button closest to the tab list: the stack's LOWER one.</summary>
    private static (double X, double Y) LowerPagerCentre(XElement root)
    {
        var (x, upperY) = UpperPagerCentre(root);
        return (x, upperY + PagerButtonBlockHeight());
    }

    /// <summary>
    /// The FIRST tab's centre — with the pager above the list, the closest tab there is. Its
    /// position is fixed: every block above it (close button, pager stack) is height-free.
    /// The tab Border stretches the sidebar column (no Width of its own), so its centre X is
    /// derived from the panel's margins and the sidebar width.
    /// </summary>
    private static (double X, double Y) FirstTabCentre(
        XElement root, double closeBlock, double stackHeight)
    {
        var tabPanel = SettingsContentFitTests.Margin(root, "TabButtonPanel");
        var tabWidth = SettingsContentFitTests.SidebarWidth(root)
            - tabPanel.Left - tabPanel.Right - 2 * SettingsWindow.TabButtonMargin;
        return (
            tabPanel.Left + SettingsWindow.TabButtonMargin + tabWidth / 2,
            closeBlock + stackHeight + tabPanel.Top
            + SettingsWindow.TabButtonMargin + SettingsWindow.TabContainerHeight / 2);
    }

    /// <summary>
    /// One stacked pager button's vertical block: height plus ITS OWN margins, read from the
    /// <see cref="SettingsWindow.TabPagerButtonThickness"/> the buttons are actually built
    /// with — not from the consts restated.
    /// </summary>
    private static double PagerButtonBlockHeight() =>
        SettingsWindow.PaginationButtonHeight
        + SettingsWindow.TabPagerButtonThickness.Top + SettingsWindow.TabPagerButtonThickness.Bottom;

    /// <summary>
    /// The stack's REAL height, derived from the layout: one button block per button
    /// <c>BuildTabPagePager</c> actually adds (its body is source-scanned for the
    /// <c>MakeTabPagerButton</c> calls — the count is read from production, not assumed),
    /// plus the panel's own margins from the live XAML. Deliberately NOT
    /// <see cref="SettingsWindow.TabPagerStackReserve"/> — the reserve-equals-stack pin holds
    /// the two against each other, so this side must not be production's own derivation.
    /// Both sides DO share the per-button block (both read
    /// <see cref="SettingsWindow.TabPagerButtonThickness"/> — one margin story, review r2
    /// M1), so a shape swap passes THIS pin by construction; the clearance pins and the pitch
    /// pin are what kill shape mutations, because they turn the thickness into distances and
    /// compare those against the radii the concurrent-detector rider demands.
    /// </summary>
    private static double DerivedStackHeight(XElement root)
    {
        var pagerPanel = SettingsContentFitTests.Margin(root, "TabPagerPanel");
        return PagerButtonCount() * PagerButtonBlockHeight()
            + pagerPanel.Top + pagerPanel.Bottom;
    }

    /// <summary>
    /// How many pager buttons <c>BuildTabPagePager</c> builds, read out of its source body.
    /// </summary>
    private static int PagerButtonCount()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var start = source.IndexOf("private void BuildTabPagePager(", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "SettingsWindow.xaml.cs no longer declares BuildTabPagePager. It is what builds "
                + "the tab pager's buttons; repoint the stack-height derivation at whatever "
                + "replaced it rather than hardcoding a count.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(start);
        var body = source[start..end];
        return body.Split("MakeTabPagerButton(").Length - 1;
    }

    private static void AssertClearance(
        (double X, double Y) from, (double X, double Y) to, string fromName, string toName)
    {
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var radii = RadiiSum;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {1}'s hit circle centre is {3:F2} DIP from the {0}'s, but the two circles "
                + "need {2:F2} ({4:F0} + {5:F0}). The overlap is {6:F2} DIP, and since Phase 1 "
                + "the tab pager rides Pagination while the close button and the tabs ride Tab "
                + "— different detectors accrue CONCURRENTLY, so nearest-wins no longer masks "
                + "the overlap and the arbitration flips mid-dwell. Widen the stack's margins, "
                + "shrink the buttons, or move the pager — do NOT widen this assertion.",
                fromName, toName, radii, distance,
                SettingsWindow.TabBtnRadius, SettingsWindow.PagBtnRadius, radii - distance))
            .That(distance).IsGreaterOrEqualThan(radii);
    }
}
