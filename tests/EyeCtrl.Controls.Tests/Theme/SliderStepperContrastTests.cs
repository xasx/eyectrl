namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// <c>DwellSlider</c>'s (−) and (+) steppers must be VISIBLE as objects, in every registered
/// theme. This is the palette half of a pair;
/// <c>DwellSliderStepperTests.TheSteppers_HaveAPerceptibleBoundaryAgainstThePage</c> is the
/// structural half that reads the real brushes off a constructed control. Neither closes the gap
/// alone — the same division of labour as
/// <see cref="ToggleContrastTests.TheSwitch_IsPerceptibleAgainstTheRowCard"/> and its structural
/// partner, and the reason this file exists rather than another assertion over there.
///
/// <para>
/// It came from live use, not from a number. The steppers are drawn 100 x 100 — exactly the hit
/// circle they register, the WYSIWYG floor — and were still reported on the tracker as "too small
/// / hard to aim at". They are not small; they had no edge. Their <c>SurfaceMedium</c> fill on the
/// settings page's <c>SurfaceDark</c> is a 1.23 / 1.23 / 1.14 step across Midnight / Ember /
/// Contrast, sampled off a real render as <c>#292944</c> on <c>#19192D</c>, so the 100 DIP extent
/// was invisible and the user was aiming at the bare glyph.
/// </para>
/// <para>
/// Reads each <see cref="ITheme"/> directly rather than through <c>Tokens.Current</c>: these run
/// in parallel with everything else, and mutating the active theme would race. That is the whole
/// content of the "no <c>Tokens.Activate</c> in this project" rule — a cross-theme gate that
/// activates nothing is exactly what <see cref="SliderPlateContrastTests"/> and
/// <see cref="ToggleContrastTests"/> already are.
/// </para>
/// </summary>
public class SliderStepperContrastTests
{
    /// <summary>
    /// A non-text user-interface component boundary owes 3:1 under WCAG 1.4.11. The stepper's
    /// edge is what says "the target is this big", so this is the floor it has to clear.
    /// </summary>
    private const double ComponentBoundaryFloor = 3.0;

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

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

    /// <summary>
    /// The stepper reads as an object against the page it sits on. Same <c>max(fill, outline)</c>
    /// shape as the track, the knob, the switch and the dwell bar: either carrier will do, and
    /// today only the outline does any work.
    ///
    /// <para>
    /// The backdrop is <see cref="ITheme.SurfaceDark"/>, not <c>SurfaceMedium</c>: the settings
    /// content card deliberately carries no fill of its own (<c>668384fa</c>, gated by
    /// <c>SettingsContentCardBoundaryTests.TheCard_CarriesNoFillOfItsOwn</c>), so a stepper's real
    /// backdrop is the window. If the card is ever given a fill again, this pairing moves with it
    /// — and that fill would first have to clear the same 3:1 against the stepper's own.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheStepper_IsPerceptibleAgainstThePage(ITheme theme)
    {
        var fill = Contrast(theme.SurfaceMedium, theme.SurfaceDark);
        var outline = Contrast(theme.TextSecondary, theme.SurfaceDark);
        var best = Math.Max(fill, outline);

        Check.WithCustomMessage(
                $"{theme.Name}: the slider's stepper reaches only {best:F2}:1 against the settings "
                + $"page (fill SurfaceMedium {fill:F2}:1, outline TextSecondary {outline:F2}:1). "
                + "Below 3:1 the 100 DIP target has no visible extent and the user aims at the "
                + "bare glyph instead — which is exactly what live testing on the tracker "
                + "reported. Brighten the theme's TextSecondary, or give the stepper a fill that "
                + "clears the floor on its own; do not simply delete the outline.")
            .That(best).IsGreaterOrEqualThan(ComponentBoundaryFloor);
    }

    /// <summary>
    /// Why <c>TextSecondary</c> and not <c>BorderSubtle</c>, stated as a fact about the palette so
    /// it cannot decay into a stale comment.
    ///
    /// <para>
    /// Both clear 3:1 against <see cref="ITheme.SurfaceDark"/> — <c>BorderSubtle</c> measures
    /// 3.35 / 3.32 / 21.00, <c>TextSecondary</c> 6.92 / 8.34 / 17.14 — so a gate at the floor
    /// alone would not distinguish them. The distinction is MARGIN: <c>BorderSubtle</c> clears
    /// Ember by 0.32, one palette tweak from failing, and it is the card's brush, where a quieter
    /// edge is right for a passive grouping. An aimable target is a different job and takes the
    /// louder brush — which is also what <c>DwellToggle</c>'s switch outline settled on, and a
    /// stepper and a switch play the same role on the same page.
    /// </para>
    /// <para>
    /// If a theme ever brightens <c>BorderSubtle</c> past <c>TextSecondary</c> here, this fails
    /// and the choice gets revisited deliberately rather than by inertia.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheChosenOutline_HasMoreMarginThanTheCardsBrush_OrContrastIsAlreadyEnormous(
        ITheme theme)
    {
        var chosen = Contrast(theme.TextSecondary, theme.SurfaceDark);
        var alternative = Contrast(theme.BorderSubtle, theme.SurfaceDark);

        // Contrast is the theme whose BorderSubtle is pure white on pure black: 21.00, the
        // arithmetic maximum, and nothing can beat it. There the choice is not about margin —
        // both are far past the floor — so the guard is that the chosen brush is itself well
        // clear, with the same 2x-the-floor headroom the other two themes give it.
        var floorWithMargin = ComponentBoundaryFloor * 2;

        Check.WithCustomMessage(
                $"{theme.Name}: the stepper's chosen outline (TextSecondary) reaches "
                + $"{chosen:F2}:1 against the page while BorderSubtle — the card's brush, which "
                + $"was the alternative — reaches {alternative:F2}:1, and the chosen one is "
                + $"neither the better of the two nor clear of {floorWithMargin:F1}:1 on its own. "
                + "The stepper was given the louder brush on purpose: it is a dwell target, not a "
                + "passive grouping, and BorderSubtle clears the 3:1 floor by only 0.32 in Ember. "
                + "If the palette has genuinely moved, re-render the row and re-pick rather than "
                + "relaxing this.")
            .That(chosen >= alternative || chosen >= floorWithMargin).IsTrue();
    }

    /// <summary>
    /// The negative half, and the reason the stepper's own fill cannot carry its boundary. Held as
    /// a fact about the palette, exactly as
    /// <c>SettingsContentCardBoundaryTests.TheCardsFill_CannotCarryTheBoundary_WhichIsWhyTheOutlineExists</c>
    /// is: if a restyle ever makes <c>SurfaceMedium</c> clear 3:1 on <c>SurfaceDark</c>, the
    /// outline becomes a style choice rather than a necessity and that should be noticed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheStepperFill_CannotCarryTheBoundary_WhichIsWhyTheOutlineExists(ITheme theme)
    {
        var fill = Contrast(theme.SurfaceMedium, theme.SurfaceDark);

        Check.WithCustomMessage(
                $"{theme.Name}: SurfaceMedium on SurfaceDark now reaches {fill:F2}:1, so a "
                + "stepper would have a visible edge from its fill alone. The outline exists "
                + "because it did not — Midnight measured 1.23:1 off a real render. If the "
                + "palette has really moved, revisit DwellSlider.StepperOutline deliberately and "
                + "update its comment rather than deleting this test.")
            .That(fill).IsStrictlyLessThan(ComponentBoundaryFloor);
    }
}
