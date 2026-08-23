namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Toggle state must be carried by lightness, not hue (spec §4.1.1 rule 7), in
/// every theme. The pre-migration pair managed 1.52:1 and was near-invisible to a
/// deuteranope.
///
/// Reads each ITheme directly rather than through Tokens.Current: these run in
/// parallel with everything else, and mutating the active theme would race.
/// </summary>
public class ToggleContrastTests
{
    /// <summary>
    /// Ceiling on ToggleOff's Lab chroma. "Off is not an error" is really a
    /// statement about saturation: gate 9 holds the semantic triad at C* >= 55, so
    /// a near-neutral off-state cannot be mistaken for one. Measured: Midnight
    /// 13.7, Ember 8.6, Contrast 0.0.
    ///
    /// This replaced an "R &lt;= B" test, which encoded "not red" as "not warm" and
    /// therefore failed Ember's warm neutral #342C21 (R 52, B 33) even though it is
    /// no more an error colour than Midnight's #2E2E42.
    /// </summary>
    private const double NeutralChromaCeiling = 20.0;

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
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Lab chroma C* = hypot(a*, b*). Mirrors tools/palette_validate.py:210-213.</summary>
    private static double Chroma(Color c)
    {
        double R = Channel(c.R), G = Channel(c.G), B = Channel(c.B);
        var x = R * 0.4124 + G * 0.3576 + B * 0.1805;
        var y = R * 0.2126 + G * 0.7152 + B * 0.0722;
        var z = R * 0.0193 + G * 0.1192 + B * 0.9505;

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;

        double fx = F(x / 0.95047), fy = F(y / 1.0), fz = F(z / 1.08883);
        return Math.Sqrt(Math.Pow(500 * (fx - fy), 2) + Math.Pow(200 * (fy - fz), 2));
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOnAndOff_DifferByAtLeastThreeToOne(ITheme theme)
    {
        var ratio = Contrast(theme.ToggleOn, theme.ToggleOff);
        Check.WithCustomMessage(
                $"{theme.Name}: ToggleOn/ToggleOff contrast is {ratio:F2}:1; spec "
                + "§4.1.1 rule 7 requires >= 3.0 so state survives any colour-vision "
                + "deficiency.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOff_IsANeutralNotAnErrorColour(ITheme theme)
    {
        // "Off" is not an error. Saturated hue is reserved for the semantic triad,
        // which gate 9 holds at C* >= 55 (§4.1.2, tools/palette_validate.py:132).
        var chroma = Chroma(theme.ToggleOff);
        Check.WithCustomMessage(
                $"{theme.Name}: ToggleOff is at C* {chroma:F1}, above the {NeutralChromaCeiling} "
                + "ceiling — it has become a hue rather than a neutral, and risks "
                + "reading as a semantic state.")
            .That(chroma).IsStrictlyLessThan(NeutralChromaCeiling);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOff_IsTheDarkerOfThePair(ITheme theme)
    {
        // Direction matters as much as separation: an "off" brighter than "on"
        // would satisfy the 3:1 rule while inverting the affordance.
        Check.WithCustomMessage($"{theme.Name}: ToggleOff must be darker than ToggleOn")
            .That(Luminance(theme.ToggleOff)).IsStrictlyLessThan(Luminance(theme.ToggleOn));
    }

    /// <summary>
    /// Cross-theme half of a two-part guard on the switch's TRACK;
    /// <c>DwellToggleVisualTests.TheTrackIsPerceptibleAgainstTheSwitchFill</c> is the structural
    /// half. The division of labour is exact and worth stating, because each half is blind to what
    /// the other sees. This theory reads the PALETTE — <c>theme.TextSecondary</c> here is the
    /// hardcoded token, not whatever brush the control happens to draw — so it proves the outline
    /// colour is necessary and sufficient in every theme, and it CANNOT fail on a code change:
    /// deleting the track's BorderBrush outright leaves it green. The structural half reads the
    /// real brushes off a constructed control under one ambient theme, so it proves the code draws
    /// an outline at all but says nothing about the other two palettes. Neither closes the gap
    /// alone — the established pairing in this repo.
    ///
    /// <para>
    /// This theory REPLACED <c>ProgressBar_IsPerceptibleAgainstEitherToggleFill</c>, which gated
    /// the dwell bar against ToggleOn/ToggleOff. That pairing can no longer occur: the toggle is a
    /// composite now, its one dwell target is a <c>DwellButton</c> whose fill is
    /// <see cref="ITheme.SurfaceLight"/>, and that button draws its bar in its own layout ROW 1
    /// while the track sits in row 0 — so the bar is never drawn on a toggle fill at all. Its
    /// perceptibility against SurfaceLight is gated by
    /// <see cref="DwellButtonProgressBarContrastTests"/>, which now lists that fill.
    /// </para>
    /// <para>
    /// What matters instead is whether the track can be SEEN, because the track is the switch's
    /// visible extent — the thing a gaze user aims at. Measured against SurfaceLight, the state
    /// fills alone are ToggleOn 2.76 / 2.70 / 11.03 and ToggleOff 1.23 / 1.25 / 1.39 across
    /// Midnight / Ember / Contrast: the ON track fails 3:1 in two of three themes and the OFF
    /// track is invisible in all three. TextSecondary as an outline measures 4.38 / 5.38 / 12.35,
    /// which is why the track's perceptibility is made independent of its fill. Gates the same
    /// combined quantity the bar tests do — max(fill-vs-surface, outline-vs-surface) — against
    /// both states, in every theme.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheTrack_IsPerceptibleAgainstTheSwitchFill(ITheme theme)
    {
        foreach (var (name, fill) in new[] { ("ToggleOn", theme.ToggleOn), ("ToggleOff", theme.ToggleOff) })
        {
            var fillContrast = Contrast(fill, theme.SurfaceLight);
            var outlineContrast = Contrast(theme.TextSecondary, theme.SurfaceLight);
            var best = Math.Max(fillContrast, outlineContrast);

            Check.WithCustomMessage(
                    $"{theme.Name}: the {name} track reaches only {best:F2}:1 against the switch's "
                    + $"SurfaceLight fill (fill {fillContrast:F2}:1, outline "
                    + $"{outlineContrast:F2}:1) — below 3:1 the switch has no visible extent, so "
                    + "there is nothing on screen for a gaze user to aim at.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }

    /// <summary>
    /// Cross-theme half for the SWITCH's own boundary against the row card it sits on, paired with
    /// the structural <c>DwellToggleVisualTests.TheSwitchHasAPerceptibleBoundaryAgainstTheCard</c>
    /// — palette here, real brushes there.
    ///
    /// <para>
    /// <see cref="ITheme.SurfaceLight"/> on <see cref="ITheme.SurfaceMedium"/> is a
    /// 1.29 / 1.26 / 1.22 fill step across Midnight / Ember / Contrast, so the switch has no edge
    /// of its own by fill alone and its <c>TextSecondary</c> outline is what supplies one
    /// (5.63 / 6.79 / 15.04). <c>BorderSubtle</c> — the right brush for the CARD, whose backdrop
    /// is the page — would not do here: 2.72 / 2.71 / 18.42, failing two of three themes.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheSwitch_IsPerceptibleAgainstTheRowCard(ITheme theme)
    {
        var fillContrast = Contrast(theme.SurfaceLight, theme.SurfaceMedium);
        var outlineContrast = Contrast(theme.TextSecondary, theme.SurfaceMedium);
        var best = Math.Max(fillContrast, outlineContrast);

        Check.WithCustomMessage(
                $"{theme.Name}: the switch reaches only {best:F2}:1 against the row card "
                + $"(fill {fillContrast:F2}:1, outline {outlineContrast:F2}:1) — below 3:1 the "
                + "dwell target has no edge and the user is aiming at a smudge.")
            .That(best).IsStrictlyGreaterThan(3.0);
    }

    /// <summary>
    /// Cross-theme half for the KNOB, paired the same way with the structural
    /// <c>DwellToggleVisualTests.TheKnobIsPerceptibleAgainstTheTrackFill</c> — palette here, real
    /// brushes there.
    ///
    /// <para>
    /// The knob's side is one of the three carriers that make toggle state readable without
    /// colour, and by its own fill the knob is NOT visible on the ON track: TextPrimary on ToggleOn
    /// measures 2.91 / 3.53 / 1.37 across Midnight / Ember / Contrast. The dark SurfaceDark ring
    /// carries it instead (4.37 / 4.18 / 15.30), which is why the gated quantity is
    /// max(fill, ring) — the same shape as the track's and the dwell bar's. The OFF state runs the
    /// other way round: the knob's TextSecondary fill reaches 5.37 / 6.72 / 17.14 on ToggleOff
    /// while the ring nearly vanishes into it, so neither term can be dropped.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheKnob_IsPerceptibleAgainstEitherTrackFill(ITheme theme)
    {
        foreach (var (name, track, knob) in new[]
                 {
                     ("ToggleOn", theme.ToggleOn, theme.TextPrimary),
                     ("ToggleOff", theme.ToggleOff, theme.TextSecondary),
                 })
        {
            var fillContrast = Contrast(knob, track);
            var ringContrast = Contrast(theme.SurfaceDark, track);
            var best = Math.Max(fillContrast, ringContrast);

            Check.WithCustomMessage(
                    $"{theme.Name}: the knob reaches only {best:F2}:1 against the {name} track "
                    + $"(fill {fillContrast:F2}:1, ring {ringContrast:F2}:1) — below 3:1 the knob's "
                    + "side stops carrying state, leaving this control to say it in colour alone.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }
}
