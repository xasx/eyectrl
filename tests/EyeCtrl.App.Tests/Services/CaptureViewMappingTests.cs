namespace EyeCtrl.App.Tests.Services;

using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Pins the mapping between a magnified view and real screen coordinates. The capture rectangle is
/// clamped to the screen (<see cref="CaptureRegionMath"/>), so near an edge the image is NOT centred
/// on the point that was requested. These tests use the default manual-selection geometry (radius
/// 150, magnification 2.0) on a 1920×1080 screen and cover both the clamped band and the interior.
/// </summary>
public class CaptureViewMappingTests
{
    private const int ScreenWidth = 1920;
    private const int ScreenHeight = 1080;
    private const int Radius = 150;
    private const double Magnification = 2.0;

    // A capture requested at x=20 with radius 150 spans -130..170, clamped to 0..300 — so the image
    // is centred on 150, not on the requested 20.
    [Fact]
    public void EffectiveCaptureCenter_NearLeftEdge_ReturnsClampedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(150);
        Check.That(center.Y).IsEqualTo(400);
    }

    [Fact]
    public void EffectiveCaptureCenter_NearTopEdge_ClampsYOnly()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(500, 10), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(500);
        Check.That(center.Y).IsEqualTo(150);
    }

    [Fact]
    public void EffectiveCaptureCenter_NearRightEdge_ReturnsClampedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(1900, 400), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(1770);
        Check.That(center.Y).IsEqualTo(400);
    }

    [Fact]
    public void EffectiveCaptureCenter_AwayFromEdges_IsUnchanged()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(960, 540), Radius, ScreenWidth, ScreenHeight);

        Check.That(center).IsEqualTo(new PhysicalPoint(960, 540));
    }

    [Fact]
    public void EffectiveCaptureCenter_InvalidRadius_FallsBackToRequestedCentre()
    {
        var requested = new PhysicalPoint(20, 400);

        var center = CaptureViewMapping.EffectiveCaptureCenter(requested, 0, ScreenWidth, ScreenHeight);

        Check.That(center).IsEqualTo(requested);
    }

    [Fact]
    public void ToRealPoint_GazeAtViewCentre_ReturnsEffectiveCaptureCentre()
    {
        var requested = new PhysicalPoint(20, 400);
        var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
        var windowCenter = new Point(300, 300); // where the magnifier window sits, in DIP

        var real = CaptureViewMapping.ToRealPoint(effective, windowCenter, Magnification, windowCenter);

        Check.That(real).IsEqualTo(new PhysicalPoint(150, 400));
        // The pre-fix assumption — the image is centred on the point that was requested — would
        // have committed the click at 20, 130 px away.
        Check.That(real.X).IsNotEqualTo(requested.X);
    }

    [Fact]
    public void ToRealPoint_OutsideClampedBand_ReturnsRequestedCentre()
    {
        var requested = new PhysicalPoint(960, 540);
        var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
        var windowCenter = new Point(300, 300);

        var real = CaptureViewMapping.ToRealPoint(effective, windowCenter, Magnification, windowCenter);

        Check.That(real).IsEqualTo(requested);
    }

    [Fact]
    public void ToRealPoint_ViewOffset_ScalesBackByMagnification()
    {
        var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
        var windowCenter = new Point(300, 300);

        var real = CaptureViewMapping.ToRealPoint(effective, windowCenter, Magnification, new Point(400, 300));

        // +100 DIP in the 2.0x view is +50 real px from the image centre (150, 400).
        Check.That(real).IsEqualTo(new PhysicalPoint(200, 400));
    }

    [Fact]
    public void ToViewPoint_ClampedCapture_PlacesEffectiveCentreAtViewCentre()
    {
        var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
        var windowCenter = new Point(300, 300);

        var view = CaptureViewMapping.ToViewPoint(effective, windowCenter, Magnification, effective);

        Check.That(view).IsEqualTo(windowCenter);
    }

    [Fact]
    public void ToViewPoint_RealOffset_ScalesByMagnification()
    {
        var effective = new PhysicalPoint(150, 400);
        var viewCenter = new Point(300, 300);

        var view = CaptureViewMapping.ToViewPoint(effective, viewCenter, Magnification, new PhysicalPoint(200, 400));

        Check.That(view).IsEqualTo(new Point(400, 300));
    }

    [Fact]
    public void ToViewPoint_ThenToRealPoint_RoundTripsInClampedBand()
    {
        var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
        var viewCenter = new Point(300, 300);
        var real = new PhysicalPoint(200, 400);

        var view = CaptureViewMapping.ToViewPoint(effective, viewCenter, Magnification, real);
        var back = CaptureViewMapping.ToRealPoint(effective, viewCenter, Magnification, view);

        Check.That(back).IsEqualTo(real);
    }
}
