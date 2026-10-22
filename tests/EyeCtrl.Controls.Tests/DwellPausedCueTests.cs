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
    /// The (arc colour, fill) pairs a dwell arc or bar is really drawn on, for <paramref name="theme"/>.
    /// Every surface A1 covers paints the accent on <c>SurfaceMedium</c> (toolbar, modes menu,
    /// action menu, edge bar, hide button, QuickActions and TaskSwitcher tiles); the DwellToggle
    /// switch bar is the accent on <c>SurfaceLight</c>; Reset's arc is the warning accent, only ever
    /// on <c>SurfaceMedium</c> (ToolbarBand.ArcBrushFor/FillFor). A pair no surface draws is not
    /// gated.
    /// </summary>
    private static IEnumerable<(string Name, Color Arc, Color Fill)> ArcOnFill(ITheme theme)
    {
        yield return ("Accent on SurfaceMedium", theme.Accent, theme.SurfaceMedium);
        yield return ("Accent on SurfaceLight (switch bar)", theme.Accent, theme.SurfaceLight);
        yield return ("AccentWarning on SurfaceMedium (Reset)", theme.AccentWarning, theme.SurfaceMedium);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheDimmedArc_StaysPerceptibleOnEveryFill_InEveryTheme(ITheme theme)
    {
        foreach (var (name, arc, fill) in ArcOnFill(theme))
        {
            var dimmed = Over(arc, theme.StrokeHalo, DwellPausedCue.ProgressOpacity);
            var contrast = Contrast(dimmed, fill);
            Check.WithCustomMessage(
                    $"{theme.Name}: dimmed {name} reaches only {contrast:F2}:1 — "
                    + "a held arc must stay at 3:1 or better or the owner cannot see it is held")
                .That(contrast).IsStrictlyGreaterThan(3.0);
        }
    }

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
