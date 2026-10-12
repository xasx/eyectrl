namespace EyeCtrl.App.Tests.Localization;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T04's reach pin (critique-01 #18): how many dwells a gaze user needs to get the language row
/// in view, measured headlessly at the user's display — 3840×2400 physical at 200 %, i.e.
/// 1920×1200 DIP — and pinned. From the toolbar's Settings button the number is 3 (Settings; the
/// sidebar's next-page pager, because the sidebar shows 6 of 15 tabs at this height; the General
/// tab); from the tray's language item it is 0, because that item deep-links straight to General
/// page 1, whose first row is the language row.
///
/// <para>
/// "Reach" counts the selections needed to make the language row a LIVE target, not the row's own
/// activation (the choice and the restart prompt are two more dwells). General is tab 12 of 15 and
/// the sidebar paginates, so the count is not 1 — the number is the point of this file.
/// </para>
///
/// <para>
/// The tab index and count are read from the REAL <c>_tabDefs</c> literal (F8) — moving General
/// changes the computed dwell count and fails the pin instead of leaving it green — and the
/// sidebar budget goes through production's <see cref="SettingsWindow.SidebarTabBudgetFor"/>
/// rather than a formula restated here.
/// </para>
/// </summary>
public class LanguageRowReachTests
{
    /// <summary>The user's display: 3840×2400 physical at 200 % is 1920×1200 DIP.</summary>
    private const double DisplayWidthDip = 1920;

    /// <inheritdoc cref="DisplayWidthDip"/>
    private const double DisplayHeightDip = 1200;

    /// <summary>1 = toolbar Settings; 1 = sidebar next-page; 1 = the General tab.</summary>
    internal const int PinnedToolbarToLanguageRowDwells = 3;

    private const int DwellMs = 800;

    /// <summary>
    /// General's 0-based index and the tab count, read from <c>SettingsWindow._tabDefs</c> — the
    /// real list, so a tab moved in front of General re-measures the reach (F8).
    /// </summary>
    internal static (int GeneralIndex, int TabCount) TabDefsFromSource()
    {
        var ids = TabIdsFromSource();
        return (ids.ToList().IndexOf("General"), ids.Count);
    }

    /// <summary>The tab ids of <c>SettingsWindow._tabDefs</c>, in declaration order.</summary>
    internal static IReadOnlyList<string> TabIdsFromSource()
    {
        var source = File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"))
            .Replace("\r\n", "\n");
        var start = source.IndexOf("_tabDefs =\n", StringComparison.Ordinal);
        var open = source.IndexOf('[', start);
        var close = source.IndexOf("];", open, StringComparison.Ordinal);
        return Regex.Matches(source[open..close], "\\(\\s*\"([A-Za-z]+)\"\\s*,")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    /// <summary>
    /// The sidebar's tab-list budget in DIP, through production's own formula and every term read
    /// off the live XAML element it belongs to.
    /// </summary>
    internal static double SidebarTabBudget(XElement root)
    {
        var close = SettingsContentFitTests.Margin(root, "CloseButton");
        var pager = SettingsContentFitTests.Margin(root, "TabPagerPanel");
        var tabs = SettingsContentFitTests.Margin(root, "TabButtonPanel");

        return SettingsWindow.SidebarTabBudgetFor(
            SettingsContentFitTests.WindowHeightFor(DisplayHeightDip),
            SettingsContentFitTests.FixedHeight(root, "CloseButton") + close.Top + close.Bottom,
            SettingsWindow.TabPagerStackReserve(pager.Top, pager.Bottom),
            tabs.Top + tabs.Bottom);
    }

    /// <summary>
    /// The dwell count from the toolbar's Settings button to the language row — one implementation
    /// shared by the English pin and T12's shipped-German variant, so the arithmetic cannot drift
    /// between the two — plus the geometry behind it, so a failure can name the budget and the tab
    /// count instead of only the computed pages.
    ///
    /// <para>
    /// The arithmetic itself is language-independent: the sidebar geometry comes from the XAML and
    /// the tab labels keep their boxes (the tab156 sweep proves that). Its German teeth are the
    /// caller's: the count only lands on the row if the language row is still the first rendered
    /// row of General page 1 under German re-pagination.
    /// </para>
    /// </summary>
    internal sealed record ToolbarReach(int Dwells, int PerPage, int GeneralPage, double Budget, int TabCount);

    /// <inheritdoc cref="ToolbarReach"/>
    internal static ToolbarReach ToolbarToLanguageRow()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = SidebarTabBudget(root);
        var slot = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;
        var perPage = SettingsTabPagination.TabsPerPage(budget, slot);
        var (generalIndex, tabCount) = TabDefsFromSource();

        // Tripwires: a scan that found nothing must not compute a vacuous pass.
        Check.WithCustomMessage(
                "The _tabDefs scan found no tab ids — the SettingsWindow source shape moved; "
                + "repoint the scan rather than letting the reach be computed from nothing.")
            .That(tabCount).IsStrictlyGreaterThan(5);
        Check.That(generalIndex).IsStrictlyGreaterThan(-1);

        var generalPage = SettingsTabPagination.PageOf(generalIndex, perPage);
        return new ToolbarReach(1 + generalPage + 1, perPage, generalPage, budget, tabCount);
    }

