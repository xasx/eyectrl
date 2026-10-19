namespace EyeCtrl.App.Tests.Services;

using System;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;

/// <summary>
/// Pins the mapping between a magnified view and real screen coordinates. The capture square is
/// always centred on the requested point (<see cref="CaptureRegionMath.Place"/>); near an edge its
/// off-screen part is transparent, so the effective centre IS the requested point — the old
/// translate-clamp moved it, which drew P off-centre near every edge.
///
/// <para>
/// The inverse map is also pinned across display scales: <see cref="CaptureViewMapping.ToRealPoint"/>
/// must divide a PHYSICAL offset by the magnification, and the window geometry is converted to
/// physical pixels inside the mapping. The DIP-converted form divides by an extra <c>ScaleX</c> —
/// identical at 100 % display scaling, proportionally wrong everywhere else (a regression that
/// shipped once already). The <c>ScaleX</c> theories fail at any non-1.0 scale against that form.
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

    // A capture requested at x=20 with radius 150 spans -130..170. The square is not translated:
    // its origin is negative and its centre stays the requested 20.
    [Fact]
    public void EffectiveCaptureCenter_NearLeftEdge_IsTheRequestedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(20, 400), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(20);
        Check.That(center.Y).IsEqualTo(400);
    }

    [Fact]
    public void EffectiveCaptureCenter_NearTopEdge_IsTheRequestedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(500, 10), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(500);
        Check.That(center.Y).IsEqualTo(10);
    }

    [Fact]
    public void EffectiveCaptureCenter_NearRightEdge_IsTheRequestedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(1900, 400), Radius, ScreenWidth, ScreenHeight);

        Check.That(center.X).IsEqualTo(1900);
        Check.That(center.Y).IsEqualTo(400);
    }

    [Fact]
    public void EffectiveCaptureCenter_AtTheCorner_IsTheRequestedCentre()
    {
        var center = CaptureViewMapping.EffectiveCaptureCenter(new PhysicalPoint(0, 0), Radius, ScreenWidth, ScreenHeight);

        Check.That(center).IsEqualTo(new PhysicalPoint(0, 0));
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
    public void ToRealPoint_GazeAtViewCentre_ReturnsRequestedCentre()
    {
        AtScale(1.0, () =>
        {
            var requested = new PhysicalPoint(20, 400);
            var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
            var viewBounds = new Rect(0, 0, 600, 600); // DIP; centre (300, 300)

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, new PhysicalPoint(300, 300));

            // Looking at the middle of the view now selects what was looked at, even at the edge:
            // the capture is centred on P, so no 130-px shift is left to compensate.
            Check.That(real).IsEqualTo(requested);
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
            var requested = new PhysicalPoint(20, 400);
            var effective = CaptureViewMapping.EffectiveCaptureCenter(requested, Radius, ScreenWidth, ScreenHeight);
            var viewBounds = new Rect(0, 0, 600, 600); // DIP centre (300, 300)

            var real = CaptureViewMapping.ToRealPoint(effective, viewBounds, Magnification, new PhysicalPoint(400, 300));

            // +100 physical px in the 2.0x view is +50 real px from the requested centre (20, 400).
            Check.That(real).IsEqualTo(new PhysicalPoint(70, 400));
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
    /// derived from the centred request, and a DIP-per-content scale from the preview circle over
    /// the capture side. The test uses its own 150-DIP zoom radius and 250-DIP preview circle
    /// (independent of the settings defaults); the selection point
    /// (20, 400) sits near the left edge. The capture is centred on it, so the marker must sit at
    /// the canvas centre at every scale — the old clamped form parked it 83–92 DIP off-centre.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ToViewPoint_ActionMenuComposition_AcrossDisplayScales_CentresTheCommitPoint(double scaleX)
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

            Check.That(marker.X).IsCloseTo(canvasCenter.X, 1e-6);
            Check.That(marker.Y).IsCloseTo(canvasCenter.Y, 1e-6);
        });
    }

    [Fact]
    public void ToViewPoint_EdgeCapture_PlacesEffectiveCentreAtViewCentre()
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
    public void ToViewPoint_ThenToRealPoint_RoundTripsNearTheEdge()
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

    [Theory]
    [InlineData(-100.0, 300.0)]
    [InlineData(4000.0, -200.0)]
    public void ClampToScreen_KeepsCommitPointsOnTheScreen(double x, double y)
    {
        var clamped = CaptureViewMapping.ClampToScreen(new PhysicalPoint(x, y), ScreenWidth, ScreenHeight);

        Check.That(clamped.X).IsGreaterOrEqualThan(0).And.IsLessOrEqualThan(ScreenWidth - 1);
        Check.That(clamped.Y).IsGreaterOrEqualThan(0).And.IsLessOrEqualThan(ScreenHeight - 1);
    }

    [Fact]
    public void ClampToScreen_LeavesAnOnScreenPointUnchanged()
    {
        var point = new PhysicalPoint(400, 300);

        Check.That(CaptureViewMapping.ClampToScreen(point, ScreenWidth, ScreenHeight)).IsEqualTo(point);
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
