namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The settings sidebar is a FIXED-width column holding variable-width tab labels, and nothing
/// guarded the two against each other. Raising the label from a literal 22 to
/// <see cref="Tokens.FontSizeTitle"/> (24) and the icon from a literal 30 to
/// <c>TabContainerHeight * ContainerRatio.Medium</c> (32) pushed the widest label, "Quick
/// Actions", from a right edge of 218.61 DIP to 233.86 — from 1.39px inside the 220 DIP column to
/// 13.86px outside it, bleeding onto the content column. The column is now 240.
///
/// The type ramp, the ratio buckets and the column width are all things this codebase changes
/// deliberately and often, so this test hard-codes **none** of them:
/// <list type="bullet">
/// <item>the column width is parsed out of SettingsWindow.xaml, the same way
/// <see cref="TrackStatusFitTests"/> reads the window height it reasons about;</item>
/// <item>the label size is <see cref="Tokens.FontSizeTitle"/> and the weight
/// <see cref="Tokens.FontWeightMedium"/>, read live;</item>
/// <item>the icon size is <c>SettingsWindow.TabContainerHeight * ContainerRatio.Medium</c> — the
/// same expression the production code writes, from the same const;</item>
/// <item>the font family is <see cref="Tokens.FontStandard"/>, the very
/// <see cref="FontFamily"/> object <c>OverlayWindow</c> assigns — glyph widths are what is being
/// measured, so a family swap must move this test;</item>
/// <item>the icons and labels come from the <c>_tabDefs</c> initializer in
/// SettingsWindow.xaml.cs through <see cref="SettingsTabDriftTests.RenderedTabDefs"/>: since T07
/// the initializer carries literal <c>settings</c> keys and the English text is resolved from
/// strings.json, so a renamed or added tab is measured without touching this file.</item>
/// </list>
///
/// <see cref="SettingsWindow"/> cannot be constructed here — it takes eight non-optional
/// dependencies — so the tab's element tree is rebuilt element for element by
/// <see cref="SidebarTabFit"/> and laid out at the user's 2.0 in T09's off-screen fit host.
/// **Nothing is shown and no input is simulated**: the host is an invisible source, which matters
/// because this suite must never take over the screen of a user whose only input channel is an eye
/// tracker. It carries no UI category, so CI runs it.
/// </summary>
public class SettingsTabLabelFitTests
{
    /// <summary>The sidebar column's width; see <see cref="SidebarTabFit.SidebarColumnWidth"/>.</summary>
    private static double SidebarColumnWidth(string xaml) => SidebarTabFit.SidebarColumnWidth(xaml);

    /// <summary>
    /// The icon glyph and label of every sidebar tab, in declaration order.
    ///
    /// Read through <see cref="SettingsTabDriftTests.RenderedTabDefs"/>, which parses the
    /// production <c>_tabDefs</c> initializer (the field is built inside the eight-dependency
    /// constructor and cannot be referenced) and resolves each literal <c>settings</c> key from
    /// the embedded English table. That keeps the strings in ONE place — strings.json — so a
    /// renamed tab or a twelfth one is measured here automatically, which a copied list would not
    /// be.
    /// </summary>
    /// <returns>One (icon, label) pair per tab.</returns>
    private static List<(string Icon, string Label)> TabChrome()
    {
        var chrome = SettingsTabDriftTests.RenderedTabDefs()
            .Select(tab => (tab.Icon, tab.Label))
            .ToList();

        // A table that resolves nothing is a test that guards nothing — the failure mode this
        // whole file exists to close. Eleven tabs ship today; the floor is deliberately loose so
        // adding one does not fail here, and tight enough that a shape change the reader stops
        // understanding does.
        Check.WithCustomMessage(
                $"Read {chrome.Count} tab definitions out of `_tabDefs`, which cannot be right. "
                + "The initializer's shape changed and the reader no longer understands it, so "
                + "this guard would measure nothing at all.")
            .That(chrome.Count).IsStrictlyGreaterThan(5);

        return chrome;
    }

    /// <summary>
    /// One tab's label and border right edges, laid out at the user's 2.0 by
    /// <see cref="SidebarTabFit.Measure"/> (T09 moved the rebuild there, onto the fit host, so
    /// the <c>tab156</c> class and this file measure the same tree).
    /// </summary>
    private static (double LabelRight, double BorderRight) MeasureTab(string icon, string label)
    {
        var result = SidebarTabFit.Measure(icon, label);
        return (result.LabelRight, result.BorderRight);
    }

