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
    public void MoveTo_WritesTheSameDrawnPositionAsUpdatePosition()
    {
        // Ticket 7's glide frames arrive through MoveTo rather than UpdatePosition so instrument 2 can
        // skip them; the drawn position itself must be identical. Mutation: make MoveTo a no-op (or
        // forget the position writes) → the glide's frames stop moving the ring and this reds.
        var window = CreateWindow();
        try
        {
            window.UpdateAppearance(new CursorSettings { CircleSize = 250 });
            var point = new PhysicalPoint(1400, 900);

            window.UpdatePosition(point);
            var expectedLeft = window.Left;
            var expectedTop = window.Top;

            window.UpdatePosition(new PhysicalPoint(0, 0));   // move away
            window.MoveTo(point, glide: true);                // a glide frame brings it back

            Check.That(window.Left).IsEqualTo(expectedLeft);
            Check.That(window.Top).IsEqualTo(expectedTop);

            window.MoveTo(new PhysicalPoint(0, 0), glide: false);
            Check.That(window.Left).IsEqualTo(0 - (window.Width / 2));
        }
        finally
        {
            window.Close();
        }
    }
    [StaFact]
    public void TwoLooks_FreeLookQuiet_PinnedBold_AndUnpinReturnsToQuiet()
    {
        // The two explicit looks. PR #191's hide-while-paused change kept the old paused dim as
        // defensive-only code; this branch's deletion removed it — no production path can show the
        // window while paused. FreeLook quiet, Fixation pinned + bold. Only a pin may bold the
        // ring, and the pin's end must return to quiet — FreeLookQuietOpacity < 1.0 keeps the two
        // distinguishable from the opacity alone.
        var window = CreateWindow();
        try
        {
            // FreeLook: the quiet look is the state the window starts in — no pin notification
            // fires for the default state, so the constructor must already establish it.
            Check.That(window.Opacity).IsEqualTo(CursorCircleWindow.FreeLookQuietOpacity);
            Check.That(CursorCircleWindow.FreeLookQuietOpacity).IsStrictlyLessThan(1.0);

            // Fixation: bold.
            window.SetPinned(true);
            Check.That(window.Opacity).IsEqualTo(1.0);

            // Fixation ends: quiet again.
            window.SetPinned(false);
            Check.That(window.Opacity).IsEqualTo(CursorCircleWindow.FreeLookQuietOpacity);
        }
        finally
        {
            window.Close();
        }
    }
}
