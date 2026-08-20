namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// DwellButton's Task-3 progress bar is a <see cref="Tokens.BrushAccent"/> fill outlined in
/// <see cref="Tokens.BrushStrokeHalo"/> (Task 3 code review), not a flat accent fill — a flat
/// fill is invisible on an accent-coloured button. Measured: the first
/// <see cref="DwellColorPicker.PresetsFor"/> swatch IS <c>theme.Accent</c>, so bar-fill-vs-
/// button-fill contrast there is a literal 1.00:1; the AccentWarning/AccentDanger semantic
/// buttons (Reset/Delete/Restart) land around 1.6-2.4:1. The halo rescues exactly those cases
/// (stroke-vs-fill 6.6-19.6:1 for the same pairs) — but the halo is weak on its own against a
/// dark neutral surface (SurfaceMedium: stroke-vs-fill only ~1.1-1.4:1), where it is the bar's
/// own bright fill that carries visibility (11-15:1) instead.
///
/// So the perceptible quantity is whichever of {fill, stroke} vs the button's own fill is
/// larger — checking only the stroke would fail on SurfaceMedium buttons; checking only the
/// fill would fail on the Accent swatch. Gates that combined quantity at >= 3:1, for every fill
/// DwellButton is actually constructed with, in every theme.
/// </summary>
public class DwellButtonProgressBarContrastTests
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

    /// <summary>Every fill a real DwellButton is constructed with, for <paramref name="theme"/>.</summary>
    private static IEnumerable<(string Name, Color Fill)> FillsFor(ITheme theme)
    {
        yield return ("SurfaceMedium (plain buttons)", theme.SurfaceMedium);
        yield return ("AccentWarning (Reset/Restart)", theme.AccentWarning);
        yield return ("AccentDanger (Delete)", theme.AccentDanger);
        foreach (var (hex, name) in DwellColorPicker.PresetsFor(theme))
            yield return ($"swatch '{name}'", (Color)ColorConverter.ConvertFromString(hex)!);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ProgressBar_IsPerceptibleAgainstEveryFillItIsDrawnOn(ITheme theme)
    {
        foreach (var (name, fill) in FillsFor(theme))
        {
            var fillContrast = Contrast(theme.Accent, fill);
            var strokeContrast = Contrast(theme.StrokeHalo, fill);
            var best = Math.Max(fillContrast, strokeContrast);

            Check.WithCustomMessage(
                    $"{theme.Name}: bar vs {name} reaches only {best:F2}:1 "
                    + $"(fill {fillContrast:F2}:1, halo {strokeContrast:F2}:1) — the bar must "
                    + "clear 3:1 by its own fill or its halo, or it is not perceptible.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }
}
