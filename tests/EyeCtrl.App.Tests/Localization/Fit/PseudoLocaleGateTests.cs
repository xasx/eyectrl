namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Tests.Settings;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 2: the BINDING gates under the pseudo-locale (<see cref="PseudoLocale"/>) — before any
/// real German exists, a translation about 40 % longer must still leave every Settings row
/// reachable, every page non-empty, and the pager and the sidebar working.
///
/// <para>
/// The Settings sources build their rows from <see cref="StringTable.Current"/>, so these tests
/// activate the pseudo-locale globally — hence the exclusive "StringTable Tests (App)" collection;
/// the assembly-level <c>StringTableRestore</c> hook puts English back after every test. The
/// sweep itself is <see cref="HandChunkedPageFitTests.AssertEveryPageReachable"/>: one rule in
/// both languages, run through production's own pagination (<c>SettingsAutoPagination.Repaginate</c>
/// at the production budget).
/// </para>
///
/// <para>
/// <b>Expected:</b> the user's learned page positions change once when German arrives (the
/// pages re-chunk per language). That is the one place translated text may move a target, and it
/// never happens under a live dwell — the invariant these gates protect is that every row is
/// still reachable on SOME page.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class PseudoLocaleGateTests
{
    private static readonly Regex Placeholder = new(@"\{\d+(?::[^{}]*)?\}", RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public PseudoLocaleGateTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The pseudo-locale is what it claims: every text bracketed and accented, about 40 % longer,
    /// and every placeholder kept — so every key still formats, and a gate that passes under it is
    /// a gate that measured long text.
    /// </summary>
    [Fact]
    public void ThePseudoLocale_IsLongerAccentedBracketed_AndKeepsEveryPlaceholder()
    {
        var english = FitCatalog.FitByKey().Keys.ToList();
        Check.WithCustomMessage("The English table is empty: the pseudo-locale would be too.").That(english.Count).IsStrictlyGreaterThan(600);

        var englishLength = 0;
        var pseudoLength = 0;
        var problems = new List<string>();
        foreach (var fitClass in StringTableSchema.FitClasses)
        {
            var items = FitCatalog.ItemsOf(fitClass);
            var pseudo = PseudoLocale.ItemsOf(fitClass);
            for (var i = 0; i < items.Count; i++)
            {
                var en = items[i].Text;
                var ps = pseudo[i].Text;
                englishLength += en.Length;
                pseudoLength += ps.Length - 2;
                if (!ps.StartsWith('[') || !ps.EndsWith(']')) problems.Add($"{items[i]}: not bracketed: {ps}");
                if (en.Any(char.IsLetter) && ps == "[" + en + "]") problems.Add($"{items[i]}: not transformed: {ps}");
                var enHoles = Placeholder.Matches(en).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
                var psHoles = Placeholder.Matches(ps).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
                if (!enHoles.SequenceEqual(psHoles)) problems.Add($"{items[i]}: placeholders differ: {en} → {ps}");
            }
        }

        // Every key formats under the pseudo table with as many arguments as it declares.
        foreach (var fitClass in StringTableSchema.FitClasses)
        {
            foreach (var item in FitCatalog.ItemsOf(fitClass).Where(i => i.Form is "" or "other"))
            {
                try
                {
                    var args = item.Args.Select(_ => (object?)2.0).ToArray();
                    var text = PseudoLocale.Table.T(item.Key, args);
                    if (!text.StartsWith('[')) problems.Add($"{item.Key}: the pseudo table resolved \"{text}\"");
                }
                catch (FormatException ex)
                {
                    problems.Add($"{item.Key}: does not format under the pseudo-locale: {ex.Message}");
                }
            }
        }

        var ratio = (double)pseudoLength / englishLength;
        _output.WriteLine($"English {englishLength} characters, pseudo-locale {pseudoLength} (×{ratio:0.###}); "
                          + $"sample: {PseudoLocale.Table.T("settings.tab.quickactions.label")}");
        Check.WithCustomMessage(string.Join("\n", problems)).That(problems).IsEmpty();
        Check.WithCustomMessage($"The pseudo-locale is ×{ratio:0.###} the English, not about ×1.4.")
            .That(ratio).IsStrictlyGreaterThan(1.3).And.IsStrictlyLessThan(1.6);
    }

    /// <summary>
    /// Binding: under the pseudo-locale every Settings source, in every view and fixture, is
    /// paginated by production (<c>Repaginate</c> at the production budget), the chunking ends with
    /// no empty page, and every dwell circle on every page lies wholly inside the content cell,
    /// clear of its neighbours by the 40 DIP floor, with unique ids.
    /// </summary>
    [StaFact]
    public void EverySettingsPage_UnderThePseudoLocale_PaginatesAndStaysReachable()
    {
        StringTable.Activate(PseudoLocale.Table);
        try
        {
            Check.That(StringTable.Current.T("settings.tab.general.label")).StartsWith("[");
            HandChunkedPageFitTests.AssertEveryPageReachable("pseudo-locale");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Binding: the sidebar still works under the pseudo-locale. A tab's label does not wrap and
    /// its highlight is a fixed-height box stretched across the column, so a longer label must
    /// move no tab: each tab's box — the frame its dwell target is centred in — is identical in
    /// English and in the pseudo-locale, and so is the number of tabs a sidebar page holds. (That
    /// a pseudo label overhangs its tab is a FIT finding, reported by the non-binding fit report;
    /// T10 shortens German labels that do.)
    /// </summary>
    [StaFact]
    public void TheSidebar_UnderThePseudoLocale_KeepsEveryTabWhereItWas()
    {
        var moved = new List<string>();
        foreach (var tab in SettingsTabDriftTests.RenderedTabDefs())
        {
            var english = SidebarTabFit.Measure(tab.Icon, tab.Label);
            var pseudoLabel = PseudoLocale.Table.T(tab.LabelKey);
            var pseudo = SidebarTabFit.Measure(tab.Icon, pseudoLabel);
            _output.WriteLine($"{tab.LabelKey}: \"{pseudoLabel}\" box {pseudo.BorderBox} (English {english.BorderBox}), "
                              + $"label right {FitText.Dip(pseudo.LabelRight)} (English {FitText.Dip(english.LabelRight)})");
            if (pseudo.BorderBox != english.BorderBox)
                moved.Add($"{tab.LabelKey}: {english.BorderBox} → {pseudo.BorderBox}");
            Check.That(pseudoLabel).StartsWith("[");
        }

        Check.WithCustomMessage("A longer label moved a sidebar tab:\n  " + string.Join("\n  ", moved)).That(moved).IsEmpty();
    }

    /// <summary>
    /// Binding: the content pager still works under the pseudo-locale. Its row — the two pager
    /// buttons and, between them, the real "page / pages" indicator (<see cref="SettingsWindow.BuildPageIndicator"/>)
    /// at a two-digit page count — fits the content column, and every pager button's accessible
    /// name resolves to pseudo-locale text rather than a raw key.
    /// </summary>
    [StaFact]
    public void ThePager_UnderThePseudoLocale_FitsItsRow_AndNamesItsButtons()
    {
        StringTable.Activate(PseudoLocale.Table);
        try
        {
            var indicator = SettingsWindow.BuildPageIndicator(99, 99);
            Check.That(indicator.Text).IsEqualTo("[99 / 99]");
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(indicator);
            using var host = FitHost.Open(panel, family: Tokens.FontStandard);
            var indicatorWidth = indicator.ActualWidth + indicator.Margin.Left + indicator.Margin.Right;
            var buttons = 2 * (SettingsWindow.PaginationButtonWidth + 2 * SettingsWindow.PaginationButtonMargin);
            var column = HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());
            _output.WriteLine($"pager row {FitText.Dip(buttons + indicatorWidth)} DIP in a {FitText.Dip(column)} column "
                              + $"[{FitText.ResolvedFace(indicator)}]");
            Check.That(buttons + indicatorWidth).IsLessOrEqualThan(column);

            foreach (var id in new[] { "pag_prev", "pag_next", "tab_prev", "tab_next" })
            {
                foreach (var boundary in new[] { false, true })
                {
                    var name = StringTable.Current.T(SettingsPagerChrome.AccessibleNameKey(id, boundary));
                    Check.WithCustomMessage($"{id} (boundary {boundary}) is named \"{name}\".").That(name).StartsWith("[");
                }
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }
}
