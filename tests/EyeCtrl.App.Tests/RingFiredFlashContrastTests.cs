namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Wave C C1 fixup-01 (ring review F1/F2): the fired flash must never make the ring LESS visible
/// than at rest. What is measured is the ring against the page, not a band against its own rest
/// colour: per backdrop and window look (0.6 quiet, 1.0 pinned), the ring's visibility is the best
/// contrast any EXPOSED band has against the backdrop (the eye uses whichever edge is strongest).
/// The peak is <see cref="FiredFlash.PeakStack"/> of the rest stack: the same bands, wider.
/// <para>
/// The colour swap shipped at e93aa0a1 failed exactly this: the owner's ring (Midnight,
/// AchromaticReticle 1.5) went from 5.22:1 (quiet) / 19.69:1 (pinned) at rest to 1.19 / 1.34:1 on
/// white at the peak; the pointer vanished. A weight step keeps every edge, so the peak equals the
/// rest in contrast and only gains width.
/// </para>
/// <para>
/// Same maths as CursorRingStyleContrastTests (WCAG luminance, byte-rounded sRGB alpha-over),
/// which is what a WPF layered window composites.
/// </para>
/// </summary>
public class RingFiredFlashContrastTests
{
    private static readonly string[] Backdrops =
    [
        "#FFFFFF", "#808080", "#000000",
        "#404040", "#C0C0C0", "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
    ];

    private static readonly double[] WindowOpacities = [0.6, 1.0];

    private static readonly CursorRingStyle[] Styles =
        [CursorRingStyle.AchromaticReticle, CursorRingStyle.WeightedSandwich, CursorRingStyle.BiTonalSegments];

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

