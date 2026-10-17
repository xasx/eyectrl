namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// G-B1: the action-menu preview (the small circle at the menu's centre) used to show the WHOLE
/// magnifier region shrunk into 200 DIP — 0.59× at the owner's settings — and its "the click lands
/// here" marker was placed from the menu's DISPLAY point (the screen centre under <c>atCenter</c>),
/// not the commit point.
///
/// <para>
/// <see cref="ActionMenuWindow.PreviewGeometry"/> is the pure seam: in the zoom path the capture is
/// centred on the commit point, windowed so the circle shows the zoom's own magnification, and the
/// marker therefore sits at the circle's centre — at edges and corners too (T5). The plain path
/// keeps the old 1.0× behaviour. This class writes <c>DpiHelper</c> for the live-screen-derived
/// tests, so it lives in the exclusive DpiHelper collection and restores 1.0 in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ActionMenuPreviewGeometryTests
{
    private const double PreviewCircleSize = 200;
    private const int ZoomRadius = 170;
    private const int ScreenWidth = 3840;
    private const int ScreenHeight = 2400;

    [Theory]
    [InlineData(1.0, 1.5)]
    [InlineData(1.0, 2.0)]
    [InlineData(1.25, 1.5)]
    [InlineData(1.25, 2.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 1.5)]
    [InlineData(2.0, 2.0)]
    public void ZoomPath_ShowsTheCommitPointAtTheMagnification_WithTheMarkerAtItsCentre(
        double scaleX, double magnification)
    {
        AtScale(scaleX, () =>
        {
            var (center, commitPoint, width, height) = ScreenCentreAndCommitPoint();

            var geometry = ActionMenuWindow.PreviewGeometry(
                center, commitPoint, center, ZoomRadius, PreviewCircleSize, magnification, scaleX, width, height);

            Check.WithCustomMessage("the preview must capture around the COMMIT point, not the menu's display point.")
                .That(geometry.CaptureCenter).IsEqualTo(commitPoint);

            // 200 DIP are rendered onto 2 × CaptureRadiusPx device px at this display scale.
            var displayedPxPerCapturedPx = PreviewCircleSize * scaleX / (2.0 * geometry.CaptureRadiusPx);
            Check.WithCustomMessage(
                    $"the preview shows {displayedPxPerCapturedPx:0.###}× at scale {scaleX}, mag {magnification}; "
                    + "it must match the magnifier's factor.")
                .That(displayedPxPerCapturedPx).IsCloseTo(magnification, magnification * 0.01);

            Check.WithCustomMessage(
                    $"the marker must sit at the preview's centre (the commit point), saw "
                    + $"({geometry.MarkerOffsetDip.X:0.##}, {geometry.MarkerOffsetDip.Y:0.##}) DIP.")
                .That(geometry.MarkerOffsetDip.X).IsCloseTo(0, 1);
            Check.That(geometry.MarkerOffsetDip.Y).IsCloseTo(0, 1);
        });
    }

    /// <summary>
    /// T5: at the screen edges and corners the capture is still centred on the commit point, so the
    /// marker stays at (0,0). The old mirror followed the translate-clamp, which parked the marker
    /// off-centre exactly where the owner needed it most.
    /// </summary>
    [Theory]
    [InlineData(3803, 85)]
    [InlineData(3839, 2399)]
    public void ZoomPath_AtTheScreenEdge_CentresBothTheCaptureAndTheMarker(int commitX, int commitY)
    {
        var center = new PhysicalPoint(1920, 1200);
        var commitPoint = new PhysicalPoint(commitX, commitY);

        var geometry = ActionMenuWindow.PreviewGeometry(
            center, commitPoint, center, ZoomRadius, PreviewCircleSize, 2.0, 2.0, ScreenWidth, ScreenHeight);

        Check.WithCustomMessage("the preview must capture around the commit point at the edge too.")
            .That(geometry.CaptureCenter).IsEqualTo(commitPoint);
        Check.WithCustomMessage(
                $"the marker is ({geometry.MarkerOffsetDip.X:0.##}, {geometry.MarkerOffsetDip.Y:0.##}) DIP; "
                + "the capture is centred on the commit point, so it must be at (0,0).")
            .That(geometry.MarkerOffsetDip.X).IsCloseTo(0, 1);
        Check.That(geometry.MarkerOffsetDip.Y).IsCloseTo(0, 1);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PlainPath_IsOneToOne_AroundTheMenuDisplayPoint(double scaleX)
    {
        AtScale(scaleX, () =>
        {
            var (center, commitPoint, width, height) = ScreenCentreAndCommitPoint();

            // A magnification is passed but the plain path must ignore it, exactly as the old code did.
            var geometry = ActionMenuWindow.PreviewGeometry(
                center, commitPoint, null, null, PreviewCircleSize, 2.0, scaleX, width, height);

            Check.That(geometry.CaptureCenter).IsEqualTo(center);
            Check.WithCustomMessage($"the plain path must stay 1.0×, saw {PreviewCircleSize * scaleX / (2.0 * geometry.CaptureRadiusPx):0.###}×.")
                .That(PreviewCircleSize * scaleX / (2.0 * geometry.CaptureRadiusPx)).IsCloseTo(1.0, 0.01);

            var expectedX = (commitPoint.X - center.X) * geometry.ViewScaleDipPerPx;
            var expectedY = (commitPoint.Y - center.Y) * geometry.ViewScaleDipPerPx;
            Check.That(geometry.MarkerOffsetDip.X).IsCloseTo(expectedX, 0.001);
            Check.That(geometry.MarkerOffsetDip.Y).IsCloseTo(expectedY, 0.001);
        });
    }

    /// <summary>
    /// T5, plain path at an edge: the capture is centred on the menu's display point (unchanged),
    /// so the marker is the offset from that point — clamped to the preview circle as today.
    /// </summary>
    [Fact]
    public void PlainPath_AtTheScreenEdge_PlacesTheMarkerFromTheUnmovedDisplayPoint()
    {
        var center = new PhysicalPoint(1920, 1200);
        var commitPoint = new PhysicalPoint(3803, 85);

        var geometry = ActionMenuWindow.PreviewGeometry(
            center, commitPoint, null, null, PreviewCircleSize, 2.0, 2.0, ScreenWidth, ScreenHeight);

        Check.That(geometry.CaptureCenter).IsEqualTo(center);
        Check.That(geometry.ViewScaleDipPerPx).IsEqualTo(0.5);

        var offsetX = (commitPoint.X - center.X) * geometry.ViewScaleDipPerPx;
        var offsetY = (commitPoint.Y - center.Y) * geometry.ViewScaleDipPerPx;
        var magnitude = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        const double maxOffset = PreviewCircleSize / 2 - 10; // ActionMenuWindow.IndicatorOffsetMargin
        offsetX = offsetX / magnitude * maxOffset;
        offsetY = offsetY / magnitude * maxOffset;

        Check.That(geometry.MarkerOffsetDip.X).IsCloseTo(offsetX, 0.001);
        Check.That(geometry.MarkerOffsetDip.Y).IsCloseTo(offsetY, 0.001);
    }

    /// <summary>The screen centre plus a commit point away from an edge (so no capture clamp).</summary>
    private static (PhysicalPoint Center, PhysicalPoint CommitPoint, int Width, int Height) ScreenCentreAndCommitPoint()
    {
        var width = (int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
        var height = (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight);
        var center = new PhysicalPoint(width / 2, height / 2);
        // Close enough to the centre that the marker stays inside the un-clamped circle
        // (max offset 90 DIP at previewCircleSize 200), far enough to be a real offset.
        return (center, new PhysicalPoint(center.X + 40, center.Y + 30), width, height);
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
