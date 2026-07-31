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
/// on a transparent overlay (spec §4.1.3). A dark or mid-tone preset collapses that
/// contrast guarantee, so every preset must clear 7:1 against its theme's
/// StrokeHalo — and the halo differs per theme (#0A0A14 / #0E0A06 / #000000), so
/// the gate runs per theme.
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

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryPreset_ClearsSevenToOneAgainstStrokeHalo(ITheme theme)
    {
        foreach (var (hex, name) in DwellColorPicker.PresetsFor(theme))
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var ratio = Contrast(c, theme.StrokeHalo);
            Check.WithCustomMessage(
                    $"{theme.Name}: preset '{name}' ({hex}) reaches only {ratio:F2}:1 "
                    + "against StrokeHalo; §5 step 9 requires >= 7:1 or the halo "
                    + "guarantee on the cursor ring collapses.")
                .That(ratio).IsStrictlyGreaterThan(7.0);
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FirstPreset_IsTheThemeAccent(ITheme theme)
    {
        // Accent is the default dwell colour; it should be the first choice offered.
        var expected = $"#{theme.Accent.R:X2}{theme.Accent.G:X2}{theme.Accent.B:X2}";
        Check.That(DwellColorPicker.PresetsFor(theme)[0].Hex).IsEqualTo(expected);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Accent_IsNeverTheOutgoingLime(ITheme theme)
    {
        // Replaces a blanket "no preset may be #00FF00" rule. That rule was about
        // the *dwell accent* Lime replaced (§9.2) — but Contrast legitimately uses
        // #00FF00 as AccentSuccess and ToggleOn, validated by all ten gates, so the
        // blanket form would have banned a gated palette value.
        Check.That($"#{theme.Accent.R:X2}{theme.Accent.G:X2}{theme.Accent.B:X2}")
             .IsNotEqualTo("#00FF00");
    }

    [Fact]
    public void MidnightPresets_AreUnchangedByTheDerivation()
    {
        // Regression guard on the *derivation*, not on the palette: ThemeParityTests
        // already pins Midnight's four underlying colours to the validator (the one
        // source of truth). This restates the four hexes deliberately, to catch a
        // change to the derivation itself — a reordered `PresetsFor`, a wrong
        // `Hex()` format, a swapped entry — that leaves every palette value correct
        // but ships a different preset list than the one users had. It is the only
        // test that fixes the *order and membership* of the shipped list.
        Check.That(DwellColorPicker.PresetsFor(MidnightTheme.Instance).Select(p => p.Hex))
             .ContainsExactly("#A5F3FC", "#7EE787", "#E8A33C", "#D6DEF5");
    }

    [Fact]
    public void DefaultPresets_FollowTheActiveTheme()
    {
        Check.That(DwellColorPicker.DefaultPresets)
             .ContainsExactly(DwellColorPicker.PresetsFor(Tokens.Current));
    }
}
