namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// <c>DwellComboBox</c>'s unmatched-value marker — the entry that names a stored code no option
/// matches, so the row stops rendering as if nothing were configured — must be usable in every
/// registered theme. This is the palette half of a pair;
/// <c>DwellComboBoxSelectionTests.TheUnmatchedMarker_NamesItsBrushAndThickness</c> is the
/// structural half that reads the real brushes off a constructed control. Neither closes the gap
/// alone — the same division of labour as <see cref="SliderStepperContrastTests"/> and its
/// structural partner, and the reason this file exists rather than another assertion over there.
///
/// <para>
/// <b>The state does not ride the outline colour, and these tests say so.</b> A marker must be
/// distinguishable from the two option states around it — <see cref="ITheme.ToggleOn"/> and
/// <see cref="ITheme.BorderSubtle"/> — and in <see cref="ContrastTheme"/> no token does that:
/// <c>AccentWarning</c> is 1.07:1 against the unselected white and 1.28:1 against the selected
/// green, <c>AccentSuccess</c> is byte-identical to <c>ToggleOn</c>, and the best available
/// token is <c>AccentDanger</c> at 1.88:1 against the selected state. Colour alone therefore
/// CANNOT carry this state in the theme chosen by the users who need structure most. The state
/// rides the shape (a warning glyph that is neither ◉ nor ◯) and the words "not available";
/// <c>DwellComboBoxSelectionTests.TheUnmatchedMarker_CarriesTheStateInItsTextNotItsColour</c>
/// holds that, and these theories do not claim it.
/// </para>
/// <para>
/// What these theories DO gate is that the marker is a visible object and its label readable —
/// the failure mode where the text carrier exists but is invisible. Each test names the pair it
/// measures, because the pairing that matters most here cannot be gated by contrast at all.
/// </para>
/// <para>
/// Reads each <see cref="ITheme"/> directly rather than through <c>Tokens.Current</c>: these run
/// in parallel with everything else, and mutating the active theme would race.
/// </para>
/// </summary>
public class ComboBoxUnmatchedMarkerContrastTests
{
    /// <summary>WCAG 1.4.11: a non-text component boundary owes 3:1.</summary>
    private const double ComponentBoundaryFloor = 3.0;

    /// <summary>
    /// The label's floor. <c>Tokens.FontSizeTitle</c> at <c>FontWeightMedium</c> is WCAG "large
    /// text" (3:1), but the stored value is the single piece of content this marker exists to
    /// communicate, and the repo's text tokens are held to the normal-text floor in every gate
    /// that reads them. Staying at 4.5 keeps one number for all of them.
    /// </summary>
    private const double LabelFloor = 4.5;

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
    /// Pair measured: <c>AccentWarning</c> vs <c>SurfaceDark</c> (the page behind the row) and
    /// <c>AccentWarning</c> vs <c>SurfaceMedium</c> (the marker's own fill). Both sides of the
    /// outline must hold: an outline exactly the fill's colour vanishes inside the pill while
    /// still contrasting with the page, and one matching the page vanishes against the page
    /// while still contrasting with the pill. Measured, the weaker side is 6.43:1 across the
    /// three themes, so this is a floor with real headroom rather than a number the palette
    /// grazes.
    ///
    /// <para>
    /// This test does NOT measure the marker against <c>ToggleOn</c> or <c>BorderSubtle</c>, and
    /// cannot: see the class remarks — no Contrast token separates from both, and the state is
    /// carried by the glyph and wording instead. The outline is a redundant normal-vision
    /// channel, gated here only so it is not an invisible smudge.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheMarkerOutline_IsPerceptibleAsAnObject(ITheme theme)
    {
        var againstPage = Contrast(theme.AccentWarning, theme.SurfaceDark);
        var againstFill = Contrast(theme.AccentWarning, theme.SurfaceMedium);

        Check.WithCustomMessage(
                $"{theme.Name}: the unmatched marker's outline reaches {againstPage:F2}:1 against "
                + $"the page (AccentWarning vs SurfaceDark) and {againstFill:F2}:1 against its own "
                + "fill (AccentWarning vs SurfaceMedium). The weaker edge is what makes the marker "
                + "a visible object; below 3:1 the outline is a smudge and only the label remains. "
                + "This does not claim the outline separates the marker from the option states — "
                + "no Contrast token does; that is the glyph's and the wording's job.")
            .That(Math.Min(againstPage, againstFill)).IsGreaterOrEqualThan(ComponentBoundaryFloor);
    }

    /// <summary>
    /// Pair measured: <c>TextPrimary</c> vs <c>SurfaceMedium</c> — the marker label on the
    /// marker fill. This is the text carrier itself, so it is the gate that matters most outside
    /// the structural file.
    ///
    /// <para>
    /// It is drawn in the same brush every option's caption uses on the same fill, so the
    /// pairing is exercised by every option on the page; it is restated here because a "tidy-up"
    /// that dims the marker to match a disabled-control idiom would move the label off the
    /// option-label pairing, and nothing in the structural test can see which brush a theme
    /// hands to a token.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheMarkerLabel_ReadsOnTheMarkerFill(ITheme theme)
    {
        var contrast = Contrast(theme.TextPrimary, theme.SurfaceMedium);

        Check.WithCustomMessage(
                $"{theme.Name}: the marker's label reaches {contrast:F2}:1 on its fill "
                + "(TextPrimary vs SurfaceMedium). The label carries the stored value and the "
                + "reason it is not selectable; a dimmed label is the one piece of the marker a "
                + "user cannot afford to squint at.")
            .That(contrast).IsGreaterOrEqualThan(LabelFloor);
    }

    /// <summary>
    /// A fact about the palette, asserted so the reasoning above cannot decay into a stale
    /// comment: <c>AccentWarning</c> — the marker's chosen outline — cannot be told from an
    /// unselected option's outline (<c>BorderSubtle</c>) or from a selected one
    /// (<c>ToggleOn</c>) in any registered theme. The BEST of the two pairings is only
    /// 2.36 / 2.38 / 1.28 across Midnight / Ember / Contrast — still under the 3:1 floor — and
    /// the genuine worst is 1.81 / 1.89 / 1.07.
    ///
    /// <para>
    /// This is WHY the marker carries its state in the glyph and the words. If a palette edit
    /// ever gives the outline pair enough separation to clear 3:1, this fails and the design can
    /// be revisited deliberately — the colour would become a second full channel, and the
    /// comments in the control and this file would need to say so rather than continuing to
    /// claim the text is the only carrier.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void NoOutlineColour_CanCarryTheStaleState(ITheme theme)
    {
        var vsUnselected = Contrast(theme.AccentWarning, theme.BorderSubtle);
        var vsSelected = Contrast(theme.AccentWarning, theme.ToggleOn);

        Check.WithCustomMessage(
                $"{theme.Name}: AccentWarning now reaches {vsUnselected:F2}:1 against "
                + $"BorderSubtle and {vsSelected:F2}:1 against ToggleOn — it can carry the stale "
                + "state as a colour in this theme. That would be an improvement, and this test "
                + "asserts the OLD fact on purpose: revisit the marker's design and the comments "
                + "in DwellComboBox and this file rather than deleting the assertion, because "
                + "until it is revisited the code claims colour is not the carrier.")
            .That(Math.Max(vsUnselected, vsSelected)).IsStrictlyLessThan(ComponentBoundaryFloor);
    }
}
