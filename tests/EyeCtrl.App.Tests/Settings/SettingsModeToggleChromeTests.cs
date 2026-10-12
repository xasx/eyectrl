namespace EyeCtrl.App.Tests.Settings;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using EyeCtrl.App.Tests;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;

/// <summary>
/// The settings mode toggle's CHROME gates: the label must stay legible on its own face in
/// EVERY theme — at rest and under the full dwell wash — and its
/// <see cref="SettingsWindow.ModeToggleHitRadius"/> hit circle must keep real clearance from
/// the two dwell targets around it: the content pager's pag_next and the first content row's
/// control.
///
/// <para>
/// The CONTRAST half reads the BUILT face (<see cref="SettingsWindow.BuildModeToggle"/> —
/// background brush colour plus the label's Foreground/FontSize/FontWeight from its output),
/// not theme tokens, and iterates every theme through the
/// <see cref="EyeCtrl.App.Tests.Localization.Fit.FitClassThemeTests"/> discipline:
/// <see cref="ThemeScope.Activate"/> (never <c>.Enter</c> — the scope must publish the theme's
/// resources, not just flip <c>Tokens.Current</c>) inside the exclusive "ActiveTheme"
/// collection. The wash is production's own: the toggle's fill is repainted by
/// <c>BlendProgress(..., 0.4)</c>, i.e. <see cref="ColorBlend.Blend"/>(base,
/// <see cref="Tokens.Accent"/>, progress * 0.4) — 0.4 at full dwell, blended here with the
/// same production helper rather than a test-side interpolation.
/// </para>
/// <para>
/// Two floors, and the size/weight assertions are what make the second one legal: at rest the
/// label owes the full WCAG AA 4.5:1, but under the 0.4 wash the pairing lands near 3.39:1
/// (Midnight), which clears only the LARGE-text 3:1 allowance — and that allowance is owed by
/// text at 18pt = 24 DIP. <see cref="Tokens.FontSizeTitle"/> is 24.0 in every theme and
/// <see cref="Tokens.FontWeightStrong"/> is Bold, both pinned below rather than assumed: a
/// theme that dropped the title size under 24 would silently strand the washed label below
/// its own floor. 18.0 is NOT the bar (plan-review r2 m-a).
/// </para>
/// <para>
/// The GEOMETRY half derives every centre from the live XAML through
/// <see cref="SettingsContentFitTests"/>' own readers plus production's consts — the
/// <see cref="SettingsPagerClearanceTests"/> discipline: no restated arithmetic, no invented
/// pager position. The toggle rides the Pagination detector alongside pag_next, and above
/// content controls that ride Control — different detectors accrue CONCURRENTLY, so
/// nearest-wins does not mask an overlap.
/// </para>
/// <para>
/// Nothing is shown and no input is simulated. No UI category, so CI runs both halves.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class SettingsModeToggleChromeTests
{
    // Copied from SettingsPageHeaderContrastTests.cs:74-87 (WCAG relative luminance):
    private static double Luminance(System.Windows.Media.Color c)
    {
        double F(double ch) { var v = ch / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [StaFact]
    public void LabelContrast_AtRestAndFullWash_ClearsTheBars_InEveryTheme()
    {
        foreach (var theme in ThemeRegistry.All)                       // mirror FitClassThemeTests'
        {                                                              // enumeration exactly
            using (ThemeScope.Activate(theme))
            {
                var buttons = new Dictionary<string, System.Windows.Controls.Border>();
                var brushes = new Dictionary<string, System.Windows.Media.SolidColorBrush>();
                var border = SettingsWindow.BuildModeToggle("All settings", () => { }, buttons, brushes);
                var label = (System.Windows.Controls.TextBlock)border.Child;

                // The pair the user sees, read from the face:
                var baseC = ((System.Windows.Media.SolidColorBrush)border.Background).Color;
                var labelC = ((System.Windows.Media.SolidColorBrush)label.Foreground).Color;

                // Size/weight are load-bearing: the 0.4 wash lands near 3.39:1, legal only at
                // or above the LARGE-text 3:1 bar. The floor is the precedent's
                // LargeTextThresholdDip = 24 (18.0 is NOT the WCAG large-text bar — plan-review r2 m-a).
                Check.WithCustomMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        "In the {0} theme the mode toggle's label renders at {1:F2} DIP, under the "
                        + "24 DIP WCAG large-text threshold. The washed pairing below is only held "
                        + "to the 3:1 large-text floor, which that size would no longer owe. The "
                        + "label's brush and size are pinned (L5/L18) — fix the pairing, not the floor.",
                        theme.Name, label.FontSize))
                    .That(label.FontSize).IsGreaterOrEqualThan(24.0);
                Check.WithCustomMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        "In the {0} theme the mode toggle's label is no longer Bold. The washed "
                        + "pairing below is held to the 3:1 large-text floor, which bold weight is "
                        + "half of entitlement to. The label's brush and size are pinned (L5/L18) — "
                        + "fix the pairing, not the floor.",
                        theme.Name))
                    .That(label.FontWeight).IsEqualTo(FontWeights.Bold);

                Check.WithCustomMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        "In the {0} theme the mode toggle's label reads {1:F2}:1 against its face at "
                        + "rest, below the {2:F1} WCAG AA floor for normal text. The label brush is "
                        + "pinned (L5/L18); a measured miss here is a FINDING — fix the pairing, "
                        + "never the threshold.",
                        theme.Name, Contrast(labelC, baseC), 4.5))
                    .That(Contrast(labelC, baseC)).IsGreaterOrEqualThan(4.5);          // at rest

                var washed = ColorBlend.Blend(baseC, Tokens.Accent, 0.4);
                Check.WithCustomMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        "In the {0} theme the mode toggle's label reads {1:F2}:1 against its "
                        + "fully-washed face (base blended 0.4 toward the accent, production's own "
                        + "ColorBlend), below the {2:F1} WCAG AA floor it owes as 24-DIP Bold large "
                        + "text. The label brush is pinned (L5/L18); a measured miss here is a "
                        + "FINDING — fix the pairing, never the threshold.",
                        theme.Name, Contrast(labelC, washed), 3.0))
                    .That(Contrast(labelC, washed)).IsGreaterOrEqualThan(3.0);         // full dwell
            }
        }
    }

    // StaFact, not Fact: the derivation reads the page indicator off the element
    // SettingsWindow.BuildPageIndicator builds, and constructing a TextBlock needs STA. Still
    // headless — nothing is shown, no UI category.
    [StaFact]
    public void ToggleClears_ThePagerAndTheFirstContentRow_AtTheNarrowestHeader()
    {
        var root = SettingsContentFitTests.Xml();
        AssertLayoutAnchors(root);

        // The narrowest supported display's window — the same 800-DIP narrow-display guard
        // SettingsContentFitTests' own probes use — and the content area it leaves after the
        // sidebar and the area's margins:
        var windowWidth = SettingsContentFitTests.WindowWidthFor(
            SettingsContentFitTests.NarrowDisplayWidthDip);
        var contentArea = SettingsContentFitTests.Margin(root, "ContentArea");
        var contentWidth = windowWidth
            - SettingsContentFitTests.SidebarWidth(root)
            - contentArea.Left - contentArea.Right;

        var toggle = ToggleCentre(root, contentWidth);

        // NOTE (spec L17d / round-3 m9), recorded here as the accepted-debt note, not a check:
        // at 448 DIP the 50-DIP hit circle's lower band overlaps the description's first line —
        // reading that line's end can accrue toggle dwell; does not occur on the owner's display.
        var pagNext = PagNextCentre(root, contentWidth);
        var firstRow = FirstContentRowCentre(root, toggle.X);

        AssertClearance(toggle, pagNext, "mode toggle", "content pager's pag_next",
            SettingsWindow.ModeToggleHitRadius, SettingsWindow.PagBtnRadius);
        AssertClearance(toggle, firstRow, "mode toggle", "first content row's control",
            SettingsWindow.ModeToggleHitRadius, SettingsWindow.ControlBtnRadius);
    }

    // ────── Derivation helpers — every number comes from the live XAML or a production const ──────

    /// <summary>
    /// Asserts the layout the derivations below stand on: the header owns content row 0 with the
    /// toggle's Auto column on its right edge, the pagination bar owns the row between the
    /// header and the card, and the bar is a horizontally-centred stack. If the XAML moves, this
    /// fails BEFORE the clearance numbers silently stop meaning anything.
    /// </summary>
    private static void AssertLayoutAnchors(XElement root)
    {
        Check.WithCustomMessage(
                "HeaderPanel no longer declares ColumnDefinitions=\"*,Auto\". The toggle's Auto "
                + "column is what anchors its host to the header's right edge; a third column or a "
                + "Star change moves the toggle off the edge this derivation measures from.")
            .That((string?)SettingsContentFitTests.Named(root, "HeaderPanel").Attribute("ColumnDefinitions"))
            .IsEqualTo("*,Auto");
        Check.That((string?)SettingsContentFitTests.Named(root, "HeaderPanel").Attribute("Grid.Row"))
            .IsEqualTo("0");

        var host = SettingsContentFitTests.Named(root, "ModeToggleHost");
        Check.WithCustomMessage(
                "ModeToggleHost has left the header's title line. The toggle rides row 0 by "
                + "design (the measured 103.9/112 header assumes it); repoint the derivation, do "
                + "not delete this.")
            .That((string?)host.Attribute("Grid.Row")).IsEqualTo("0");
        Check.That((string?)host.Attribute("Grid.Column")).IsEqualTo("1");
        Check.WithCustomMessage(
                "ModeToggleHost is no longer top-aligned; the toggle's centre-y derivation reads "
                + "its top margin and assumes the block sits at the header's top.")
            .That((string?)host.Attribute("VerticalAlignment")).IsEqualTo("Top");

        var pager = SettingsContentFitTests.Named(root, "PaginationPanel");
        Check.WithCustomMessage(
                "PaginationPanel no longer owns the row between the header and the card; the "
                + "stacking the y-derivations below walk is header, pager, card.")
            .That((string?)pager.Attribute("Grid.Row")).IsEqualTo("1");
        Check.WithCustomMessage(
                "PaginationPanel is no longer a horizontally-centred bar; the pag_next x-derivation "
                + "assumes that shape. Re-derive it for the new layout rather than loosening this.")
            .That((string?)pager.Attribute("Orientation")).IsEqualTo("Horizontal");
        Check.That((string?)pager.Attribute("HorizontalAlignment")).IsEqualTo("Center");

        var card = SettingsContentFitTests.Named(root, "ContentCard");
        Check.WithCustomMessage("ContentCard has no parent grid.").That(card.Parent).IsNotNull();
        Check.WithCustomMessage(
                "ContentCard's parent no longer owns content row 2; the card-top derivation stacks "
                + "header, pager row, then card.")
            .That((string?)card.Parent!.Attribute("Grid.Row")).IsEqualTo("2");
    }

    /// <summary>
    /// The toggle's centre, in content-area coordinates: header row 0 with the host's top margin
    /// above it, x inset from the header's right edge by the host's left margin (the 12-DIP
    /// title-to-toggle gap the fit arithmetic uses, plan-review I10) plus half the toggle's width.
    /// The header spans the content area with no margin of its own, so the header's width IS the
    /// content area's width.
    /// </summary>
    private static (double X, double Y) ToggleCentre(XElement root, double contentWidth)
    {
        var host = SettingsContentFitTests.Margin(root, "ModeToggleHost");
        return (
            contentWidth - host.Left - SettingsWindow.ModeToggleWidth / 2,
            host.Top + SettingsWindow.ModeToggleHeight / 2);
    }

    /// <summary>
    /// pag_next's centre — the pager button closest to the toggle, being the bar's right-hand
    /// block. The bar is centred on the content area; its width is two button blocks flanking
    /// the page indicator, read from production's consts and off the element
    /// <see cref="SettingsWindow.BuildPageIndicator"/> actually builds (its MinWidth and own side
    /// margins), never restated.
    ///
    /// <para>
    /// The indicator is taken at its layout FLOOR (MinWidth + margins) deliberately: that is the
    /// narrowest bar the layout can produce, which puts pag_next closest to the toggle's column.
    /// A longer localized "page / pages" string only widens the bar and walks pag_next further
    /// right along the centred bar, away from directly-under-the-toggle — with the toggle at
    /// contentWidth - 116 and the floor bar already placing pag_next right of that column, the
    /// distance is monotone in the bar's width over the whole legal range, so the floor is the
    /// worst case.
    /// </para>
    /// </summary>
    private static (double X, double Y) PagNextCentre(XElement root, double contentWidth)
    {
        var header = SettingsContentFitTests.Margin(root, "HeaderPanel");
        var panel = SettingsContentFitTests.Margin(root, "PaginationPanel");

        // The uniform margin MakePagButton builds its buttons with — the one margin story.
        var buttonMargin = SettingsWindow.PaginationButtonMargin;
        var buttonBlock = SettingsWindow.PaginationButtonWidth + 2 * buttonMargin;

        var indicator = SettingsWindow.BuildPageIndicator(1, 1);
        var indicatorBlock = indicator.MinWidth + indicator.Margin.Left + indicator.Margin.Right;

        var barWidth = 2 * buttonBlock + indicatorBlock;
        return (
            contentWidth / 2 + barWidth / 2 - buttonMargin - SettingsWindow.PaginationButtonWidth / 2,
            SettingsContentFitTests.FixedHeight(root, "HeaderPanel") + header.Bottom
                + panel.Top + buttonMargin + SettingsWindow.PaginationButtonHeight / 2);
    }

    /// <summary>
    /// The first content row's hit-circle centre. The clearance test's machinery cannot be
    /// reused for the content-row centre (it walks the SIDEBAR's rows), so this derives from the
    /// same XAML constants it reads — the header's fixed height and bottom margin, the pager
    /// row's laid-out height (the panel's own margins plus one button block, its tallest child;
    /// production pins PagerRow.MinHeight to PaginationReserveHeight, this same number), and the
    /// card's top padding — plus the pitch production guarantees every row kind: a first row's
    /// hit circle sits <see cref="SettingsWindow.LabelledRowMinHeight"/> / 2 below the row top
    /// (two-column rows centre their control in the box; stacked rows sit deeper; ButtonRow
    /// centres in the same box).
    ///
    /// <para>
    /// Which COLUMN the first row's control lands in varies by page and row kind (two-column
    /// rows right-align the control, stacked rows left-align it), so the pin takes the closest
    /// possible column — directly under the toggle — and lets the vertical terms carry the
    /// clearance: any real column is at least this far from the toggle.
    /// </para>
    /// </summary>
    private static (double X, double Y) FirstContentRowCentre(XElement root, double toggleX)
    {
        var header = SettingsContentFitTests.Margin(root, "HeaderPanel");
        var panel = SettingsContentFitTests.Margin(root, "PaginationPanel");
        var cardPadding = SettingsContentFitTests.Padding(root, "ContentCard");

        var pagerRowHeight = panel.Top + panel.Bottom
            + SettingsWindow.PaginationButtonHeight + 2 * SettingsWindow.PaginationButtonMargin;

        return (
            toggleX,
            SettingsContentFitTests.FixedHeight(root, "HeaderPanel") + header.Bottom
                + pagerRowHeight
                + cardPadding.Top
                + SettingsWindow.LabelledRowMinHeight / 2);
    }

    /// <summary>
    /// The clearance two hit circles need, in the <see cref="SettingsPagerClearanceTests"/>
    /// shape: Euclidean centre distance against the sum of the radii, with the failure message
    /// naming both sides and the fix direction.
    /// </summary>
    private static void AssertClearance(
        (double X, double Y) from, (double X, double Y) to,
        string fromName, string toName, double fromRadius, double toRadius)
    {
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var radii = fromRadius + toRadius;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {0}'s hit circle centre is {1:F2} DIP from the {2}'s, but the two circles "
                + "need {3:F2} ({4:F0} + {5:F0}); the overlap is {6:F2} DIP. The toggle rides the "
                + "Pagination detector while content controls ride Control — different detectors "
                + "accrue CONCURRENTLY, so nearest-wins does not mask the overlap. Widen the "
                + "header's bottom margin, shrink the toggle, or move the target — do NOT widen "
                + "this assertion.",
                fromName, distance, toName, radii, fromRadius, toRadius, radii - distance))
            .That(distance).IsGreaterOrEqualThan(radii);
    }
}