    private static Color Blend(Color fg, Color bg, double alpha)
    {
        byte Mix(byte f, byte b) => (byte)Math.Round(f * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// The ring against the backdrop. Both bands share the ring's outer edge, so a solid core sits
    /// on the outer band and the outer band shows only where it is wider than the core; dashed
    /// (BiTonal) bands interleave and each sits on the backdrop. Each band's composite is what the
    /// window presents at <paramref name="windowOpacity"/>; the ring's visibility is the best
    /// exposed band's contrast with the backdrop.
    /// </summary>
    internal static double Visibility(CursorRingStack stack, Color backdrop, double windowOpacity)
    {
        var dashed = stack.Outer.DashPeriod > 0 || stack.Core.DashPeriod > 0;
        var outer = Blend(Parse(stack.Outer.Hex), backdrop, stack.Outer.Opacity);
        var coreUnder = dashed ? backdrop : outer;
        var core = Blend(Parse(stack.Core.Hex), coreUnder, stack.Core.Opacity);

        var best = Contrast(Blend(core, backdrop, windowOpacity), backdrop);
        var outerExposed = dashed || stack.Outer.Thickness > stack.Core.Thickness;
        if (outerExposed)
            best = Math.Max(best, Contrast(Blend(outer, backdrop, windowOpacity), backdrop));
        return best;
    }

    private static CursorRingStack RestStack(ITheme theme, CursorRingStyle style, double thickness,
        double size = 200) =>
        CursorRingStacks.For(style, size, thickness, 1.0,
            Hex(theme.Accent), Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

    /// <summary>
    /// (size, thickness) rows: the owner's ring and the default thickness at 200, plus SMALL, THICK
    /// rings that the Settings sliders reach (size 10–500, thickness 1–10), where the peak runs out
    /// of room and must share it (ring review r02 N1: 20/3.5, 30/5 and worse).
    /// </summary>
    private static readonly (double Size, double Thickness)[] Rings =
    [
        (200, 1.5), (200, 1.0),
        (20, 3.5), (20, 5), (20, 10), (30, 5), (30, 10), (12, 2),
    ];

    public static TheoryData<string, CursorRingStyle> ThemesAndStyles()
    {
        var data = new TheoryData<string, CursorRingStyle>();
        foreach (var theme in ThemeRegistry.All)
            foreach (var style in Styles)
                data.Add(theme.Name, style);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemesAndStyles))]
    public void AtThePeak_TheRingIsNeverLessVisibleThanAtRest(string themeName, CursorRingStyle style)
    {
        // Every theme x style x backdrop x look x ring (the owner's, the default, and small thick
        // ones). Mutation: a peak that recolours either band (the e93aa0a1 colour swap) → RED on
        // light backdrops; a peak core as wide as the outer band (the r02 N1 cap: it hides the light
        // band on a small thick ring) → RED on dark ones.
        var theme = ThemeRegistry.All.Single(t => t.Name == themeName);

        foreach (var (size, thickness) in Rings)
        {
            var rest = RestStack(theme, style, thickness, size);
            var peak = FiredFlash.PeakStack(rest, size);
            foreach (var alpha in WindowOpacities)
                foreach (var hex in Backdrops)
                {
                    var backdrop = Parse(hex);
                    var atRest = Visibility(rest, backdrop, alpha);
                    var atPeak = Visibility(peak, backdrop, alpha);
                    Check.WithCustomMessage(
                            $"{themeName} {style} {size}/{thickness} on {hex} at {alpha}: the ring is {atPeak:F2}:1 "
                            + $"at the peak, less visible than {atRest:F2}:1 at rest.")
                        .That(atPeak).IsGreaterOrEqualThan(atRest - 1e-9);
                }
        }
    }

    [Theory]
    [InlineData("#FFFFFF", 0.6, 5.22)]
    [InlineData("#FFFFFF", 1.0, 19.69)]
    [InlineData("#808080", 0.6, 2.90)]
    [InlineData("#808080", 1.0, 4.99)]
    [InlineData("#000000", 0.6, 5.69)]
    [InlineData("#000000", 1.0, 15.63)]
    public void TheOwnersRing_KeepsItsRestVisibilityAtThePeak(string backdropHex, double alpha, double expected)
    {
        // Midnight, AchromaticReticle, thickness 1.5, opacity 1 (settings.json, 29.10). The ring
        // review's figures at rest; the peak must equal them (a weight step changes no colour).
        var midnight = ThemeRegistry.All.Single(t => t.Name == "Midnight");
        var rest = RestStack(midnight, CursorRingStyle.AchromaticReticle, 1.5);
        var peak = FiredFlash.PeakStack(rest, 200);
        var backdrop = Parse(backdropHex);

        Check.That(Math.Round(Visibility(rest, backdrop, alpha), 2)).IsEqualTo(expected);
        Check.That(Math.Round(Visibility(peak, backdrop, alpha), 2)).IsEqualTo(expected);
    }

    [Fact]
    public void TheOwnersRing_PeakIsTheWeightStep()
    {
        // 1.5 DIP dark core → 4.5 (3 → 9 device px at 200 %); the light band's exposed 4 DIP → 8.
        var midnight = ThemeRegistry.All.Single(t => t.Name == "Midnight");
        var rest = RestStack(midnight, CursorRingStyle.AchromaticReticle, 1.5);

        var peak = FiredFlash.PeakStack(rest, 200);

        Check.That(peak.Core.Thickness).IsCloseTo(4.5, 1e-9);
        Check.That(peak.Outer.Thickness).IsCloseTo(12.5, 1e-9);
        Check.That(peak.Core.Hex).IsEqualTo(rest.Core.Hex);
        Check.That(peak.Outer.Hex).IsEqualTo(rest.Outer.Hex);
        Check.That(peak.Core.Opacity).IsEqualTo(rest.Core.Opacity);
        Check.That(peak.Outer.Opacity).IsEqualTo(rest.Outer.Opacity);
    }

    [Fact]
    public void OnEveryRingTheSlidersReach_NeitherBandIsThinnerAtThePeak_AndTheOuterBandStaysExposed()
    {
        // r02 N1: on a small, thick ring the peak runs out of room (half the ring's size). The room
        // is shared so that the core never shrinks below its rest width AND the outer band's exposed
        // part never shrinks below ITS rest width; a ring that fits at rest fits at the peak too.
        // Mutation: the old cap (core = min(3t, room), outer = min(core + 2e, room)) → the outer band
        // loses its exposed width (e.g. 30/5: exposed 4 → 0) → RED.
        foreach (var style in Styles)
            for (var size = 10.0; size <= 60; size += 2)
                for (var thickness = 1.0; thickness <= 10; thickness += 0.5)
                {
                    var rest = CursorRingStacks.For(style, size, thickness, 1.0, "#112233", "#000000", "#FFFFFF");
                    var peak = FiredFlash.PeakStack(rest, size);
                    var restExposed = Math.Max(0, rest.Outer.Thickness - rest.Core.Thickness);
                    var peakExposed = peak.Outer.Thickness - peak.Core.Thickness;
                    var where = $"{style} {size}/{thickness}";

                    Check.WithCustomMessage($"{where}: the core is thinner at the peak ({peak.Core.Thickness} < {rest.Core.Thickness}).")
                        .That(peak.Core.Thickness).IsGreaterOrEqualThan(rest.Core.Thickness - 1e-9);
                    Check.WithCustomMessage($"{where}: the outer band's exposed width shrank at the peak ({peakExposed} < {restExposed}).")
                        .That(peakExposed).IsGreaterOrEqualThan(restExposed - 1e-9);
                    if (rest.Outer.Thickness <= size / 2)
                        Check.WithCustomMessage($"{where}: a ring that fits at rest overflows its room at the peak ({peak.Outer.Thickness} > {size / 2}).")
                            .That(peak.Outer.Thickness).IsLessOrEqualThan(size / 2 + 1e-9);
                }
    }

    [Theory]
    [InlineData(30, 5, 7, 15)]
    [InlineData(20, 3.5, 3.5, 10)]
    [InlineData(20, 10, 10, 14)]
    public void ASmallThickRing_SharesTheRoom(double size, double thickness, double peakCore, double peakOuter)
    {
        // 30/5: room 15, exposed 4 → 8, so the core grows only to 7 (not 15). 20/3.5: no room for
        // the core to grow; the light band takes 4 → 6.5. 20/10 already overflows at rest (14 > 10)
        // and keeps its rest widths.
        var rest = CursorRingStacks.For(CursorRingStyle.AchromaticReticle, size, thickness, 1.0, "#112233", "#000000", "#FFFFFF");

        var peak = FiredFlash.PeakStack(rest, size);

        Check.That(peak.Core.Thickness).IsCloseTo(peakCore, 1e-9);
        Check.That(peak.Outer.Thickness).IsCloseTo(peakOuter, 1e-9);
    }
}
