namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The settings page header names the active tab and says in one line what it covers. It is the
/// only place a tab explains itself — the sidebar glyphs alone are ambiguous — and the user's only
/// input channel is an eye tracker, so "lean in and squint" is not a workaround available to them.
///
/// <para>
/// Its backdrop is NOT the content card. The header sits in <c>ContentArea</c>'s row 0, above
/// <c>ContentCard</c>, so both lines paint on the window's own
/// <c>Background="{StaticResource BrushSurfaceDark}"</c> — a different pairing from the one
/// <see cref="SettingsRowDescriptionContrastTests"/> gates, which is why this is a second file
/// rather than another theory in that one.
/// </para>
/// <para>
/// Two different floors, because the two lines are different sizes:
/// <list type="bullet">
/// <item><c>HeaderTitle</c> renders at <see cref="ITheme.FontSizeDisplay"/> (34 DIP) in
/// <c>FontWeightStrong</c>. That IS WCAG "large text" (18pt = 24 DIP), so it owes 3:1.</item>
/// <item><c>HeaderDescription</c> renders at <see cref="ITheme.FontSizeSubtitle"/> (18 DIP) in
/// normal weight, which is NOT large text (that needs 24 DIP, or 18.67 DIP bold), so it owes the
/// full 4.5:1.</item>
/// </list>
/// The sizes are asserted rather than assumed, below: the floors are only the right floors while
/// the type ramp still puts the two lines either side of the large-text threshold.
/// </para>
/// <para>
/// Measured across every registered theme, against <c>SurfaceDark</c>:
/// <c>TextPrimary</c> is 12.69 (Midnight) / 14.75 (Ember) / 21.00 (Contrast), and
/// <c>TextSecondary</c> is 6.92 / 8.34 / 17.14. Both clear their floor everywhere, so the header
/// keeps the tokens the rest of the settings type ramp uses. For reference <c>TextDim</c> is
/// 5.37 / 5.55 / 12.55 — it would also clear 4.5:1 on this darker backdrop, unlike on the card
/// (4.37 in Midnight, which is why a row's description is TextSecondary). The header stays on
/// TextSecondary anyway so the two descriptions read as the same kind of text.
/// </para>
/// <para>
/// Runs per THEME rather than against whatever <c>Tokens.Current</c> happens to be — same reason
/// as <see cref="SettingsRowDescriptionContrastTests"/> and
/// <see cref="EyeCtrl.App.Tests.ModalButtonPaletteTests"/>: the picker varies both sides of the
/// pairing, and a gate reading only the active theme would let a new or edited theme ship an
/// unreadable header. It lives in EyeCtrl.App.Tests, not EyeCtrl.Controls.Tests, because
/// <c>Tokens.Activate</c> is banned there — and nothing here activates anything, it reads the
/// theme objects directly.
/// </para>
/// <para>
/// Alpha is ignored on purpose. Midnight's and Ember's <c>SurfaceDark</c> carry 0xFA, and what
/// shows through the remaining 2% is the desktop, which no test can know. Comparing the declared
/// RGB is the same convention <see cref="SettingsContentCardBoundaryTests"/> uses against this
/// very token, and it is the conservative one: the pairing is judged on the colour the theme
/// actually declares.
/// </para>
/// </summary>
public class SettingsPageHeaderContrastTests
{
    /// <summary>The WCAG AA floor for normal-weight body text.</summary>
    private const double NormalTextFloor = 4.5;

    /// <summary>The WCAG AA floor for large text (18pt = 24 DIP, or 14pt bold).</summary>
    private const double LargeTextFloor = 3.0;

