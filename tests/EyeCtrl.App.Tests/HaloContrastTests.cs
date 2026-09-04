namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The §4.1.3 contrast guarantee, as an executable claim. A single-colour stroke
/// on a transparent overlay cannot hold contrast against arbitrary desktop
/// content; a two-tone stroke can, because the visible feature becomes the
/// internal accent/halo edge rather than the accent/backdrop edge.
/// </summary>
public class HaloContrastTests
{
    // Keep in sync with CursorRingStyleContrastTests' copy of this helper block (Channel,
    // Luminance, Contrast, Parse, StrongestVisibleEdge, Backdrops, EdgeFloor): a fix to the
    // contrast maths here must be made there too, or the two files pass while asserting
    // different definitions of the same guarantee under the same EdgeFloor name.

    /// <summary>Backdrops a transparent overlay may be drawn over. Mirrors spec §9.5.</summary>
    public static readonly string[] Backdrops =
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
    /// The strongest edge available to the eye: accent-vs-halo (backdrop
    /// independent), accent-vs-backdrop, or halo-vs-backdrop.
    ///
    /// Taking the maximum is deliberate and is exactly what the guarantee claims:
    /// the mark is discernible if ANY of its edges is strong. It does NOT claim the
    /// accent-vs-backdrop edge is strong - that one is often weak, which is the
    /// entire reason the halo exists.
    ///
    /// Consequence: for Accent+StrokeHalo the constant accent-vs-halo term (15.78:1)
    /// dominates on every backdrop, so <see cref="AccentWithHalo_ClearsTheFloorOnEveryBackdrop"/>
    /// cannot fail for a backdrop reason - which is precisely the property the halo
    /// exists to deliver. The loop is retained as documentation of the backdrops the
    /// guarantee must hold over (spec §9.5), not as a backdrop-discrimination test.
    /// </summary>
    private static double StrongestVisibleEdge(Color mark, Color halo, Color backdrop) =>
        Math.Max(Contrast(mark, halo),
            Math.Max(Contrast(mark, backdrop), Contrast(halo, backdrop)));

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AccentWithHalo_ClearsTheFloorOnEveryBackdrop(ITheme theme)
    {
        foreach (var b in Backdrops)
        {
            var edge = StrongestVisibleEdge(theme.Accent, theme.StrokeHalo, Parse(b));
            Check.WithCustomMessage(
                    $"{theme.Name}: Accent+StrokeHalo reaches only {edge:F2}:1 over {b}; "
                    + $"§4.1.3 requires >= {EdgeFloor}.")
                .That(edge).IsStrictlyGreaterThan(EdgeFloor);
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AccentAlone_FailsOnAtLeastOneBackdrop(ITheme theme)
    {
        // The reason the halo exists. If this ever passes, the guarantee has
        // become unnecessary and §4.1.3 should be revisited.
        var worst = double.MaxValue;
        foreach (var b in Backdrops)
            worst = Math.Min(worst, Contrast(theme.Accent, Parse(b)));

        Check.WithCustomMessage(
                $"{theme.Name}: Accent alone manages {worst:F2}:1 at worst — if that is "
                + $"above {EdgeFloor} the halo is redundant.")
            .That(worst).IsStrictlyLessThan(EdgeFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void CueBadgeColour_NeedsTheHaloToo(ITheme theme)
    {
        // The cue badge is AccentWarning on a filled shape (spec §4.1.3 case 2).
        var worstAlone = double.MaxValue;
        foreach (var b in Backdrops)
            worstAlone = Math.Min(worstAlone, Contrast(theme.AccentWarning, Parse(b)));
        Check.WithCustomMessage(
                $"{theme.Name}: AccentWarning alone manages {worstAlone:F2}:1 at worst.")
            .That(worstAlone).IsStrictlyLessThan(EdgeFloor);

        foreach (var b in Backdrops)
            Check.WithCustomMessage($"{theme.Name}: cue badge breaks the guarantee over {b}.")
                .That(StrongestVisibleEdge(theme.AccentWarning, theme.StrokeHalo, Parse(b)))
                .IsStrictlyGreaterThan(EdgeFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryColourPickerPreset_KeepsTheGuarantee(ITheme theme)
    {
        // CursorSettings.CircleColor is user-selectable, so the guarantee must
        // survive any preset — not just the default.
        foreach (var preset in EyeCtrl.Controls.DwellColorPicker.PresetsFor(theme))
        {
            var mark = Parse(preset.Hex);
            foreach (var b in Backdrops)
                Check.WithCustomMessage(
                        $"{theme.Name}: preset '{preset.Name}' ({preset.Hex}) breaks the "
                        + $"halo guarantee over {b}.")
                    .That(StrongestVisibleEdge(mark, theme.StrokeHalo, Parse(b)))
                    .IsStrictlyGreaterThan(EdgeFloor);
        }
    }
}
