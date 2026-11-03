namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

/// <summary>
/// C10's blocked ring look. The owner's decision (03.11): the bands are DASHED, never dimmed —
/// C1's lesson is that the ring must not get harder to see — and the cue lives in the window's
/// bands, never in <see cref="System.Windows.Window.Opacity"/> (the pin/quiet channel), never in a
/// dimmed brush alpha, and never in view-model state. The window is built headlessly and never
/// shown.
///
/// <para>
/// Ring review r01 F1/F2: the dashes must be PAIRED — both solid bands break at the same angular
/// length — and BiTonal's interleave must survive, or the mark band paints over the halo and the
/// ring loses one colour for the whole block.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class CursorRingBlockedLookTests
{
    private const double Size = 200;

    [StaFact]
    public void SetBlocked_DashesTheBandsInPairs_WithoutChangingStrengthColourOrWindowOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var settings = new CursorSettings
        {
            RingStyle = CursorRingStyle.AchromaticReticle,
            CircleSize = Size,
            CircleThickness = 1.5,
            CircleOpacity = 1.0,
        };
        var window = new CursorCircleWindow();
        window.UpdateAppearance(settings);

        var rest = RestStack(settings);
        var expected = CursorRingStacks.Blocked(rest, Size);

        var windowOpacity = window.Opacity;
        var haloOpacity = window.CircleHalo.Opacity;
        var coreOpacity = window.Circle.Opacity;
        var haloThickness = window.CircleHalo.StrokeThickness;
        var coreThickness = window.Circle.StrokeThickness;
        var restAlpha = ((SolidColorBrush)window.Circle.Stroke).Color.A;

        window.SetBlocked(true);

        Check.That(window.IsBlockedLookOn).IsTrue();

        // The window paints exactly the Core helper's geometry, converted to StrokeThickness units.
        CheckBand(window.CircleHalo, expected.Outer);
        CheckBand(window.Circle, expected.Core);

        Check.WithCustomMessage("the two solid bands must break at the SAME angle (ring r01 F2).")
            .That(AngularDash(expected.Outer)).IsCloseTo(AngularDash(expected.Core), 1e-9);
        Check.WithCustomMessage("a solid blocked ring must start both bands at angle 0.")
            .That(expected.Outer.DashOffset).IsEqualTo(0.0);
        Check.WithCustomMessage("a solid blocked ring must start both bands at angle 0.")
            .That(expected.Core.DashOffset).IsEqualTo(0.0);
        Check.WithCustomMessage("the halo band's drawn dash must start at angle 0 (K7).")
            .That(window.CircleHalo.StrokeDashOffset).IsEqualTo(0.0);
        Check.WithCustomMessage("the mark band's drawn dash must start at angle 0 (K7).")
            .That(window.Circle.StrokeDashOffset).IsEqualTo(0.0);

        Check.WithCustomMessage("the blocked look must not dim the owner's pointer (C1 lesson).")
            .That(window.Circle.Opacity).IsEqualTo(coreOpacity);
        Check.WithCustomMessage("the blocked look must not dim the owner's pointer (C1 lesson).")
            .That(window.CircleHalo.Opacity).IsEqualTo(haloOpacity);
        Check.WithCustomMessage("the blocked look must not thin the bands.")
            .That(window.Circle.StrokeThickness).IsEqualTo(coreThickness);
        Check.WithCustomMessage("the blocked look must not thin the bands.")
            .That(window.CircleHalo.StrokeThickness).IsEqualTo(haloThickness);
        Check.WithCustomMessage("dimming the stroke's alpha would void C1's visibility guarantee (K6).")
            .That(((SolidColorBrush)window.Circle.Stroke).Color.A).IsEqualTo(restAlpha);
        Check.WithCustomMessage("the stroke alpha must stay fully opaque at rest and blocked.")
            .That(restAlpha).IsEqualTo((byte)255);
        Check.WithCustomMessage("Window.Opacity is the pin/quiet channel; a block must never write it.")
            .That(window.Opacity).IsEqualTo(windowOpacity);

        window.SetBlocked(false);

        Check.That(window.IsBlockedLookOn).IsFalse();
        Check.That(window.Circle.StrokeDashArray).IsEmpty();
        Check.That(window.CircleHalo.StrokeDashArray).IsEmpty();
        Check.That(window.Circle.Opacity).IsEqualTo(coreOpacity);
        Check.That(window.Opacity).IsEqualTo(windowOpacity);
    }

    [StaFact]
    public void BiTonal_KeepsItsInterleave_AtHalfThePeriod()
    {
        // Ring r01 F1: one shared phase paints the mark band over the halo and leaves one colour.
        // The blocked stack halves the period and offsets the core by exactly one new dash.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var settings = new CursorSettings
        {
            RingStyle = CursorRingStyle.BiTonalSegments,
            CircleSize = Size,
            CircleThickness = 1.5,
            CircleOpacity = 1.0,
        };
        var window = new CursorCircleWindow();
        window.UpdateAppearance(settings);
        var rest = RestStack(settings);

        window.SetBlocked(true);
        var blocked = CursorRingStacks.Blocked(rest, Size);

        Check.That(blocked.Outer.DashPeriod).IsCloseTo(rest.Outer.DashPeriod / 2, 1e-9);
        Check.That(blocked.Core.DashPeriod).IsCloseTo(rest.Core.DashPeriod / 2, 1e-9);
        Check.That(blocked.Outer.DashOffset).IsEqualTo(0.0);
        Check.WithCustomMessage("the core must be offset by exactly ONE new dash, not one old cycle.")
            .That(blocked.Core.DashOffset).IsCloseTo(blocked.Core.DashPeriod, 1e-9);
        Check.WithCustomMessage("the two dash trains are out of phase: the mark is not painted over the halo.")
            .That(blocked.Core.DashOffset).IsNotEqualTo(0.0);

        // The halo's offset stays 0; a mutant that puts it out of phase (K7) fails here.
        Check.That(window.CircleHalo.StrokeDashOffset).IsEqualTo(0.0);
        Check.That(window.Circle.StrokeDashOffset)
            .IsCloseTo(blocked.Core.DashOffset / blocked.Core.Thickness, 1e-9);
        CheckBand(window.CircleHalo, blocked.Outer);
        CheckBand(window.Circle, blocked.Core);
    }

    [StaFact]
    public void ASettingsChangeWhileBlocked_KeepsTheBlockedLook()
    {
        // UpdateAppearance re-applies the whole stack; it must re-apply the block on top, or a
        // settings edit mid-block would silently restore a normal-looking ring.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var window = new CursorCircleWindow();
        window.UpdateAppearance(new CursorSettings());

        window.SetBlocked(true);
        window.UpdateAppearance(new CursorSettings { CircleSize = 180 });

        Check.That(window.IsBlockedLookOn).IsTrue();
        Check.That(window.Circle.StrokeDashArray.Count).IsEqualTo(2);
        Check.That(window.Width).IsEqualTo(180);
    }

    [StaFact]
    public void SettingTheSameStateTwice_WritesNothingTwice()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(1.0, 1.0);
        var window = new CursorCircleWindow();
        window.UpdateAppearance(new CursorSettings());

        window.SetBlocked(true);
        var coreDash = window.Circle.StrokeDashArray[0];
        var haloDash = window.CircleHalo.StrokeDashArray[0];
        var opacity = window.Circle.Opacity;
        window.SetBlocked(true);

        Check.That(window.Circle.StrokeDashArray[0]).IsEqualTo(coreDash);
        Check.That(window.CircleHalo.StrokeDashArray[0]).IsEqualTo(haloDash);
        Check.That(window.Circle.Opacity).IsEqualTo(opacity);
    }

    private static CursorRingStack RestStack(CursorSettings settings) =>
        CursorRingStacks.For(
            settings.RingStyle, Size, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, Hex(Tokens.StrokeHalo), Hex(Tokens.TextPrimary));

    private static void CheckBand(System.Windows.Shapes.Ellipse ellipse, RingBand band)
    {
        var units = band.DashPeriod / band.Thickness;
        Check.That(ellipse.StrokeDashArray).HasSize(2);
        Check.That(ellipse.StrokeDashArray[0]).IsCloseTo(units, 1e-9);
        Check.That(ellipse.StrokeDashArray[1]).IsCloseTo(units, 1e-9);
        Check.That(ellipse.StrokeThickness).IsCloseTo(band.Thickness, 1e-9);
        Check.That(ellipse.Opacity).IsCloseTo(band.Opacity, 1e-9);
    }

    private static double AngularDash(RingBand band) =>
        band.DashPeriod / (Math.PI * (Size - band.Thickness));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