    /// <summary>The size at which WCAG stops calling normal-weight text "small", in DIP.</summary>
    private const double LargeTextThresholdDip = 24.0;

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Every registered theme.</summary>
    /// <returns>One row per theme in <c>ThemeRegistry.All</c>.</returns>
    public static TheoryData<ITheme> EveryTheme()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All) data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void TheHeaderTitle_ClearsTheLargeTextFloor_InEveryTheme(ITheme theme)
    {
        var ratio = Contrast(theme.SurfaceDark, theme.TextPrimary);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme the settings page header's TITLE reads {1:F2}:1 against the "
                + "page (SurfaceDark), below the {2:F1} WCAG AA floor. It renders at "
                + "FontSizeDisplay in FontWeightStrong, so it qualifies for the large-text "
                + "allowance and owes 3.0 rather than 4.5 — failing this means the title is not "
                + "legible at all. Brighten the theme's TextPrimary; there is no dimmer token to "
                + "move to.",
                theme.Name, ratio, LargeTextFloor))
            .That(ratio).IsGreaterOrEqualThan(LargeTextFloor);
    }

    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void TheHeaderDescription_ClearsTheNormalTextFloor_InEveryTheme(ITheme theme)
    {
        var ratio = Contrast(theme.SurfaceDark, theme.TextSecondary);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme the settings page header's DESCRIPTION reads {1:F2}:1 against "
                + "the page (SurfaceDark), below the {2:F1} WCAG AA floor for normal-weight text. "
                + "It renders at FontSizeSubtitle in normal weight, so it does NOT qualify for "
                + "the 3.0 large-text allowance. Brighten the theme's TextSecondary, or move the "
                + "description to TextPrimary.",
                theme.Name, ratio, NormalTextFloor))
            .That(ratio).IsGreaterOrEqualThan(NormalTextFloor);
    }

    /// <summary>
    /// The two floors above are only the right floors while the type ramp keeps the title above
    /// the large-text threshold and the description below it. Shrinking
    /// <c>FontSizeDisplay</c> to 22, or growing <c>FontSizeSubtitle</c> to 24, would silently
    /// swap which allowance each line is entitled to — so the classification is asserted rather
    /// than left in a comment.
    /// </summary>
    /// <param name="theme">The theme under test.</param>
    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void TheTypeRamp_StillPutsTheTwoLinesEitherSideOfTheLargeTextThreshold(ITheme theme)
    {
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "{0}'s FontSizeDisplay is {1:F2} DIP, below the {2:F0} DIP WCAG large-text "
                + "threshold. The header title would then owe the full 4.5:1, not the 3.0 this "
                + "file holds it to.",
                theme.Name, theme.FontSizeDisplay, LargeTextThresholdDip))
            .That(theme.FontSizeDisplay).IsGreaterOrEqualThan(LargeTextThresholdDip);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "{0}'s FontSizeSubtitle is {1:F2} DIP, at or above the {2:F0} DIP WCAG "
                + "large-text threshold. The header description would then qualify for the 3.0 "
                + "allowance, and the 4.5 floor this file holds it to is stricter than WCAG "
                + "requires — revisit deliberately rather than leaving the comment stale.",
                theme.Name, theme.FontSizeSubtitle, LargeTextThresholdDip))
            .That(theme.FontSizeSubtitle).IsStrictlyLessThan(LargeTextThresholdDip);
    }

    /// <summary>The XAML language namespace, which carries <c>x:Name</c>.</summary>
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Finds the single element in SettingsWindow.xaml carrying the given x:Name.</summary>
    /// <param name="name">The x:Name to look for.</param>
    /// <returns>The named element.</returns>
    private static XElement Named(string name)
    {
        var path = Path.Combine(EyeCtrl.App.Tests.SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml");
        var root = XDocument.Parse(File.ReadAllText(path)).Root;
        Check.WithCustomMessage("SettingsWindow.xaml has no root element.").That(root).IsNotNull();

        var hit = root!.DescendantsAndSelf()
            .SingleOrDefault(e => (string?)e.Attribute(Xaml + "Name") == name);
        Check.WithCustomMessage(
                $"SettingsWindow.xaml no longer has an element named \"{name}\". The page header's "
                + "contrast gate is pinned to that element's Foreground; repoint both rather than "
                + "deleting this.")
            .That(hit).IsNotNull();
        return hit!;
    }

    /// <summary>
    /// The LINK between the XAML and the per-theme gates above, and it is load-bearing exactly as
    /// <c>SettingsContentCardBoundaryTests.TheOutline_StillNamesABrushAndAThickness</c> is: the
    /// gates measure <c>TextPrimary</c> and <c>TextSecondary</c> against <c>SurfaceDark</c>, so
    /// repointing either TextBlock at a different token would leave them measuring a pairing the
    /// page does not use — green over an illegible header. The font-size keys are pinned for the
    /// same reason: which FLOOR each line owes is decided by its size, and a title dropped to
    /// FontSizeSubtitle would owe 4.5 while still being checked against 3.0.
    /// </summary>
    [Fact]
    public void TheHeaderTextBlocks_StillNameTheTokensTheseGatesMeasure()
    {
        var expectations = new[]
        {
            ("HeaderTitle", "Foreground", "BrushTextPrimary"),
            ("HeaderTitle", "FontSize", "FontSizeDisplay"),
            ("HeaderTitle", "FontWeight", "FontWeightStrong"),
            ("HeaderDescription", "Foreground", "BrushTextSecondary"),
            ("HeaderDescription", "FontSize", "FontSizeSubtitle"),
        };

        foreach (var (element, property, key) in expectations)
        {
            var attr = Named(element).Attribute(property);
            Check.WithCustomMessage(
                    $"{element} no longer declares a {property}. Every value this gate reasons "
                    + "about comes from the theme dictionary; a missing one inherits from "
                    + "somewhere else and the measured pairing stops being the painted one.")
                .That(attr).IsNotNull();

            Check.WithCustomMessage(
                    $"{element}'s {property} is \"{attr!.Value}\", not {key}. The per-theme gates "
                    + "in this file measure TextPrimary and TextSecondary against SurfaceDark at "
                    + "FontSizeDisplay and FontSizeSubtitle — repointing this attribute leaves "
                    + "them measuring a pairing the page does not paint, so they would stay green "
                    + "over an unreadable header. Repoint the gate too if this is deliberate.")
                .That(attr.Value).Contains(key);
        }

        Check.WithCustomMessage(
                "HeaderDescription no longer wraps. The header's Height is fixed, so an unwrapped "
                + "description is silently truncated at the content column's edge instead of "
                + "taking the second line SettingsContentFitTests budgets for it.")
            .That((string?)Named("HeaderDescription").Attribute("TextWrapping"))
            .IsEqualTo("Wrap");
    }

    /// <summary>
    /// The header paints on the PAGE, not on the card, and the two backdrops are genuinely
    /// different colours — so the gate above is measuring a pairing the row-description gate does
    /// not already cover. Asserted as a fact about the palette: if a theme ever flattened
    /// SurfaceDark onto SurfaceMedium, this file and
    /// <see cref="SettingsRowDescriptionContrastTests"/> would be checking the same thing twice
    /// and one of them could go.
    /// </summary>
    [Fact]
    public void ThePageAndTheCard_AreStillDifferentBackdrops()
    {
        var same = ThemeRegistry.All
            .Where(t => t.SurfaceDark.R == t.SurfaceMedium.R
                        && t.SurfaceDark.G == t.SurfaceMedium.G
                        && t.SurfaceDark.B == t.SurfaceMedium.B)
            .Select(t => t.Name)
            .ToList();

        Check.WithCustomMessage(
                "These themes now paint the page and the content card the same colour: "
                + string.Join(", ", same)
                + ". The header's contrast gate exists because its backdrop is SurfaceDark while "
                + "a row description's is SurfaceMedium; if those have merged, fold the two gates "
                + "together deliberately rather than leaving a duplicate.")
            .That(same).IsEmpty();
    }
}
