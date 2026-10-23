namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// A1 — the "paused, not reset" look of a held dwell. Two promises: it is display-only (the arc
/// keeps its value, thickness, brush and visibility; no colour change), and the dimmed arc stays
/// perceptible against every fill it is drawn on, in all four themes.
/// </summary>
public class DwellPausedCueTests
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

    /// <summary>What a pixel under an arc stroke shows at <paramref name="alpha"/>: the accent over its dark halo.</summary>
    private static Color Over(Color top, Color under, double alpha) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + under.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + under.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + under.B * (1 - alpha)));

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [StaFact]
    public void SetPaused_DimsOnlyTheProgressArc_AndKeepsItsValueThicknessAndBrush()
    {
        var button = DwellArcButton.Create(new Point(0, 0), 50, symbol: null);
        button.UpdateProgress(0.4);
        var brush = button.ProgressPath.Stroke;
        var thickness = button.ProgressPath.StrokeThickness;
        var end = button.Arc.Point;

        button.SetPaused(true);

        Check.That(button.ProgressPath.Opacity).IsEqualTo(DwellPausedCue.ProgressOpacity);
        Check.WithCustomMessage("the held value must still be drawn at its length").That(button.Arc.Point).IsEqualTo(end);
        Check.That(button.ProgressPath.StrokeThickness).IsEqualTo(thickness);
        Check.That(button.ProgressPath.Stroke).IsSameReferenceAs(brush);
        Check.That(button.ProgressPath.Visibility).IsEqualTo(Visibility.Visible);
        Check.WithCustomMessage("the halo is the contrast guarantee; it must not dim")
            .That(button.HaloPath.Opacity).IsEqualTo(1.0);

        button.SetPaused(false);

        Check.That(button.ProgressPath.Opacity).IsEqualTo(1.0);
    }

    // Mutation (A1-v1): SetPaused is a no-op -> the opacity check goes red.
    // Mutation (A1-v2): SetPaused dims the halo too -> the halo check goes red.

    [StaFact]
    public void ThePausedLook_SurvivesAProgressUpdate()
    {
        var button = DwellArcButton.Create(new Point(0, 0), 50, symbol: null);
        button.SetPaused(true);
        button.UpdateProgress(0.5);
        button.UpdateProgress(0.0);
        button.UpdateProgress(0.3);

        Check.WithCustomMessage("progress updates arrive while held and must not clear the dim")
            .That(button.ProgressPath.Opacity).IsEqualTo(DwellPausedCue.ProgressOpacity);
    }

    [Fact]
    public void TheXamlConverter_MapsTheFlagToTheSharedOpacity()
    {
        var converter = DwellPausedOpacityConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Check.That(converter.Convert(true, typeof(double), null, culture)).IsEqualTo(DwellPausedCue.ProgressOpacity);
        Check.That(converter.Convert(false, typeof(double), null, culture)).IsEqualTo(1.0);
        Check.WithCustomMessage("an unset binding must read as the normal look, never dimmed")
            .That(converter.Convert(null, typeof(double), null, culture)).IsEqualTo(1.0);
    }

    // Mutation (A1-v4): Convert returns ProgressOpacity for false -> red.

    [Fact]
    public void TheDim_IsAnActualDim_NotOffAndNotUnchanged()
    {
        Check.That(DwellPausedCue.ProgressOpacity).IsStrictlyLessThan(1.0);
        Check.That(DwellPausedCue.ProgressOpacity).IsStrictlyGreaterThan(0.5);
        Check.That(DwellPausedCue.OpacityFor(false)).IsEqualTo(1.0);
        Check.That(DwellPausedCue.OpacityFor(true)).IsEqualTo(DwellPausedCue.ProgressOpacity);
    }

    /// <summary>
    /// How each A1 surface really draws its progress, for <paramref name="theme"/>: the colour, the
    /// pixel the dimmed element is blended over, and the fill it is read against.
    /// <list type="bullet">
    /// <item>An ARC (toolbar, modes menu, action menu, edge bar) is stroked over its own dark halo
    /// (DwellArcButton's HaloPath), on the SurfaceMedium face; Reset's arc is the warning accent
    /// (ToolbarBand.ArcBrushFor/FillFor).</item>
    /// <item>A BAR (TaskSwitcher and QuickActions tiles, the QuickActions close button) is a
    /// <c>ProgressBar</c> with a transparent background, so it blends straight over SurfaceMedium.</item>
    /// </list>
    /// A pair no A1 surface draws is not gated (review 01 L3).
    /// </summary>
    private static IEnumerable<(string Name, Color Progress, Color Under, Color Fill)> AsDrawn(ITheme theme)
    {
        yield return ("arc: Accent over its halo, on SurfaceMedium", theme.Accent, theme.StrokeHalo, theme.SurfaceMedium);
        yield return ("arc: AccentWarning (Reset) over its halo, on SurfaceMedium", theme.AccentWarning, theme.StrokeHalo, theme.SurfaceMedium);
        yield return ("bar: Accent over SurfaceMedium", theme.Accent, theme.SurfaceMedium, theme.SurfaceMedium);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheDimmedArcAndBar_StayPerceptibleOnTheirFill_InEveryTheme(ITheme theme)
    {
        foreach (var (name, progress, under, fill) in AsDrawn(theme))
        {
            var dimmed = Over(progress, under, DwellPausedCue.ProgressOpacity);
            var contrast = Contrast(dimmed, fill);
            Check.WithCustomMessage(
                    $"{theme.Name}: dimmed {name} reaches only {contrast:F2}:1 — "
                    + "a held cue must stay at 3:1 or better or the owner cannot see it is held")
                .That(contrast).IsStrictlyGreaterThan(3.0);
        }
    }

    /// <summary>
    /// The dim must also be SEEN as a change: the held bar against the live bar. Review 01 measured
    /// 1.76–2.43:1 at 0.7; 1.5:1 is the floor below which "held" stops reading as different.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheDimmedBar_IsVisiblyDifferentFromTheLiveBar(ITheme theme)
    {
        const double MinHeldVsLive = 1.5;
        var live = theme.Accent;
        var held = Over(theme.Accent, theme.SurfaceMedium, DwellPausedCue.ProgressOpacity);

        Check.WithCustomMessage($"{theme.Name}: the held bar is only {Contrast(live, held):F2}:1 from the live one")
            .That(Contrast(live, held)).IsGreaterOrEqualThan(MinHeldVsLive);
    }

    // Mutation (A1-v5): ProgressOpacity 0.95 -> the held bar is indistinguishable, red.

    // Mutation (A1-v3): ProgressOpacity 0.3 -> a theme falls under 3:1 and this goes red.

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheDimmedArc_IsStillDarkerThanTheLiveArc_SoItReadsAsHeld(ITheme theme)
    {
        var live = Over(theme.Accent, theme.StrokeHalo, 1.0);
        var dimmed = Over(theme.Accent, theme.StrokeHalo, DwellPausedCue.ProgressOpacity);

        // Same hue family (it is a blend toward the dark halo, never toward another colour) and
        // strictly lower luminance, when the accent is lighter than the halo.
        if (Luminance(theme.Accent) > Luminance(theme.StrokeHalo))
            Check.That(Luminance(dimmed)).IsStrictlyLessThan(Luminance(live));
        else
            Check.That(Luminance(dimmed)).IsStrictlyGreaterThan(Luminance(live));
    }
}