    internal static string FirstRenderedRowId()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = HandChunkedPageFitTests.ContentHeightBudget(root, DisplayHeightDip);
        var columnWidth = HandChunkedPageFitTests.ContentColumnWidth(root, DisplayWidthDip);
        var rows = new GeneralPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var rendered = SettingsAutoPagination.Repaginate(
            rows, columnWidth, budget, SettingsWindow.ControlBtnRadius * 2, DwellMs);
        return ((ComboBoxSpec)rendered[0].Rows[0]).Id;
    }

    [Fact]
    public void FromTheToolbarSettingsButton_TheLanguageRow_IsThreeDwellsAway()
    {
        var reach = ToolbarToLanguageRow();

        Check.WithCustomMessage(
                $"At {DisplayWidthDip}x{DisplayHeightDip} DIP the sidebar budget is {reach.Budget} DIP, "
                + $"so {reach.PerPage} of {reach.TabCount} tabs fit per page and General is on page "
                + $"{reach.GeneralPage}. If this moved, re-measure the reach and update the pin "
                + "deliberately — a longer recovery path is a UX decision, not a test failure to "
                + "paper over.")
            .That(reach.Dwells).IsEqualTo(PinnedToolbarToLanguageRowDwells);
        Check.That(reach.PerPage).IsEqualTo(6);
        Check.That(reach.GeneralPage).IsEqualTo(1);
    }

    [StaFact]
    public void FromTheTrayLanguageItem_TheLanguageRow_IsZeroDwellsAway()
    {
        // Measured, not restated: the deep link DECIDES to open-then-navigate, and the language
        // row is the first row of the page production renders — so the dwell count is 0 by
        // computation.
        var action = SettingsDeepLink.Decide(windowOpen: false, SettingsTarget.GeneralPage1);
        var rowIsFirst = FirstRenderedRowId() == "g_language";
        var dwells = action == SettingsDeepLinkAction.OpenThenNavigate && rowIsFirst
            ? 0
            : int.MaxValue;

        Check.WithCustomMessage(
                "The tray item is no longer 0 dwells from the language row: the deep link must "
                + "decide OpenThenNavigate (SettingsDeepLink) and the row must be the first row "
                + "of rendered General page 1.")
            .That(dwells).IsEqualTo(0);
    }

    [StaFact]
    public void TheLanguageRow_IsTheFirstRowOnGeneralPageOne()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = HandChunkedPageFitTests.ContentHeightBudget(root, DisplayHeightDip);
        var columnWidth = HandChunkedPageFitTests.ContentColumnWidth(root, DisplayWidthDip);

        // The pages production actually renders: the source's rows through the real pipeline,
        // at the real window numbers for this display. The radius is the physical 2.0-scale one.
        var rows = new GeneralPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        var rendered = SettingsAutoPagination.Repaginate(
            rows, columnWidth, budget, SettingsWindow.ControlBtnRadius * 2, DwellMs);

        Check.That(rendered[0].Rows[0]).IsInstanceOf<ComboBoxSpec>();
        Check.That(((ComboBoxSpec)rendered[0].Rows[0]).Id).IsEqualTo("g_language");
    }
}
