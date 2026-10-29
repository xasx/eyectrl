namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Wave C C1's "clearly visible" claim, quantified for the owner's ring style (AchromaticReticle,
/// core opacity 1). The flash paints the whole stroke in the lighter achromatic token, so per band
/// the step is rest composite → peak composite, each band drawn at the window's look opacity (0.6
/// quiet, 1.0 pinned) over a backdrop. The light band is already that colour, so its step is 1:1;
/// the dark band carries the step, and it must clear WCAG 2.1 SC 1.4.11's non-text floor (3:1) on
/// every backdrop, at both opacities, in every theme.
/// <para>
/// Measured for Midnight (the owner's theme): the dark band steps from relative luminance 0.002–0.151
/// to 0.235–0.831 at 0.6 (4.38:1 on white, the worst case, to 6.12:1), and 0.003 → 0.731 at 1.0
/// (14.65:1 on every backdrop).
/// </para>
/// <para>
/// Same maths as CursorRingStyleContrastTests (WCAG luminance, byte-rounded sRGB alpha-over), which
/// is what a WPF layered window composites.
/// </para>
/// </summary>
public class RingFiredFlashContrastTests
{
    private static readonly string[] Backdrops =
    [
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF",
        "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
    ];

    private const double PerceptibilityFloor = 3.0;

    private static readonly double[] WindowOpacities = [0.6, 1.0];

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (FiredFlash.RelativeLuminance(a), FiredFlash.RelativeLuminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color Blend(Color fg, Color bg, double alpha)
    {
        byte Mix(byte f, byte b) => (byte)Math.Round(f * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    public static TheoryData<string> AllThemes()
    {
        var data = new TheoryData<string>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme.Name);
        return data;
    }

    /// <summary>The step of the band that changes most: what the eye sees flash.</summary>
    private static double WorstCaseStep(ITheme theme)
    {
        // AchromaticReticle: outer band TextPrimary, core band StrokeHalo at opacity 1 on top.
        var outer = theme.TextPrimary;
        var core = theme.StrokeHalo;
        var flash = FiredFlash.RelativeLuminance(theme.TextPrimary) >= FiredFlash.RelativeLuminance(theme.StrokeHalo)
            ? theme.TextPrimary
            : theme.StrokeHalo;

        return WindowOpacities.SelectMany(alpha => Backdrops.Select(hex =>
        {
            var backdrop = Parse(hex);
            var peak = Blend(flash, backdrop, alpha);
            return Math.Max(
                Contrast(Blend(outer, backdrop, alpha), peak),
                Contrast(Blend(core, backdrop, alpha), peak));
        })).Min();
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheStep_ClearsTheNonTextFloor_OnEveryBackdrop_AtBothWindowOpacities(string themeName)
    {
        var theme = ThemeRegistry.All.Single(t => t.Name == themeName);

        var worst = WorstCaseStep(theme);

        Check.WithCustomMessage(
                $"{themeName}: the flash's worst-case step is {worst:F2}:1, under the {PerceptibilityFloor}:1 floor.")
            .That(worst).IsGreaterOrEqualThan(PerceptibilityFloor);
    }

    [Fact]
    public void TheOwnersTheme_WorstCaseStep_IsPinned()
    {
        // Midnight's worst case is the quiet look over white: dark band 0.151 → 0.831.
        var midnight = ThemeRegistry.All.Single(t => t.Name == "Midnight");

        Check.That(Math.Round(WorstCaseStep(midnight), 2)).IsEqualTo(4.38);
    }
}
