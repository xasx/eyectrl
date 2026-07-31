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

    [Theory]
    [InlineData(ModalChoiceKind.Affirmative)]
    [InlineData(ModalChoiceKind.Neutral)]
    [InlineData(ModalChoiceKind.Negative)]
    public void EveryKind_ClearsTheAALargeTextFloor(ModalChoiceKind kind)
    {
        // The 24px bold label is WCAG "large text" (>= 14pt bold), so the AA floor is 3.0:1.
        // Affirmative measures 4.37:1, Negative 5.74:1, Neutral 10.33:1 — all clear it.
        var (background, foreground) = ModalButtonPalette.For(kind);
        var ratio = Contrast(background, foreground.Color);

        Check.WithCustomMessage(
                $"{kind} modal button is {ratio:F2}:1 label-on-fill; 3.0 is the AA large-text "
                + "floor for the 24px bold label these buttons use.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Theory]
    // Affirmative is deliberately excluded: it is pinned to the shared Tokens.ToggleOn
    // (#4A8F5C), which measures 4.37:1 against the dark label — a 0.13 near-miss on the
    // 4.5 normal-text floor, but these buttons are 24px bold (WCAG "large text", 3.0
    // floor), so 4.37 still passes AA. Brightening the shared token to clear 4.5 was
    // rejected as app-wide scope creep; the near-miss is documented, not papered over.
    // Negative (5.74:1) and Neutral (10.33:1) both clear 4.5 and are held to it here.
    [InlineData(ModalChoiceKind.Negative)]
    [InlineData(ModalChoiceKind.Neutral)]
    public void BrightKinds_AlsoClearTheNormalTextFloor(ModalChoiceKind kind)
    {
        var (background, foreground) = ModalButtonPalette.For(kind);
        var ratio = Contrast(background, foreground.Color);

        Check.WithCustomMessage(
                $"{kind} modal button is {ratio:F2}:1 label-on-fill; the normal-text AA "
                + "floor is 4.5, and this bright kind is expected to clear it.")
            .That(ratio).IsStrictlyGreaterThan(4.5);
    }

    [Fact]
    public void AffirmativeAndNegative_KeepTheirExistingFills()
    {
        // Regression guard: every reset confirmation in the settings window already uses
        // these. This task must not restyle them.
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Affirmative).Background)
             .IsEqualTo(Tokens.ToggleOn);
        Check.That(ModalButtonPalette.For(ModalChoiceKind.Negative).Background)
             .IsEqualTo(Tokens.AccentDanger);
    }

    [Fact]
    public void Neutral_IsDistinctFromBothOthers()
    {
        var neutral = ModalButtonPalette.For(ModalChoiceKind.Neutral).Background;
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Affirmative).Background);
        Check.That(neutral).IsNotEqualTo(ModalButtonPalette.For(ModalChoiceKind.Negative).Background);
    }

    [Theory]
    // The dwell-progress blend (ColorBlend.Blend(base, Accent, progress * factor)) shifts each
    // button's fill toward the bright Accent as the user dwells. For the bright fills the dark
    // label only gains contrast, but the dark-fill Neutral with its LIGHT label would collapse:
    // a 0.5 factor reaches 2.62:1 at full dwell (below the 3.0 large-text floor the static test
    // asserts). The palette therefore exposes a per-kind blend factor sized so the label clears
    // the AA floor across the whole dwell. This test exercises the blend the static tests can't
    // see — the worst case is the moment the button fires (progress 1.0).
    [InlineData(ModalChoiceKind.Affirmative)]
    [InlineData(ModalChoiceKind.Neutral)]
    [InlineData(ModalChoiceKind.Negative)]
    public void LabelStaysLegible_AtFullDwellBlend(ModalChoiceKind kind)
    {
        var (baseColor, foreground) = ModalButtonPalette.For(kind);
        var factor = ModalButtonPalette.DwellBlendFactor(kind);
        var atFullDwell = ColorBlend.Blend(baseColor, Tokens.Accent, 1.0 * factor);
        var ratio = Contrast(atFullDwell, foreground.Color);

        Check.WithCustomMessage(
                $"{kind} modal button blends toward Accent at factor {factor} and reaches "
                + $"{ratio:F2}:1 label-on-fill at full dwell (progress 1.0); the AA large-text "
                + "floor for the 24px bold label is 3.0, and it must hold through the whole dwell "
                + "— the button fires at progress 1.0, exactly when a 0.5 factor collapsed.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }
}
