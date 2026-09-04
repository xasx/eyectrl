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
/// <see cref="StrongestVisibleEdge"/>). Asserted at full opacity only: <c>CircleOpacity</c>
/// is not modelled here.
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
            foreach (var preset in DwellColorPicker.PresetsFor(theme))
            {
                var stack = CursorRingStacks.For(
                    style, 250, 3, 1.0, preset.Hex, Hex(theme.StrokeHalo), Hex(theme.TextPrimary));

                var outer = Parse(stack.Outer.Hex);
                var core = Parse(stack.Core.Hex);

                foreach (var b in Backdrops)
                {
                    var edge = StrongestVisibleEdge(core, outer, Parse(b));
                    Check.WithCustomMessage(
                            $"{theme.Name}/{style}: preset '{preset.Name}' ({preset.Hex}) reaches "
                            + $"only {edge:F2}:1 over {b}; §4.1.3 requires >= {EdgeFloor}.")
                        .That(edge).IsStrictlyGreaterThan(EdgeFloor);
                }
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
}
