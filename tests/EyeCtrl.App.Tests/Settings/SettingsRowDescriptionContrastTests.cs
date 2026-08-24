namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// A settings row's description is body text the user has to actually read, on a surface the
/// theme picker changes underneath it — and the user's only input channel is an eye tracker, so
/// "squint at it" is not a workaround available to them.
///
/// <para>
/// It renders at <see cref="Tokens.FontSizeSubtitle"/> in normal weight, which is not WCAG
/// "large text" (that needs 18pt = 24 DIP, or 14pt bold), so it owes the full 4.5:1 — not the
/// 3.0 the modal buttons are held to. Its backdrop is <c>ContentCard</c>'s
/// <c>SurfaceMedium</c>.
/// </para>
/// <para>
/// Measured across every registered theme, <c>TextDim</c> on <c>SurfaceMedium</c> is
/// 4.37 (Midnight) / 4.52 (Ember) / 11.01 (Contrast) — Midnight FAILS, which is why the row uses
/// <c>TextSecondary</c>: 5.63 / 6.79 / 15.04, the dimmest token that clears 4.5 everywhere.
/// (For reference <c>TextPrimary</c> is 10.33 / 12.01 / 18.42, and the name line uses it.)
/// </para>
/// <para>
/// The gate runs per THEME rather than against whatever <c>Tokens.Current</c> happens to be —
/// same reason as <see cref="EyeCtrl.App.Tests.ModalButtonPaletteTests"/>: the picker varies both
/// tokens underneath this pairing, and a gate reading only the active theme would let a new or
/// edited theme ship an unreadable description. It lives in EyeCtrl.App.Tests, not
/// EyeCtrl.Controls.Tests, because <c>Tokens.Activate</c> is banned there (parallel
/// theme-reading tests would race) and nothing here activates anything.
/// </para>
/// </summary>
public class SettingsRowDescriptionContrastTests
{
    /// <summary>The WCAG AA floor for normal-weight body text.</summary>
    private const double NormalTextFloor = 4.5;

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
    public static TheoryData<ITheme> EveryTheme()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All) data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTheme))]
    public void TheDescriptionBrush_ClearsTheNormalTextFloor_InEveryTheme(ITheme theme)
    {
        var ratio = Contrast(theme.SurfaceMedium, theme.TextSecondary);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme a settings row's description reads {1:F2}:1 against the content "
                + "card, below the {2:F1} WCAG AA floor for normal-weight text. It renders at "
                + "FontSizeSubtitle in normal weight, so it does not qualify for the 3.0 "
                + "large-text allowance. Brighten the theme's TextSecondary, or move the "
                + "description to TextPrimary — do NOT move it to TextDim, which already fails "
                + "this floor in Midnight (4.37).",
                theme.Name, ratio, NormalTextFloor))
            .That(ratio).IsGreaterOrEqualThan(NormalTextFloor);
    }

    /// <summary>
    /// The negative half, and the reason the row does not simply use the dimmest token available:
    /// <c>TextDim</c> on the card fails 4.5:1 in at least one theme. Asserted as a FACT about the
    /// palette, so that if a theme is ever brightened enough for TextDim to clear the floor
    /// everywhere, this fails and the choice above can be revisited deliberately rather than
    /// remaining a stale comment.
    /// </summary>
    [Fact]
    public void TextDim_WouldFailThatFloor_SoTheRowDoesNotUseIt()
    {
        var failing = ThemeRegistry.All
            .Where(t => Contrast(t.SurfaceMedium, t.TextDim) < NormalTextFloor)
            .Select(t => string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:F2}", t.Name, Contrast(t.SurfaceMedium, t.TextDim)))
            .ToList();

        Check.WithCustomMessage(
                "TextDim on SurfaceMedium now clears 4.5:1 in every theme. The settings row's "
                + "description deliberately uses TextSecondary because it did not (Midnight "
                + "measured 4.37); if the palette has moved, that choice can be revisited — but "
                + "do it deliberately, and update the comment in "
                + "SettingsWindow.LabelledSettingsRow rather than deleting this test.")
            .That(failing).Not.IsEmpty();
    }
}
