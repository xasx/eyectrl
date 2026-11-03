namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// C10 fixup-01, ring review r01 F3/F9: the blocked look measured the way C1 measured the fired
/// flash. Every theme x style x backdrop x window look x ring must stay AT LEAST as visible while
/// dashed as it was solid — a dash may remove coverage (that is the cue) but may never remove a
/// colour or a contrast. The model is angular: a band is painted only where its own dash is on, and
/// the mark band is painted over the halo only where BOTH are on (the BiTonal collapse).
///
/// <para>
/// This is what catches a BiTonal blocked look whose two bands share one phase: equal thickness +
/// one phase leaves only the mark colour, and on a light page the ring drops from 3.92:1 to 1.52:1
/// for the whole block (ring r01 F1). K6 (a dimmed brush alpha) is not visible to this pure model;
/// <c>CursorRingBlockedLookTests</c> reads the actual brush alpha.
/// </para>
/// </summary>
public class CursorRingBlockedContrastTests
{
    private static readonly string[] Backdrops =
    [
        "#FFFFFF", "#808080", "#000000",
        "#404040", "#C0C0C0", "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
    ];

    private static readonly double[] WindowOpacities = [0.6, 1.0];

    private static readonly CursorRingStyle[] Styles =
        [CursorRingStyle.AchromaticReticle, CursorRingStyle.WeightedSandwich, CursorRingStyle.BiTonalSegments];

    private static readonly (double Size, double Thickness)[] Rings =
    [
        (200, 1.5), (200, 1.0), (20, 3.5), (30, 5),
    ];

    private const int AngleBins = 720;

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
    public void TheBlockedLook_IsNeverLessVisibleThanTheSolidRestLook(string themeName, CursorRingStyle style)
    {
        var theme = ThemeRegistry.All.Single(t => t.Name == themeName);

        foreach (var (size, thickness) in Rings)
        {
            var rest = RestStack(theme, style, thickness, size);
            var blocked = CursorRingStacks.Blocked(rest, size);
            foreach (var alpha in WindowOpacities)
                foreach (var hex in Backdrops)
                {
                    var backdrop = Parse(hex);
                    var atRest = RingFiredFlashContrastTests.Visibility(rest, backdrop, alpha);
                    var atBlocked = BlockedVisibility(blocked, size, backdrop, alpha);
                    Check.WithCustomMessage(
                            $"{themeName} {style} {size}/{thickness} on {hex} at {alpha}: the blocked ring is "
                            + $"{atBlocked:F2}:1, less visible than {atRest:F2}:1 at rest.")
                        .That(atBlocked).IsGreaterOrEqualThan(atRest - 1e-9);
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
    public void TheOwnersBlockedRing_KeepsItsRestVisibility(string backdropHex, double alpha, double expected)
    {
        // Midnight, AchromaticReticle 200/1.5, opacity 1 (settings.json, 29.10). The figures are
        // the ring review's rest measurements; the blocked look must keep every one of them.
        var midnight = ThemeRegistry.All.Single(t => t.Name == "Midnight");
        var rest = RestStack(midnight, CursorRingStyle.AchromaticReticle, 1.5, 200);
        var blocked = CursorRingStacks.Blocked(rest, 200);
        var backdrop = Parse(backdropHex);

        Check.That(Math.Round(RingFiredFlashContrastTests.Visibility(rest, backdrop, alpha), 2)).IsEqualTo(expected);
        Check.That(Math.Round(BlockedVisibility(blocked, 200, backdrop, alpha), 2)).IsEqualTo(expected);
    }

    [Fact]
    public void TheMatrix_SeesTheBiTonalCollapse_WhenBothBandsShareOnePhase()
    {
        // Teeth for the matrix and for F1: an equal-thickness ring whose two bands are in phase
        // loses the halo colour and drops below rest on Midnight-on-white. This is the shape the
        // helper must never produce; if the matrix cannot see it, it cannot guard the fix.
        var midnight = ThemeRegistry.All.Single(t => t.Name == "Midnight");
        var rest = RestStack(midnight, CursorRingStyle.BiTonalSegments, 1.5, 200);
        var correct = CursorRingStacks.Blocked(rest, 200);
        var collapsed = new CursorRingStack(
            correct.Outer with { DashOffset = 0 },
            correct.Core with { DashOffset = 0 });
        var white = Parse("#FFFFFF");

        var atRest = RingFiredFlashContrastTests.Visibility(rest, white, 0.6);
        var atCorrect = BlockedVisibility(correct, 200, white, 0.6);
        var atCollapsed = BlockedVisibility(collapsed, 200, white, 0.6);

        Check.That(atCorrect).IsGreaterOrEqualThan(atRest - 1e-9);
        Check.WithCustomMessage("the matrix must see the collapsed BiTonal ring drop below its rest visibility.")
            .That(atCollapsed).IsStrictlyLessThan(atRest);
    }

    /// <summary>
    /// Angular visibility: for each angle, a band is painted only where its dash is on; the mark
    /// band covers the halo only where BOTH are on, and the halo is still visible in its exposed
    /// rim when it is wider. The ring's visibility is the best painted-band contrast over the circle
    /// (the same "best exposed band" rule C1 uses for the solid rest look).
    /// </summary>
    private static double BlockedVisibility(CursorRingStack stack, double size, Color backdrop, double windowOpacity)
    {
        var best = 0.0;
        var outer = Parse(stack.Outer.Hex);
        var core = Parse(stack.Core.Hex);

        for (var i = 0; i < AngleBins; i++)
        {
            var angle = 2 * Math.PI * i / AngleBins;
            var outerOn = IsOn(stack.Outer, size, angle);
            var coreOn = IsOn(stack.Core, size, angle);
            if (!outerOn && !coreOn)
                continue;

            var outerComposite = Blend(outer, backdrop, stack.Outer.Opacity);
            var coreUnder = outerOn ? outerComposite : backdrop;
            var coreComposite = Blend(core, coreUnder, stack.Core.Opacity);

            var coreContrast = coreOn
                ? Contrast(Blend(coreComposite, backdrop, windowOpacity), backdrop)
                : 0.0;

            // The mark band is painted second. Where it is on, the halo shows only its exposed rim;
            // where it is off, the halo is fully on the backdrop.
            var outerExposed = outerOn && (stack.Outer.Thickness > stack.Core.Thickness || !coreOn);
            var outerContrast = outerExposed
                ? Contrast(Blend(outerComposite, backdrop, windowOpacity), backdrop)
                : 0.0;

            best = Math.Max(best, Math.Max(coreContrast, outerContrast));
        }

        return best;
    }

    private static bool IsOn(RingBand band, double size, double angle)
    {
        if (band.DashPeriod <= 0)
            return true;

        var radius = Math.Max(0, (size - band.Thickness) / 2);
        var period = 2 * band.DashPeriod;
        var along = (angle * radius + band.DashOffset) % period;
        return along < band.DashPeriod;
    }

    private static CursorRingStack RestStack(ITheme theme, CursorRingStyle style, double thickness, double size) =>
        CursorRingStacks.For(style, size, thickness, 1.0,
            Hex(theme.Accent), Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

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
}
