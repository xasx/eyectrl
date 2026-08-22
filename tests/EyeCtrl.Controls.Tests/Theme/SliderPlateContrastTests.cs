namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The three colour relationships <c>DwellSlider</c>'s value plate rests on, gated at the token
/// level in every registered theme: the value and the two end labels against the plate surface,
/// and the rail's fill against its track.
///
/// This is the theme-sweep half of a pair.
/// <see cref="DwellSliderPlateTests.PlateText_IsLegibleAgainstThePlate"/> is the other: it proves
/// the control actually reaches for these tokens, which this file cannot see, and it reads only
/// the ambient theme, which is the half this file supplies. The pairing is this suite's
/// convention — compare <see cref="ToggleContrastTests.TheTrack_IsPerceptibleAgainstTheSwitchFill"/>
/// with <c>DwellToggleVisualTests.TheTrackIsPerceptibleAgainstTheSwitchFill</c>. (Both names
/// changed when DwellToggle became a composite: its dwell bar moved inside the switch, where
/// it is never drawn on a toggle fill, so the pairing there now gates the TRACK instead.)
///
/// It is not a hypothetical split. The plan this plate was built from specified
/// <c>TextDim</c> for the end labels; measured against <c>SurfaceMedium</c> that is 4.37:1 in
/// Midnight — below the bar — while clearing it in Ember (4.52) and Contrast (11.01). Only a
/// sweep says so.
///
/// Reads each ITheme directly rather than through <c>Tokens.Current</c>: these run in parallel
/// with everything else, and mutating the active theme would race.
/// </summary>
public class SliderPlateContrastTests
{
    /// <summary>
    /// The plate's end labels are <c>FontSizeSubtitle</c> (18 DIP) at normal weight. WCAG's
    /// large-text exemption starts at 24 DIP, or 18.66 DIP bold, so they do not qualify for the
    /// 3:1 relaxation and are held to the full body-text ratio — as is the value itself, which
    /// clears it comfortably anyway at <c>FontSizeDisplay</c>.
    /// </summary>
    private const double TextRatioFloor = 4.5;

    /// <summary>
    /// The rail is a graphical object, not text, so WCAG 1.4.11's 3:1 non-text floor applies.
    /// Below it the filled part is indistinguishable from the empty part and the rail says
    /// nothing.
    /// </summary>
    private const double RailRatioFloor = 3.0;

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

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

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheValue_IsLegibleOnThePlate(ITheme theme)
    {
        var ratio = Contrast(theme.TextPrimary, theme.SurfaceMedium);
        Check.WithCustomMessage(
                $"{theme.Name}: the slider's value (TextPrimary {theme.TextPrimary}) reaches only "
                + $"{ratio:F2}:1 on the plate (SurfaceMedium {theme.SurfaceMedium}); the plate "
                + $"exists to be read at a glance and must clear {TextRatioFloor}:1.")
            .That(ratio).IsGreaterOrEqualThan(TextRatioFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheEndLabels_AreLegibleOnThePlate(ITheme theme)
    {
        var ratio = Contrast(theme.TextSecondary, theme.SurfaceMedium);
        Check.WithCustomMessage(
                $"{theme.Name}: the rail's min/max labels (TextSecondary {theme.TextSecondary}) "
                + $"reach only {ratio:F2}:1 on the plate (SurfaceMedium {theme.SurfaceMedium}). "
                + "At 18 DIP normal weight they are not WCAG large text, so the "
                + $"{TextRatioFloor}:1 body floor applies. TextDim was tried here and is 4.37:1 "
                + "in Midnight — do not go back to it.")
            .That(ratio).IsGreaterOrEqualThan(TextRatioFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheRailFill_IsPerceptibleAgainstItsTrack(ITheme theme)
    {
        var ratio = Contrast(theme.Accent, theme.SurfaceDark);
        Check.WithCustomMessage(
                $"{theme.Name}: the rail's fill (Accent {theme.Accent}) reaches only {ratio:F2}:1 "
                + $"against its track (SurfaceDark {theme.SurfaceDark}) — how far along its range "
                + "the value sits is the one thing the rail is for, and below "
                + $"{RailRatioFloor}:1 it cannot be seen.")
            .That(ratio).IsGreaterOrEqualThan(RailRatioFloor);
    }
}
