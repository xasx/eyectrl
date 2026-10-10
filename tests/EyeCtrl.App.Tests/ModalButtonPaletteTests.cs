namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Modal buttons are filled shapes with a label on them, so the label's contrast
/// depends on the fill. The bright semantic fills need a dark label; a neutral fill is
/// itself dark and needs a light one. Getting this backwards produced a 2.16:1 button
/// during the overlay migration, which is why the mapping is a tested function rather
/// than a literal at the call site.
/// </summary>
public class ModalButtonPaletteTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Every registered theme paired with every choice kind.
    ///
    /// These gates run per THEME, not against whatever <c>Tokens.Current</c> happens to be.
    /// The pairings were chosen against a fixed palette, and the theme picker now varies all
    /// five tokens underneath them — a gate that measures only the active theme would let a
    /// new or edited theme ship an illegible modal button. Kinds come from
    /// <see cref="Enum.GetValues{T}()"/> so a fourth kind is gated the day it is added.
    /// </summary>
    public static TheoryData<ITheme, ModalChoiceKind> EveryThemeAndKind()
    {
        var data = new TheoryData<ITheme, ModalChoiceKind>();
        foreach (var theme in ThemeRegistry.All)
        {
            foreach (var kind in Enum.GetValues<ModalChoiceKind>())
            {
                data.Add(theme, kind);
            }
        }

        return data;
    }

    /// <summary>Every registered theme paired with the kinds held to the stricter 4.5 floor.</summary>
    public static TheoryData<ITheme, ModalChoiceKind> EveryThemeAndBrightKind()
    {
        var data = new TheoryData<ITheme, ModalChoiceKind>();
        foreach (var theme in ThemeRegistry.All)
        {
            data.Add(theme, ModalChoiceKind.Negative);
            data.Add(theme, ModalChoiceKind.Neutral);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryThemeAndKind))]
    public void EveryKind_ClearsTheAALargeTextFloor(ITheme theme, ModalChoiceKind kind)
    {
        // The 24px bold label is WCAG "large text" (>= 14pt bold), so the AA floor is 3.0:1.
        // Measured at rest: Affirmative 4.37 (Midnight) / 4.18 (Ember) / 6.50 (Daylight) /
        // 15.30 (Contrast); Negative 5.74 / 5.74 / 5.42 / 8.16; Neutral 10.33 / 12.01 /
        // 17.67 / 18.42. All clear 3.0.
        var (background, foreground) = ModalButtonPalette.For(kind, theme);
        var ratio = Contrast(background, foreground.Color);

        Check.WithCustomMessage(
                $"{theme.Name}/{kind} modal button is {ratio:F2}:1 label-on-fill; 3.0 is the AA "
                + "large-text floor for the 24px bold label these buttons use.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Theory]
    // Affirmative is deliberately excluded: it is pinned to the shared ToggleOn token, which
    // measures 4.37:1 against the dark label in Midnight and 4.18:1 in Ember — a near-miss on
    // the 4.5 normal-text floor, but these buttons are 24px bold (WCAG "large text", 3.0
    // floor), so both still pass AA. Daylight's Affirmative is 6.50 (the measured label rule
    // picks its white SurfaceLight). Brightening the shared token to clear 4.5 was rejected as
    // app-wide scope creep; the near-miss is documented, not papered over. Note the worst case
    // is EMBER's 4.18, not Midnight's 4.37 — the figure to quote when revisiting this.
    // Negative (>= 5.42) and Neutral (>= 10.33) clear 4.5 in every theme and are held to it.
    [MemberData(nameof(EveryThemeAndBrightKind))]
    public void BrightKinds_AlsoClearTheNormalTextFloor(ITheme theme, ModalChoiceKind kind)
    {
        var (background, foreground) = ModalButtonPalette.For(kind, theme);
        var ratio = Contrast(background, foreground.Color);

        Check.WithCustomMessage(
                $"{theme.Name}/{kind} modal button is {ratio:F2}:1 label-on-fill; the normal-text "
                + "AA floor is 4.5, and this kind is expected to clear it in every theme.")
            .That(ratio).IsStrictlyGreaterThan(4.5);
    }

    [Theory]
    [MemberData(nameof(ToggleContrastThemes))]
    public void AffirmativeAndNegative_KeepTheirExistingFills(ITheme theme)
    {
        // Regression guard: every reset confirmation in the settings window already uses
        // these. This task must not restyle them — in any theme.
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Affirmative, theme).Background)
             .IsEqualTo(theme.ToggleOn);
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Negative, theme).Background)
             .IsEqualTo(theme.AccentDanger);
    }

    [Theory]
    [MemberData(nameof(ToggleContrastThemes))]
    public void BrightKinds_ResolveToTheExpectedLabelBrush(ITheme theme)
    {
        // Direct pin added in review round 1 (M8): the contrast floors alone let the
        // measured rule flip a label on Contrast or Daylight's Affirmative while every
        // ratio still passes. Pin WHICH brush each theme resolves to, so a flip is a
        // deliberate change to this test, not a silent drift under the floors.
        var expected = theme is DaylightTheme
            ? theme.BrushSurfaceLight
            : theme.BrushSurfaceDark;
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Affirmative, theme).Foreground)
             .IsSameReferenceAs(expected);
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Negative, theme).Foreground)
             .IsSameReferenceAs(expected);
    }

    [Theory]
    [MemberData(nameof(ToggleContrastThemes))]
    public void Neutral_IsDistinctFromBothOthers(ITheme theme)
    {
        // Per theme: nothing stops a new theme from collapsing SurfaceMedium onto ToggleOn,
        // which would make "another way forward" indistinguishable from "confirm".
        var neutral = ModalButtonPalette.For(ModalChoiceKind.Neutral, theme).Background;
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Affirmative, theme).Background);
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Negative, theme).Background);
    }

    /// <summary>Every registered theme, for the gates that do not vary by kind.</summary>
    public static TheoryData<ITheme> ToggleContrastThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
        {
            data.Add(theme);
        }

        return data;
    }

    [Theory]
    // The dwell-progress blend (ColorBlend.Blend(base, Accent, progress * factor)) shifts each
    // button's fill toward the bright Accent as the user dwells. For the bright fills the dark
    // label only gains contrast, but the dark-fill Neutral with its LIGHT label would collapse:
    // a 0.5 factor reaches 2.62:1 at full dwell (below the 3.0 large-text floor the static test
    // asserts). The palette therefore exposes a per-kind blend factor sized so the label clears
    // the AA floor across the whole dwell. This test exercises the blend the static tests can't
    // see — the worst case is the moment the button fires (progress 1.0).
    //
    // Per theme, because Accent is a theme token too: the blend TARGET moves with the theme,
    // not just the base. Worst measured full-dwell case is Midnight Neutral at 5.11:1.
    [MemberData(nameof(EveryThemeAndKind))]
    public void LabelStaysLegible_AtFullDwellBlend(ITheme theme, ModalChoiceKind kind)
    {
        var (baseColor, foreground) = ModalButtonPalette.For(kind, theme);
        var factor = ModalButtonPalette.DwellBlendFactor(kind);
        var atFullDwell = ColorBlend.Blend(baseColor, theme.Accent, 1.0 * factor);
        var ratio = Contrast(atFullDwell, foreground.Color);

        Check.WithCustomMessage(
                $"{theme.Name}/{kind} modal button blends toward Accent at factor {factor} and "
                + $"reaches {ratio:F2}:1 label-on-fill at full dwell (progress 1.0); the AA "
                + "large-text floor for the 24px bold label is 3.0, and it must hold through the "
                + "whole dwell — the button fires at progress 1.0, exactly when a 0.5 factor "
                + "collapsed.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }
}
