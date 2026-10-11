namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// §4.1.3's guarantee, extended to every ring style. HaloContrastTests proves the palette
/// can hold an edge; this checks each style actually uses a pair that does, whatever the
/// user picked in the colour picker and whatever theme is active — a pairing check plus a
/// regression net, not a demonstration that the guarantee holds across backdrops (see
/// <see cref="StrongestVisibleEdge"/>). Most of this file is asserted at full opacity only;
/// <see cref="CircleOpacity_WorstCaseAcrossEveryStyleThemeAndPreset_IsPinned"/> is the one
/// test that models <c>CircleOpacity</c>, via <see cref="LowestSafeOpacity"/>.
/// </summary>
public class CursorRingStyleContrastTests
{
    // Keep in sync with HaloContrastTests' copy of this helper block (Channel, Luminance,
    // Contrast, Parse, StrongestVisibleEdge, Backdrops, EdgeFloor): a fix to the contrast
    // maths here must be made there too, or the two files pass while asserting different
    // definitions of the same guarantee under the same EdgeFloor name.

    /// <summary>Backdrops a transparent overlay may be drawn over. Mirrors spec §9.5.</summary>
    private static readonly string[] Backdrops =
    [
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF",
        "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
    ];

    private const double EdgeFloor = 7.0;

