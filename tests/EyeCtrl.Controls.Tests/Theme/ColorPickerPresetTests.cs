namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// DwellColorPicker feeds CursorSettings.CircleColor, which colours a haloed ring
/// on a transparent overlay (spec §4.1.3). A dark or mid-tone preset collapses
/// that contrast guarantee, so every preset must clear 7:1 against StrokeHalo.
/// </summary>
public class ColorPickerPresetTests
{
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

    [Fact]
    public void EveryPreset_ClearsSevenToOneAgainstStrokeHalo()
    {
        foreach (var (hex, name) in DwellColorPicker.DefaultPresets)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var ratio = Contrast(c, Tokens.StrokeHalo);
            Check.WithCustomMessage(
                    $"Preset '{name}' ({hex}) reaches only {ratio:F2}:1 against "
                    + "StrokeHalo; §5 step 9 requires >= 7:1 or the halo guarantee "
                    + "on the cursor ring collapses.")
                .That(ratio).IsStrictlyGreaterThan(7.0);
        }
    }

    [Fact]
    public void Presets_DoNotOfferTheOutgoingLime()
    {
        Check.That(DwellColorPicker.DefaultPresets.Select(p => p.Hex))
             .Not.Contains("#00FF00");
    }

    [Fact]
    public void FirstPreset_IsTheAccent()
    {
        // Accent is the default dwell colour; it should be the first choice offered.
        Check.That(DwellColorPicker.DefaultPresets[0].Hex).IsEqualTo("#A5F3FC");
    }
}
