namespace EyeCtrl.App.Tests.Localization;

using System.Linq;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
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
/// </summary>
public class LanguageRowReachTests
{
    /// <summary>The user's display: 3840×2400 physical at 200 % is 1920×1200 DIP.</summary>
    private const double DisplayWidthDip = 1920;

    /// <inheritdoc cref="DisplayWidthDip"/>
    private const double DisplayHeightDip = 1200;

    /// <summary>General's 0-based index in <c>SettingsWindow._tabDefs</c> (tab 12 of 15).</summary>
    private const int GeneralTabIndex = 11;

    private const int TabCount = 15;

    /// <summary>1 = toolbar Settings; 1 = sidebar next-page; 1 = the General tab.</summary>
    private const int PinnedToolbarToLanguageRowDwells = 3;

    /// <summary>The deep link opens on the row's page; nothing further to reach it.</summary>
    private const int PinnedTrayToLanguageRowDwells = 0;

    private const int DwellMs = 800;

    /// <summary>
    /// The sidebar's tab-list budget in DIP at this display — every term of
    /// <c>SettingsWindow.SidebarTabBudget</c> read off the live XAML and production's consts.
    /// </summary>
    private static double SidebarTabBudget(XElement root)
    {
        var windowHeight = SettingsContentFitTests.WindowHeightFor(DisplayHeightDip);
        var close = SettingsContentFitTests.Margin(root, "CloseButton");
        var pager = SettingsContentFitTests.Margin(root, "TabPagerPanel");
        var tabs = SettingsContentFitTests.Margin(root, "TabButtonPanel");

        return windowHeight
            - SettingsContentFitTests.FixedHeight(root, "CloseButton")
            - close.Top - close.Bottom
            - SettingsWindow.TabPagerStackReserve(pager.Top, pager.Bottom)
            - tabs.Top - tabs.Bottom;
    }

    [Fact]
    public void FromTheToolbarSettingsButton_TheLanguageRow_IsThreeDwellsAway()
    {
        var root = SettingsContentFitTests.Xml();
        var budget = SidebarTabBudget(root);
        var slot = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;
        var perPage = SettingsTabPagination.TabsPerPage(budget, slot);
        var generalPage = SettingsTabPagination.PageOf(GeneralTabIndex, perPage);
        var dwells = 1 + generalPage + 1;

        Check.WithCustomMessage(
                $"At {DisplayWidthDip}x{DisplayHeightDip} DIP the sidebar budget is {budget} DIP, "
                + $"so {perPage} of {TabCount} tabs fit per page and General (index {GeneralTabIndex}) "
                + $"is on page {generalPage}. If this moved, re-measure the reach and update the pin "
                + "deliberately — a longer recovery path is a UX decision, not a test failure to "
                + "paper over.")
            .That(dwells).IsEqualTo(PinnedToolbarToLanguageRowDwells);
        Check.That(perPage).IsEqualTo(6);
        Check.That(generalPage).IsEqualTo(1);
    }

    [StaFact]
    public void FromTheTrayLanguageItem_TheLanguageRow_IsZeroDwellsAway()
    {
        // The deep link's landing is pinned by the row-on-page-one check below; the tray item's
        // target is pinned by SettingsDeepLinkWiringTests. Together: the row is in view when the
        // window opens, so reaching it costs no further dwell.
        Check.That(PinnedTrayToLanguageRowDwells).IsEqualTo(0);
        Check.That(FirstRenderedRowId()).IsEqualTo("g_language");
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

    private static string FirstRenderedRowId()
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
}