    [StaFact]
    public void EveryTabLabel_FitsTheSidebarColumn()
    {
        var columnWidth = SidebarTabFit.ColumnWidth();
        var chrome = TabChrome();

        // The production expression, not its result. TabContainerHeight is internal for this.
        var iconSize = SettingsWindow.TabContainerHeight * ContainerRatio.Medium;

        var overflowing = new List<string>();
        var widest = ("", 0.0);

        foreach (var (icon, label) in chrome)
        {
            var (labelRight, _) = MeasureTab(icon, label);
            if (labelRight > widest.Item2) widest = (label, labelRight);
            if (labelRight > columnWidth)
                overflowing.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "\"{0}\" right edge {1:F2} vs column {2:F2} (over by {3:F2}px)",
                    label, labelRight, columnWidth, labelRight - columnWidth));
        }

        // Invariant formatting: the German default prints "233,86", which cannot be grepped for.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "{0} sidebar tab label(s) overrun the {1:F2} DIP sidebar column declared by "
                + "SettingsWindow.xaml, so they bleed onto the settings content column:\n  {2}\n"
                + "Measured with the live ramp (label {3}, icon {4}) and the live family "
                + "\"{5}\". This is the regression the 220 -> 240 widening fixed: at 220 "
                + "\"Quick Actions\" measured 233.86. Widen the column to clear the widest label, "
                + "or shorten the label — do NOT constrain it with TextTrimming, which ellipsizes "
                + "three of these tabs (see the comment in SettingsWindow.BuildTabButtons).",
                overflowing.Count, columnWidth, string.Join("\n  ", overflowing),
                Tokens.FontSizeTitle, iconSize, Tokens.FontStandard.Source))
            .That(overflowing).IsEmpty();

        // The measurement must actually have happened. A harness that silently measures zero-width
        // labels — a font that fails to load, an Arrange that never ran — would report every tab
        // as fitting.
        Check.WithCustomMessage(
                $"The widest tab label measured only {widest.Item2:F2} DIP, which no real label "
                + "does. The measurement harness is not laying anything out, so the assertion "
                + "above proves nothing.")
            .That(widest.Item2).IsStrictlyGreaterThan(columnWidth / 2);
    }

    [StaFact]
    public void TheWidestTabLabel_StaysInsideItsOwnTabHighlight_OrIsRecordedAsNotDoingSo()
    {
        // The column is the hard constraint — past it the label lands on the content column. The
        // tab Border is the SOFT one: past it the label merely leaves its own rounded highlight
        // and sits on the sidebar fill, still fully legible. That overhang is ACCEPTED for
        // "Quick Actions" at 240 (5.86px) rather than fixed, and this test records the accepted
        // amount so that it cannot quietly grow into something conspicuous.
        var columnWidth = SidebarTabFit.ColumnWidth();
        var chrome = TabChrome();

        var worst = 0.0;
        var worstLabel = "";
        foreach (var (icon, label) in chrome)
        {
            var (labelRight, borderRight) = MeasureTab(icon, label);
            if (labelRight - borderRight > worst)
            {
                worst = labelRight - borderRight;
                worstLabel = label;
            }
        }

        // 8px: the overhang accepted today is 5.86px, and the tab Border's own Margin is 4 per
        // side — so 8 is "still within the gutter between two tabs' highlights". Anything past
        // that reads as text escaping its button rather than crowding it.
        const double tolerated = SidebarTabFit.ToleratedOverhang;
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "\"{0}\" now overhangs its tab Border by {1:F2}px, past the {2:F2}px this layout "
                + "accepts. It still fits the {3:F2} DIP column — the label is not bleeding onto "
                + "the content area — but it has escaped its highlight far enough to look broken. "
                + "Widen the sidebar column further, or shorten the label.",
                worstLabel, worst, tolerated, columnWidth))
            .That(worst).IsStrictlyLessThan(tolerated);
    }

    [StaFact]
    public void TheColumnReading_TakesTheFirstColumnOfTheRootGrid()
    {
        // A wrong reading here does not fail loudly, it fails GREEN: reading the Star content
        // column as the sidebar hands the assertion a width of hundreds of pixels that every
        // label clears. So the extraction is probed directly.
        var probe =
            """
            <overlays:OverlayWindow xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:overlays="urn:o" Height="900">
              <Grid ColumnDefinitions="240, *"><TextBlock/></Grid>
            </overlays:OverlayWindow>
            """;
        Check.WithCustomMessage("SidebarColumnWidth did not read the first column's width.")
            .That(SidebarColumnWidth(probe)).IsEqualTo(240d);
    }

    [StaFact]
    public void TheTabChromeReading_FindsEveryTabAndItsRealLabel()
    {
        // Pins the reader against the real file: if it degraded to matching a subset, the fit
        // assertion would still pass while no longer covering the tab that overflows.
        var chrome = TabChrome();

        // Not a copy of the list — a spot check that the parse produced real labels rather than
        // ids or builder names. "Quick Actions" is the widest and the one this whole file is
        // about; if it is ever renamed, this line is meant to fail and be updated.
        Check.WithCustomMessage(
                "The `_tabDefs` parse did not yield \"Quick Actions\", the widest label and the "
                + $"reason this guard exists. Parsed: {string.Join(" | ", chrome.Select(c => c.Label))}.")
            .That(chrome.Select(c => c.Label)).Contains("Quick Actions");

        Check.WithCustomMessage("A parsed tab has an empty icon or label.")
            .That(chrome.All(c => c.Icon.Length > 0 && c.Label.Length > 0)).IsTrue();
    }
}
