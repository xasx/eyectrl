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
    /// </summary>
    private static double StrongestVisibleEdge(Color mark, Color halo, Color backdrop) =>
        Math.Max(Contrast(mark, halo),
            Math.Max(Contrast(mark, backdrop), Contrast(halo, backdrop)));

    [Fact]
    public void AccentWithHalo_ClearsTheFloorOnEveryBackdrop()
    {
        foreach (var b in Backdrops)
        {
            var edge = StrongestVisibleEdge(Tokens.Accent, Tokens.StrokeHalo, Parse(b));
            Check.WithCustomMessage(
                    $"Accent+StrokeHalo reaches only {edge:F2}:1 over {b}; "
                    + $"§4.1.3 requires >= {EdgeFloor}.")
                .That(edge).IsStrictlyGreaterThan(EdgeFloor);
        }
    }

    [Fact]
    public void AccentAlone_FailsOnAtLeastOneBackdrop()
    {
        // The reason the halo exists. If this ever passes, the guarantee has
        // become unnecessary and §4.1.3 should be revisited.
        var worst = double.MaxValue;
        foreach (var b in Backdrops)
            worst = Math.Min(worst, Contrast(Tokens.Accent, Parse(b)));

        Check.WithCustomMessage(
                $"Accent alone manages {worst:F2}:1 at worst — if that is above "
                + $"{EdgeFloor} the halo is redundant.")
            .That(worst).IsStrictlyLessThan(EdgeFloor);
    }

    [Fact]
    public void CueBadgeColour_NeedsTheHaloToo()
    {
        // The cue badge is AccentWarning on a filled shape (spec §4.1.3 case 2).
        var worstAlone = double.MaxValue;
        foreach (var b in Backdrops)
            worstAlone = Math.Min(worstAlone, Contrast(Tokens.AccentWarning, Parse(b)));
        Check.That(worstAlone).IsStrictlyLessThan(EdgeFloor);

        foreach (var b in Backdrops)
            Check.That(StrongestVisibleEdge(Tokens.AccentWarning, Tokens.StrokeHalo, Parse(b)))
                 .IsStrictlyGreaterThan(EdgeFloor);
    }

    [Fact]
    public void EveryColourPickerPreset_KeepsTheGuarantee()
    {
        // CursorSettings.CircleColor is user-selectable, so the guarantee must
        // survive any preset — not just the default.
        foreach (var preset in EyeCtrl.Controls.DwellColorPicker.DefaultPresets)
        {
            var mark = Parse(preset.Hex);
            foreach (var b in Backdrops)
                Check.WithCustomMessage(
                        $"Preset '{preset.Name}' ({preset.Hex}) breaks the halo "
                        + $"guarantee over {b}.")
                    .That(StrongestVisibleEdge(mark, Tokens.StrokeHalo, Parse(b)))
                    .IsStrictlyGreaterThan(EdgeFloor);
        }
    }
}
