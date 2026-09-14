namespace EyeCtrl.App.Tests.Services;

using System;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;

/// <summary>
/// Pins the mapping between a magnified view and real screen coordinates. The capture rectangle is
/// clamped to the screen (<see cref="CaptureRegionMath"/>), so near an edge the image is NOT centred
/// on the point that was requested. These tests use the default manual-selection geometry (radius
/// 150, magnification 2.0) on a 1920×1080 screen and cover both the clamped band and the interior.
///
/// <para>
/// The inverse map is also pinned across display scales: <see cref="ToRealPoint"/> must divide a
/// PHYSICAL offset by the magnification, and the window geometry is converted to physical pixels
/// inside the mapping. The DIP-converted form divides by an extra <c>ScaleX</c> — identical at
/// 100 % display scaling, proportionally wrong everywhere else (a regression that shipped once
/// already). The <c>ScaleX</c> theories fail at any non-1.0 scale against that form.
/// </para>
///
/// <para>
/// This class calls <c>DpiHelper.Initialize</c> and therefore belongs to the
/// "DpiHelper Tests (App)" non-parallel collection (see <c>DpiHelperWriterIsolationTests</c>);
/// every test restores scale 1.0 in a finally.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
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
        AtScale(1.0, () =>
        {
            var requested = new PhysicalPoint(20, 400);
            var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
            var viewBounds = new Rect(0, 0, 600, 600); // DIP; centre (300, 300)

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, new PhysicalPoint(300, 300));

            Check.That(real).IsEqualTo(new PhysicalPoint(150, 400));
            // The pre-fix assumption — the image is centred on the point that was requested — would
            // have committed the click at 20, 130 px away.
            Check.That(real.X).IsNotEqualTo(requested.X);
        });
    }

    [Fact]
    public void ToRealPoint_OutsideClampedBand_ReturnsRequestedCentre()
    {
        AtScale(1.0, () =>
        {
            var requested = new PhysicalPoint(960, 540);
            var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
            var viewBounds = new Rect(0, 0, 600, 600);

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, new PhysicalPoint(300, 300));

            Check.That(real).IsEqualTo(requested);
        });
    }

    [Fact]
    public void ToRealPoint_ViewOffset_ScalesBackByMagnification()
    {
        AtScale(1.0, () =>
        {
            var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
            var viewBounds = new Rect(0, 0, 600, 600); // DIP centre (300, 300)

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, new PhysicalPoint(400, 300));

            // +100 physical px in the 2.0x view is +50 real px from the image centre (150, 400).
            Check.That(real).IsEqualTo(new PhysicalPoint(200, 400));
        });
    }

    /// <summary>
    /// The units pin, and the reason this defect shipped once: at 100 % display scaling the
    /// DIP-converted offset and the physical offset are the same number, so a test at scale 1.0
    /// cannot tell the correct formula from the one that divides by an extra ScaleX. The view
    /// bounds are fixed in DIPs and the gaze is a FIXED physical point; as the scale rises the
    /// physical window centre moves and the result must move with it. Expected values are the
    /// physical formula hand-computed: centre (400, 380) DIP -> (400·S, 380·S) physical, then
    /// real = (960, 540) + (gaze − centre) / 2.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1110.0, 550.0)]
    [InlineData(1.25, 1060.0, 502.5)]
    [InlineData(1.5, 1010.0, 455.0)]
    [InlineData(2.0, 910.0, 360.0)]
    public void ToRealPoint_AcrossDisplayScales_MapsInPhysicalUnits(double scaleX, double expectedX, double expectedY)
    {
        AtScale(scaleX, () =>
        {
            var effective = new PhysicalPoint(960, 540);
            var viewBounds = new Rect(100, 80, 600, 600); // DIP; centre (400, 380)
            var gaze = new PhysicalPoint(700, 400);       // fixed physical screen point

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, gaze);

            Check.That(real).IsEqualTo(new PhysicalPoint(expectedX, expectedY));
        });
    }

    /// <summary>
    /// The action-menu forward map, composed exactly as the marker branch composes it, across
    /// display scales: capture radius from the DIP zoom radius times ScaleX, the effective centre
    /// derived from the clamped request, and a DIP-per-content scale from the preview circle over
    /// the capture side. Defaults: CircleRadiusPixels 150, CircleSize 250; the selection point
    /// (20, 400) sits inside the left-edge clamp band at every scale. The marker must track the
    /// EFFECTIVE centre — the unclamped form would park it at the canvas centre (200, 200).
    /// </summary>
    [Theory]
    [InlineData(1.0, 91.6666666667)]
    [InlineData(1.25, 88.3689839572)]
    [InlineData(1.5, 86.1111111111)]
    [InlineData(2.0, 83.3333333333)]
    public void ToViewPoint_ActionMenuComposition_AcrossDisplayScales_TracksTheEffectiveCentre(double scaleX, double expectedMarkerX)
    {
        AtScale(scaleX, () =>
        {
            const double zoomRadiusDip = 150;
            const double previewCircleSizeDip = 250;
            var requested = new PhysicalPoint(20, 400);

            var captureRadius = (int)(zoomRadiusDip * DpiHelper.ScaleX);
            var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, captureRadius, ScreenWidth, ScreenHeight);
            var scale = previewCircleSizeDip / (2.0 * captureRadius);
            var canvasCenter = new Point(200, 200);

            var marker = CaptureViewMapping.ToViewPoint(effective, canvasCenter, scale, requested);

            Check.That(marker.X).IsCloseTo(expectedMarkerX, 1e-6);
            Check.That(marker.Y).IsEqualTo(200);
        });
    }

    [Fact]
    public void ToViewPoint_ClampedCapture_PlacesEffectiveCentreAtViewCentre()
    {
        var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
        var viewCenter = new Point(300, 300);

        var view = CaptureViewMapping.ToViewPoint(effective, viewCenter, Magnification, effective);

        Check.That(view).IsEqualTo(viewCenter);
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
        AtScale(1.0, () =>
        {
            var effective = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);
            var viewCenter = new Point(300, 300);
            var real = new PhysicalPoint(200, 400);

            var view = CaptureViewMapping.ToViewPoint(effective, viewCenter, Magnification, real);
            var back = CaptureViewMapping.ToRealPoint(effective, new Rect(0, 0, 600, 600), Magnification, new PhysicalPoint(view.X, view.Y));

            Check.That(back).IsEqualTo(real);
        });
    }

    private static void AtScale(double scaleX, Action test)
    {
        DpiHelper.Initialize(scaleX, scaleX);
        try
        {
            test();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }
}