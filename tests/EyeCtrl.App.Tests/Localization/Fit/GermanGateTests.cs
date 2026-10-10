namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// T10: T09's binding layout gates, run on the German draft under a TEST manifest that switches
/// every surface on (<see cref="GermanDraft"/>). The shipped manifest stays empty — these rehearse
/// T11–T13's switch-on in the test process only.
///
/// <para>
/// Covered here: the draft is complete and every surface becomes effective; every Settings page
/// paginates and stays reachable; the sidebar keeps every tab where it was; the pager fits; the
/// TrackStatus rows fit. The per-class fit is <see cref="GermanFitTests"/>; geometry invariance and
/// the Settings modal's exception take German as one more table in
/// <see cref="GeometryInvarianceTests"/> and <see cref="SettingsModalExceptionTests"/>; the
/// Supervisor's classes and setup window are checked in EyeCtrl.Supervisor.Tests.
/// </para>
///
/// <para>
/// The Settings sources read <see cref="StringTable.Current"/>, so the German table is activated
/// globally: the exclusive "StringTable Tests (App)" collection, with English restored after each.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class GermanGateTests
{
    private readonly ITestOutputHelper _output;

    public GermanGateTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every English key has German, the test table loads it without a warning, and every surface
    /// becomes effective — so the gates below measure German everywhere, not English fallbacks.
    /// </summary>
    [Fact]
    public void TheDraft_CoversEveryKey_AndEverySurfaceSwitchesOn()
    {
        Check.WithCustomMessage("Keys with no German: " + string.Join(", ", GermanDraft.MissingKeys()))
            .That(GermanDraft.MissingKeys()).IsEmpty();
        Check.WithCustomMessage("The German test table warned while loading:\n  " + string.Join("\n  ", GermanDraft.LoadWarnings))
            .That(GermanDraft.LoadWarnings).IsEmpty();

        var table = GermanDraft.Table;
        var notGerman = new List<string>();
        foreach (var surface in GermanDraft.TestManifest)
        {
            if ((table.FormatProviderFor(surface) as System.Globalization.CultureInfo)?.Name != "de-DE") notGerman.Add($"{surface}: formats invariant, so it is not effective");
        }

        // A sample per surface resolves the German text, not the English.
        foreach (var key in new[] { "supervisor.fallback.title", "app-tray.exit", "caregiver-alert.title", "drift-notice.message",
                     "startup-error.title", "track-status.title", "language-recovery.prompt.restart", "settings.tab.general.label",
                     "calibration.window.retry", "eye-care.banner.message", "overlays.ring.leftClick" })
        {
            if (table.T(key) == StringTable.EmbeddedEnglish.T(key)) notGerman.Add($"{key} resolves its English \"{table.T(key)}\"");
        }

        Check.WithCustomMessage(string.Join("\n", notGerman)).That(notGerman).IsEmpty();
    }

    /// <summary>Binding: under German every Settings page paginates and every row stays reachable.</summary>
    [StaFact]
    public void EverySettingsPage_InGerman_PaginatesAndStaysReachable()
    {
        StringTable.Activate(GermanDraft.Table);
        try
        {
            Check.That(StringTable.Current.T("settings.tab.general.label")).IsEqualTo("Allgemein");
            HandChunkedPageFitTests.AssertEveryPageReachable("German");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>Every theme × both row forms: two-column at the production column, stacked at the narrowest.</summary>
    public static TheoryData<string, string> ThemesAndRowForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in ThemeRegistry.Names)
        {
            data.Add(theme, "two-column");
            data.Add(theme, "stacked");
        }

        return data;
    }

    /// <summary>
    /// Binding (the coordinator's call after mutant M10c): every German text on every Settings page
    /// shows whole sideways — a wrapping row name, description or note has no unbreakable run (a
    /// U+00AD break counts, with its hyphen) wider than its column, and a one-line text is not
    /// clipped. Both row forms, in every theme. See <see cref="HandChunkedPageFitTests.RowTextOverruns"/>.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(ThemesAndRowForms))]
    public void EverySettingsText_InGerman_ShowsWholeSideways(string theme, string form) =>
        AssertRowTextsShowWhole(GermanDraft.Table, theme, form);

    /// <summary>The same sweep on the English: the baseline the German gate is measured against.</summary>
    [StaTheory]
    [MemberData(nameof(ThemesAndRowForms))]
    public void EverySettingsText_InEnglish_ShowsWholeSideways(string theme, string form) =>
        AssertRowTextsShowWhole(StringTable.EmbeddedEnglish, theme, form);

    private void AssertRowTextsShowWhole(StringTable strings, string theme, string form)
    {
        var width = form == "stacked"
            ? HandChunkedPageFitTests.StackedColumnWidth
            : HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
        Check.WithCustomMessage($"The {form} column ({FitText.Dip(width)} DIP) does not give the {form} row form.")
            .That(width >= SettingsWindow.TwoColumnMinimumWidth).IsEqualTo(form == "two-column");

        using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
        StringTable.Activate(strings);
        try
        {
            var findings = HandChunkedPageFitTests.RowTextOverruns(width, out var measured);
            _output.WriteLine($"{theme}, {form} ({FitText.Dip(width)} DIP): {measured} texts measured, {findings.Count} overrun");
            Check.WithCustomMessage("Too few Settings texts were measured: the sweep is not seeing the pages.")
                .That(measured).IsStrictlyGreaterThan(300);
            Check.WithCustomMessage($"Settings texts overrun their column ({theme}, {form}):\n  " + string.Join("\n  ", findings))
                .That(findings).IsEmpty();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: every German tab label stays on one line in production's tab face, keeps the
    /// sidebar's pitch, and leaves the tab's box exactly where the English one is.
    /// </summary>
    [StaFact]
    public void TheSidebar_InGerman_KeepsEveryTabWhereItWas()
    {
        var problems = new List<string>();
        var translated = 0;
        var pitch = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;
        foreach (var tab in SettingsTabDriftTests.RenderedTabDefs())
        {
            var german = GermanDraft.Table.T(tab.LabelKey);
            Check.That(german).IsNotEqualTo(tab.LabelKey);
            if (german != tab.Label) translated++;
            var english = SidebarTabFit.Measure(tab.Icon, tab.Label);
            var measured = SidebarTabFit.Measure(tab.Icon, german);
            _output.WriteLine($"{tab.LabelKey}: \"{german}\" box {measured.BorderBox} (English {english.BorderBox})");
            if (!measured.LabelOneLine)
                problems.Add($"{tab.LabelKey}: \"{german}\" wraps or is taller than one line ({FitText.Dip(measured.LabelHeight)})");
            if (Math.Abs(measured.BorderBox.Height + 2 * SettingsWindow.TabButtonMargin - pitch) > 1e-6)
                problems.Add($"{tab.LabelKey}: the tab's pitch changed");
            if (measured.BorderBox != english.BorderBox)
                problems.Add($"{tab.LabelKey}: the tab moved, {english.BorderBox} → {measured.BorderBox}");
        }

        Check.WithCustomMessage("Every tab read its English: the German table is not effective, so nothing was measured.")
            .That(translated).IsStrictlyGreaterThan(10);
        Check.WithCustomMessage("The German labels changed the sidebar:\n  " + string.Join("\n  ", problems)).That(problems).IsEmpty();
    }

    /// <summary>Binding: the pager's row fits in German, and its buttons have German names.</summary>
    [StaFact]
    public void ThePager_InGerman_FitsItsRow_AndNamesItsButtons()
    {
        StringTable.Activate(GermanDraft.Table);
        try
        {
            var indicator = SettingsWindow.BuildPageIndicator(99, 99);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(indicator);
            using var host = FitHost.Open(panel, family: Tokens.FontStandard);
            var indicatorWidth = indicator.ActualWidth + indicator.Margin.Left + indicator.Margin.Right;
            var buttons = 2 * (SettingsWindow.PaginationButtonWidth + 2 * SettingsWindow.PaginationButtonMargin);
            var column = HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
            Check.That(buttons + indicatorWidth).IsLessOrEqualThan(column);

            foreach (var id in new[] { "pag_prev", "pag_next", "tab_prev", "tab_next" })
            {
                foreach (var boundary in new[] { false, true })
                {
                    var key = SettingsPagerChrome.AccessibleNameKey(id, boundary);
                    Check.WithCustomMessage($"{id} (boundary {boundary}) is not German.")
                        .That(StringTable.Current.T(key)).IsNotEqualTo(StringTable.EmbeddedEnglish.T(key));
                }
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: the drift notice — a crisis surface — shows its whole message. The window is a fixed
    /// 560 DIP, and the message is ONE line (no wrapping) classed <c>row</c>, which no T09 class
    /// measures: a longer translation would be clipped at the banner's edge. English is the
    /// baseline; an over-long message must fail (the teeth).
    /// </summary>
    [StaFact]
    public void TheDriftNotice_ShowsItsWholeMessage_InEnglishAndGerman()
    {
        var english = DriftNotice(StringTable.EmbeddedEnglish.T("drift-notice.message"));
        var german = DriftNotice(GermanDraft.Table.T("drift-notice.message"));
        var overLong = DriftNotice(string.Join(" ", Enumerable.Repeat(GermanDraft.Table.T("drift-notice.message"), 2)));
        _output.WriteLine($"English: {english}");
        _output.WriteLine($"German: {german}");
        Check.WithCustomMessage("The drift-notice gate passed a doubled message: it cannot fail.").That(overLong.Fits).IsFalse();
        Check.WithCustomMessage(english.ToString()).That(english.Fits).IsTrue();
        Check.WithCustomMessage(german.ToString()).That(german.Fits).IsTrue();
    }

    /// <summary>The drift notice's content at 2.0 in its own 560 × 72 window: the message's one line must fit the banner inside it.</summary>
    private static FitVerdict DriftNotice(string message)
    {
        EyeCtrl.App.Tests.Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new DriftNoticeWindow();
        window.Message.Text = message;
        var root = (System.Windows.FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: window.Width, height: window.Height, inheritFrom: window);
        var banner = window.Banner;
        var natural = FitText.NaturalWidth(window.Message, message);
        var room = window.Width - banner.Padding.Left - banner.Padding.Right
                   - banner.BorderThickness.Left - banner.BorderThickness.Right;
        var shown = window.Message.ActualWidth;
        return new FitVerdict(natural <= room && natural <= shown + 0.5,
            $"one line {FitText.Dip(natural)} DIP; shown {FitText.Dip(shown)}; room in the {FitText.Dip(window.Width)} DIP window {FitText.Dip(room)}",
            FitText.ResolvedFace(window.Message), room - natural);
    }

    /// <summary>Binding: every German TrackStatus row fits the fixed window's width (de-DE digits included).</summary>
    [StaFact]
    public void EveryTrackStatusRow_InGerman_FitsTheWindowWidth()
    {
        var german = GermanDraft.Table;
        Check.WithCustomMessage("The TrackStatus title resolved its English: the German table is not effective.")
            .That(german.T("track-status.title")).IsNotEqualTo(StringTable.EmbeddedEnglish.T("track-status.title"));
        var failures = new List<string>();
        foreach (var quality in Enum.GetValues<DistanceQuality>())
        {
            var verdict = TrackStatusWidthTests.Measure(
                german.T("track-status.title"),
                german.T("track-status.distance"),
                german.T("track-status.distance.mm", 1000.0),
                german.T("track-status.quality"),
                TrackStatusWindow.QualityWord(german, quality));
            _output.WriteLine($"{quality}: {verdict}");
            if (!verdict.Fits) failures.Add($"{quality}: {verdict}");
        }

        Check.WithCustomMessage("German TrackStatus rows overrun the window width:\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }
}
