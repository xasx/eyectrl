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
/// fill is invisible on a button whose own fill is near the accent.
///
/// Everything below is a statement about the FILL SET <see cref="FillsFor"/> gates, not about
/// what the live UI happens to draw today. The set is deliberately a superset of that — it
/// records which entries no longer occur, and why they stay — so these measurements do not go
/// stale each time a control is restyled.
///
/// Measured over that set: its first <see cref="DwellColorPicker.PresetsFor"/> entry IS
/// <c>theme.Accent</c>, so bar-fill-vs-button-fill contrast on it is a literal 1.00:1;
/// <c>SettingsWindow</c>'s AccentWarning Reset/Delete buttons and the Supervisor's hardcoded
/// Retry/Save/Launch/Dismiss fills land around 1.0-2.4:1. The halo rescues exactly those cases
/// (stroke-vs-fill up to ~19.6:1 for the same pairs) — but the halo is weak on its own against
/// a dark neutral surface (SurfaceMedium: stroke-vs-fill only ~1.1-1.4:1), where it is the
/// bar's own bright fill that carries visibility (11-15:1) instead.
///
/// So the perceptible quantity is whichever of {fill, stroke} vs the button's own fill is
/// larger — checking only the stroke would fail on the SurfaceMedium entry; checking only the
/// fill would fail on the Accent-swatch entry. Gates that combined quantity at >= 3:1, for
/// every fill listed in <see cref="FillsFor"/>, in every theme.
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

    /// <summary>
    /// Every fill a real <c>DwellButton</c> is constructed with, for <paramref name="theme"/>.
    ///
    /// <c>AccentDanger</c> is deliberately NOT included: <c>SettingsWindow.FillFor</c> maps
    /// BOTH <c>ButtonKind.Reset</c> and <c>ButtonKind.Delete</c> to <c>AccentWarning</c>
    /// (<c>ResetBase</c>/<c>DeleteBase</c>), and <c>SettingsWindow</c>'s one other AccentDanger
    /// consumer — <c>CloseButton</c> — is a plain XAML <c>Border</c> with its own hand-rolled
    /// dwell visual, not a <c>DwellButton</c>. No <c>DwellButton.ProgressBar</c> is drawn on
    /// AccentDanger today, so a preset here would gate a pairing that cannot occur.
    ///
    /// The three literal RGB values are the Supervisor's hardcoded baseColors — read from
    /// FallenBackDialog.xaml.cs (Retry #1E641E, Dismiss #502828) and SupervisorSetupWindow.xaml.cs
    /// (Launch #2D5A89; its own Save button reuses #1E641E) — fixed regardless of theme, unlike
    /// every other entry here, but still exercised against each theme's Accent/StrokeHalo since
    /// the Supervisor links EyeCtrl.Controls and reads the same Tokens.Current.
    /// </summary>
    private static IEnumerable<(string Name, Color Fill)> FillsFor(ITheme theme)
    {
        yield return ("SurfaceMedium (plain buttons and colour-swatch cards)", theme.SurfaceMedium);
        // DwellToggle's switch. Added when the toggle became a composite whose one dwell
        // target is a DwellButton on this fill — a brand-new fill for the bar that nothing
        // gated. Accent-vs-SurfaceLight measures 8.64 / 8.10 / 12.07, so it passes on its
        // fill alone today; the point is that it is now GATED rather than merely fine.
        yield return ("SurfaceLight (the DwellToggle switch)", theme.SurfaceLight);
        yield return ("AccentWarning (Reset/Delete)", theme.AccentWarning);
        // Retained deliberately, though no bar is drawn on a preset any more: a swatch is a
        // SurfaceMedium card (already listed above) carrying the preset colour on an inner chip,
        // and the bar sits on the card. Over-gating costs nothing and these four are the exact
        // colours a future redesign would put back under the bar; deleting them would only have
        // to be rediscovered. They are NOT the colour-swatch coverage that matters, however —
        // ColorPickerPresetTests gates every preset at 7:1 against StrokeHalo for the cursor
        // ring, and that one is load-bearing.
        foreach (var (hex, name) in DwellColorPicker.PresetsFor(theme))
            yield return (
                $"swatch preset '{name}' (no bar drawn on it today — see above)",
                (Color)ColorConverter.ConvertFromString(hex)!);

        yield return ("Supervisor Retry/Save (#1E641E)", Color.FromRgb(0x1E, 0x64, 0x1E));
        yield return ("Supervisor Dismiss (#502828)", Color.FromRgb(0x50, 0x28, 0x28));
        yield return ("Supervisor Launch (#2D5A89)", Color.FromRgb(0x2D, 0x5A, 0x89));
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