    /// <summary>
    /// WCAG 2.1 SC 1.4.11's non-text floor — the same one <c>HaloContrastTests.EdgeFloor</c>
    /// cites for §1. Used only by <see cref="BiTonalSegments_OneFamilyClearsThePerceptibilityFloorOnEveryBackdrop"/>,
    /// whose property is weaker than <see cref="EdgeFloor"/> on purpose: it is asking whether
    /// one band family is perceivable at all, not whether the (here, non-perimeter) internal
    /// edge is strong.
    /// </summary>
    private const double PerceptibilityFloor = 3.0;

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

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// Standard alpha-over compositing, per channel, byte-rounded — matching what a WPF
    /// framebuffer actually stores. <paramref name="fg"/> is drawn at <paramref name="alpha"/>
    /// over the opaque <paramref name="bg"/>; the result is itself opaque.
    /// </summary>
    private static Color Blend(Color fg, Color bg, double alpha)
    {
        byte Mix(byte f, byte b) => (byte)Math.Round(f * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    /// <summary>
    /// The strongest edge available to the eye: mark-vs-halo (backdrop independent),
    /// mark-vs-backdrop, or halo-vs-backdrop.
    ///
    /// Taking the maximum is deliberate and is what the guarantee claims: the mark is
    /// discernible if ANY of its edges is strong. Consequence for this file: the
    /// backdrop-independent mark-vs-halo term dominates for every style and preset shipped
    /// today (worst case 9.13:1, Midnight's "Amber" against StrokeHalo), so the
    /// <see cref="Backdrops"/> loop cannot change an outcome. It is retained as documentation
    /// of the backdrops the guarantee must hold over (spec §9.5), and as a regression net if
    /// a future palette, preset or style pairing weakens that term — not as a
    /// backdrop-discrimination test. See <see cref="HaloContrastTests.StrongestVisibleEdge"/>
    /// for the fuller reasoning.
    ///
    /// The dominant mark-vs-halo term does not mean the same thing visually for all three
    /// styles. For <see cref="CursorRingStyle.WeightedSandwich"/> and
    /// <see cref="CursorRingStyle.AchromaticReticle"/> the two bands are concentric, so this
    /// edge is a continuous ring running the full perimeter. For
    /// <see cref="CursorRingStyle.BiTonalSegments"/> the bands sit at the same radius and
    /// width with complementary dash phases, so the mark and halo never share a perimeter —
    /// this edge instead renders as 32 junctions across the stroke width, where a mark segment
    /// ends and a halo segment begins (or vice versa). The number is real (the two colours do
    /// meet), but "9.13:1 along the whole ring" and "9.13:1 at 32 short seams" are not the same
    /// guarantee to the eye. BiTonal's actual per-backdrop guarantee — that at least one
    /// segment family is perceivable against the backdrop on its own — is pinned separately by
    /// <see cref="BiTonalSegments_OneFamilyClearsThePerceptibilityFloorOnEveryBackdrop"/>.
    /// </summary>
    private static double StrongestVisibleEdge(Color mark, Color halo, Color backdrop) =>
        Math.Max(Contrast(mark, halo),
            Math.Max(Contrast(mark, backdrop), Contrast(halo, backdrop)));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryStyle_KeepsTheGuarantee_ForEveryPresetAndBackdrop(ITheme theme)
    {
        foreach (var style in Enum.GetValues<CursorRingStyle>())
        {
            foreach (var hex in DwellColorPicker.PresetsFor(theme))
            {
                var stack = CursorRingStacks.For(
                    style, 250, 3, 1.0, hex, Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

                var outer = Parse(stack.Outer.Hex);
                var core = Parse(stack.Core.Hex);

                foreach (var b in Backdrops)
                {
                    var edge = StrongestVisibleEdge(core, outer, Parse(b));
                    Check.WithCustomMessage(
                            $"{theme.Name}/{style}: preset '{hex}' ({hex}) reaches "
                            + $"only {edge:F2}:1 over {b}; §4.1.3 requires >= {EdgeFloor}.")
                        .That(edge).IsStrictlyGreaterThan(EdgeFloor);
                }
            }
        }
    }

    /// <summary>
    /// BiTonal's real guarantee (a PR review's diagnosis, corrected — see the class remarks
    /// on <see cref="StrongestVisibleEdge"/>): the mark-vs-halo term above never renders as a
    /// perimeter edge for this style, so it is not evidence that the style is legible. What
    /// the spec actually promises is "half the circumference is always at full weight against
    /// any backdrop" — i.e. on every backdrop, at least one of the two segment families must
    /// be perceivable against it on its own: <c>max(contrast(mark, backdrop),
    /// contrast(halo, backdrop)) >= 3.0</c> (<see cref="PerceptibilityFloor"/>).
    ///
    /// This is deliberately NOT <c>min(...)</c>: requiring BOTH families to clear a floor
    /// against every backdrop fails on 8 of the 9 backdrops here even at 3.0 (e.g. the halo is
    /// only 1.07:1 against black; the accent is only 1.25:1 against white) — because one
    /// family fading into the backdrop while the other carries is the whole point of the
    /// style, not a defect.
    ///
    /// Not asserted for <see cref="CursorRingStyle.WeightedSandwich"/> or
    /// <see cref="CursorRingStyle.AchromaticReticle"/>: those are concentric stacks whose
    /// internal mark/halo edge IS a perimeter edge, so <see cref="EdgeFloor"/> above is the
    /// property that matters for them, not this one.
    ///
    /// Verified worst case across all four themes' presets and all nine backdrops: Daylight's
    /// "Green" ring preset (<c>#005200</c> — see <c>DwellColorPicker.RingGreen</c>) against
    /// <c>#FF5C7A</c>, 3.20:1 (mark 3.20:1, halo 2.97:1). Among the dark themes it remains
    /// Midnight's "Amber" (<c>#E8A33C</c>) against <c>#404040</c>, 4.81:1 (mark 4.81:1,
    /// halo 1.90:1).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BiTonalSegments_OneFamilyClearsThePerceptibilityFloorOnEveryBackdrop(ITheme theme)
    {
        foreach (var hex in DwellColorPicker.PresetsFor(theme))
        {
            var stack = CursorRingStacks.For(
                CursorRingStyle.BiTonalSegments, 250, 3, 1.0,
                hex, Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

            var mark = Parse(stack.Core.Hex);
            var halo = Parse(stack.Outer.Hex);

            foreach (var b in Backdrops)
            {
                var backdrop = Parse(b);
                var markContrast = Contrast(mark, backdrop);
                var haloContrast = Contrast(halo, backdrop);
                var strongestFamily = Math.Max(markContrast, haloContrast);

                Check.WithCustomMessage(
                        $"{theme.Name}/BiTonalSegments: preset '{hex}' ({hex}) over {b} — "
                        + $"mark {markContrast:F2}:1, halo {haloContrast:F2}:1; neither family reaches "
                        + $"the {PerceptibilityFloor}:1 perceptibility floor.")
                    .That(strongestFamily).IsGreaterOrEqualThan(PerceptibilityFloor);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AchromaticReticle_DoesNotDependOnThePresetAtAll(ITheme theme)
    {
        // Its bands are TextPrimary and StrokeHalo, so the colour picker cannot weaken it —
        // which is why the picker is swapped out for a note under this style.
        var withAccent = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0,
            Hex(theme.Accent), Hex(theme.StrokeHalo), Hex(theme.TextPrimary));
        var withMagenta = CursorRingStacks.For(
            CursorRingStyle.AchromaticReticle, 250, 3, 1.0,
            "#FF00FF", Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

        Check.That(withMagenta).IsEqualTo(withAccent);

        var edge = Contrast(Parse(withAccent.Core.Hex), Parse(withAccent.Outer.Hex));
        Check.WithCustomMessage($"{theme.Name}: reticle edge is only {edge:F2}:1.")
            .That(edge).IsStrictlyGreaterThan(EdgeFloor);
    }

    /// <summary>
    /// The opacity slider's own step (<c>CursorPageSource</c>'s <c>c_opacity</c> row), ascending
    /// so <see cref="LowestSafeOpacity"/> can return the first (lowest) value that passes without
    /// assuming the edge is monotonic in opacity.
    /// </summary>
    private static readonly double[] OpacitySteps = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0];

    /// <summary>
    /// The lowest opacity step at which <paramref name="style"/>'s internal edge still clears
    /// <see cref="EdgeFloor"/>, for the given theme and mark colour. Every band involved except
    /// the core is opaque (<see cref="CursorRingStacks.For"/> applies <c>opacity</c> to the core
    /// band only), so it is only ever the core's composite that this needs to model.
    ///
    /// Concentric styles (<see cref="CursorRingStyle.WeightedSandwich"/>,
    /// <see cref="CursorRingStyle.AchromaticReticle"/>) paint the core directly over the opaque
    /// outer band on the same centre path, so the composite is <c>core*a + outer*(1-a)</c> and
    /// backdrop-independent. <see cref="CursorRingStyle.BiTonalSegments"/> places its two bands
    /// side by side at one radius instead — the core has no outer band under it, only whatever
    /// backdrop the transparent overlay sits on — so its composite is <c>core*a + backdrop*(1-a)</c>,
    /// checked against <see cref="Backdrops"/>, worst backdrop wins.
    /// </summary>
    private static double LowestSafeOpacity(CursorRingStyle style, ITheme theme, string markHex)
    {
        var stack = CursorRingStacks.For(
            style, 250, 3, 1.0, markHex, Hex(theme.StrokeHalo), Hex(theme.TextPrimary));
        var core = Parse(stack.Core.Hex);
        var outer = Parse(stack.Outer.Hex);

        foreach (var a in OpacitySteps)
        {
            var edge = style == CursorRingStyle.BiTonalSegments
                ? Backdrops.Min(b => Contrast(Blend(core, Parse(b), a), outer))
                : Contrast(Blend(core, outer, a), outer);

            if (edge > EdgeFloor)
                return a;
        }

        return double.PositiveInfinity;
    }

    /// <summary>
    /// B1's real threshold: the lowest <c>CircleOpacity</c> slider step at which EVERY shipped
    /// (style, theme, preset) combination's internal edge still clears <see cref="EdgeFloor"/>.
    /// Corrects an earlier doc-comment claim (see <c>CursorSettings.CircleOpacity</c>) that once
    /// the outer band is opaque, opacity no longer needs to be modelled at all — wrong, because
    /// the core composites OVER that opaque band (or, for <see cref="CursorRingStyle.BiTonalSegments"/>,
    /// over the backdrop) and converges on whatever is behind it as opacity falls. The shipped
    /// default alone (Midnight, <c>WeightedSandwich</c>, the Accent preset) only needs 0.7
    /// (6.03:1 at 0.6, 7.92:1 at 0.7), but several dark-on-white-halo presets need 0.9 under
    /// both <c>WeightedSandwich</c> and <c>BiTonalSegments</c> — Midnight's Amber
    /// (<c>AccentWarning</c>), tied by Daylight's ring green (<c>#005200</c>) and its warning.
    /// 0.9 is therefore the worst case across every combination shipped today, and the value
    /// this test pins. A palette or preset change that moves it must fail here and update
    /// <c>CircleOpacity</c>'s doc comment together with it.
    /// </summary>
    private const double WorstCaseOpacityThreshold = 0.9;

    [Fact]
    public void CircleOpacity_WorstCaseAcrossEveryStyleThemeAndPreset_IsPinned()
    {
        var worst = 0.0;
        var worstLabel = "";

        foreach (var theme in ThemeRegistry.All)
        {
            foreach (var style in Enum.GetValues<CursorRingStyle>())
            {
                foreach (var hex in DwellColorPicker.PresetsFor(theme))
                {
                    var threshold = LowestSafeOpacity(style, theme, hex);
                    if (threshold > worst)
                    {
                        worst = threshold;
                        worstLabel = $"{theme.Name}/{style}: preset '{hex}' ({hex})";
                    }
                }
            }
        }

        Check.WithCustomMessage($"worst case across every style/theme/preset is {worstLabel} at opacity {worst}; " +
                                 $"expected {WorstCaseOpacityThreshold}.")
            .That(worst).IsEqualTo(WorstCaseOpacityThreshold);
    }
}
