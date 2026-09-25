namespace EyeCtrl.App.Tests;

using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Guards <see cref="CursorCircleWindow.UpdateAppearance"/>, which a merge-readiness mutation
/// sweep found was executed by NO test at all: swapping the two <c>Apply</c> calls, dropping the
/// <c>/ band.Thickness</c> unit conversion, or dropping the <c>else</c> that clears a stale dash
/// pattern each destroyed the feature while every suite stayed green.
///
/// Constructs the window but never shows it, same as <see cref="CornerRadiusClampTests"/> and
/// <see cref="OverlayResourceResolutionTests"/> — this suite must never take over the screen of a
/// user whose only input is gaze. <c>CircleHalo</c>'s XAML resolves {StaticResource
/// BrushStrokeHalo}/{StaticResource BrushAccent}, so — unlike <see cref="OverlayFontTests"/> —
/// this needs <see cref="WpfApplicationGate"/> and therefore joins the same
/// <c>[Collection("WpfApplication")]</c> as its other consumers.
/// </summary>
[Collection("WpfApplication")]
public class CursorRingRenderTests
{
    private static CursorCircleWindow CreateWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return new CursorCircleWindow();
    }

    private static Color StrokeColor(System.Windows.Shapes.Shape shape) =>
        ((SolidColorBrush)shape.Stroke).Color;

    [StaFact]
    public void WeightedSandwich_PaintsOuterBandOnHaloAndCoreBandOnCircle()
    {
        // Catches the Apply-call swap: if CircleHalo painted the CORE band (and vice versa),
        // the wide halo would no longer sit under the narrow core and the internal edge — the
        // entire point of the feature — would disappear.
        var settings = new CursorSettings
        {
            RingStyle = CursorRingStyle.WeightedSandwich,
            CircleThickness = 3.0,
            CircleOpacity = 1.0,
            CircleColor = "#112233",
        };
        var window = CreateWindow();
        try
        {
            window.UpdateAppearance(settings);

            var stack = CursorRingStacks.For(
                settings.RingStyle, System.Math.Max(10, settings.CircleSize),
                settings.CircleThickness, settings.CircleOpacity, settings.CircleColor,
                "#000000", "#FFFFFF");

            Check.That(window.CircleHalo.StrokeThickness).IsEqualTo(stack.Outer.Thickness);
            Check.That(window.Circle.StrokeThickness).IsEqualTo(stack.Core.Thickness);

            // WeightedSandwich's outer band is the halo token, the core band the user's mark
            // colour — the two differ, so this also pins which ellipse gets which colour.
            Check.That(StrokeColor(window.CircleHalo))
                .IsNotEqualTo((Color)ColorConverter.ConvertFromString(settings.CircleColor));
            Check.That(StrokeColor(window.Circle))
                .IsEqualTo((Color)ColorConverter.ConvertFromString(settings.CircleColor));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void BiTonalSegments_DashesAreInStrokeThicknessUnitsAndAntiPhase()
    {
        // Catches the dropped "/ band.Thickness" divisions: WPF expresses StrokeDashArray and
        // StrokeDashOffset in multiples of StrokeThickness, so an absolute arc length left
        // undivided renders the dash pattern at the wrong scale.
        var settings = new CursorSettings
        {
            RingStyle = CursorRingStyle.BiTonalSegments,
            CircleSize = 250,
            CircleThickness = 3.0,
            CircleOpacity = 1.0,
        };
        var window = CreateWindow();
        try
        {
            window.UpdateAppearance(settings);

            var stack = CursorRingStacks.For(
                settings.RingStyle, settings.CircleSize, settings.CircleThickness,
                settings.CircleOpacity, settings.CircleColor, "#000000", "#FFFFFF");

            var outerUnits = stack.Outer.DashPeriod / stack.Outer.Thickness;
            var coreUnits = stack.Core.DashPeriod / stack.Core.Thickness;
            var coreOffsetUnits = stack.Core.DashOffset / stack.Core.Thickness;

            Check.That(window.CircleHalo.StrokeDashArray).ContainsExactly(outerUnits, outerUnits);
            Check.That(window.Circle.StrokeDashArray).ContainsExactly(coreUnits, coreUnits);

            Check.That(window.CircleHalo.StrokeDashOffset).IsEqualTo(0.0);
            Check.That(window.Circle.StrokeDashOffset).IsEqualTo(coreOffsetUnits);

            // Anti-phase: one band starts on-dash, the other half a cycle later, which is what
            // interleaves the two into one visible ring instead of overprinting each other.
            Check.WithCustomMessage(
                    "The halo and core dash offsets must differ, or the two bands are in phase "
                    + "and the bi-tonal style is silently voided.")
                .That(window.CircleHalo.StrokeDashOffset)
                .IsNotEqualTo(window.Circle.StrokeDashOffset);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SwitchingFromDashedToSolid_ClearsTheStaleDashPattern()
    {
        // Catches the dropped "else" branch: without it, switching from a dashed style
        // (BiTonalSegments) to a solid one leaves the old StrokeDashArray/Offset painted.
        var window = CreateWindow();
        try
        {
            window.UpdateAppearance(new CursorSettings { RingStyle = CursorRingStyle.BiTonalSegments });
            window.UpdateAppearance(new CursorSettings { RingStyle = CursorRingStyle.WeightedSandwich });

            Check.That(window.CircleHalo.StrokeDashArray).IsEmpty();
            Check.That(window.Circle.StrokeDashArray).IsEmpty();
            Check.That(window.CircleHalo.StrokeDashOffset).IsEqualTo(0.0);
            Check.That(window.Circle.StrokeDashOffset).IsEqualTo(0.0);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SetPausedDim_DimsTheWholeRing_AndRestoresFullOpacity()
    {
        // Paused-trap guard: the ring stays visible while paused, but dimmed — it must read as
        // "tracking alive", not as the normal cursor competing with the resume target.
        var window = CreateWindow();
        try
        {
            Check.That(window.Opacity).IsEqualTo(1.0);

            window.SetPausedDim(true);
            Check.That(window.Opacity).IsEqualTo(CursorCircleWindow.PausedDimOpacity);
            Check.That(window.Opacity).IsStrictlyLessThan(1.0);

            window.SetPausedDim(false);
            Check.That(window.Opacity).IsEqualTo(1.0);
        }
        finally
        {
            window.Close();
        }
    }
}
