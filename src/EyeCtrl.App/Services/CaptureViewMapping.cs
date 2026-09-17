namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Pure geometry for a magnified view of a captured screen region. The capture rectangle is
/// clamped to the screen (<see cref="CaptureRegionMath"/>), so near an edge the centre of the
/// captured image — the effective centre — is not the centre that was requested. Mapping between
/// the view and real screen coordinates must use the effective centre: assuming the requested
/// centre silently misplaces points by up to the capture radius near any screen edge.
///
/// Units: real-screen coordinates are physical pixels (<see cref="PhysicalPoint"/>); view
/// coordinates are DIPs (System.Windows <see cref="Point"/>/<see cref="Rect"/>), matching the
/// WPF window geometry that defines the view. <see cref="ToRealPoint"/> converts the view
/// geometry to physical pixels internally, because the gaze it maps lives in physical screen
/// space; the scale it applies is the on-screen magnification in physical pixels per content
/// pixel. <see cref="ToViewPoint"/> stays in DIP view units — its consumers draw on a WPF
/// canvas — so its scale is expressed in DIPs per content pixel.
///
/// Kept separate from the windows so it can be unit tested without constructing WPF windows.
/// </summary>
public static class CaptureViewMapping
{
    /// <summary>
    /// The centre of the image a capture of <paramref name="radius"/> around
    /// <paramref name="requestedCenter"/> actually produces, in physical pixels. Mirrors the
    /// clamping <see cref="ScreenCaptureService.CaptureRegion"/> applies, so a caller that needs
    /// the centre before the image arrives (the action-menu preview marker) gets the same value
    /// the capture will.
    /// </summary>
    public static PhysicalPoint EffectiveCaptureCenter(PhysicalPoint requestedCenter, int radius, int screenWidth, int screenHeight)
    {
        var region = CaptureRegionMath.Compute((int)requestedCenter.X, (int)requestedCenter.Y, radius, screenWidth, screenHeight);
        return region is null
            ? requestedCenter
            : CaptureRegionMath.CenterOf(region.Value);
    }

    /// <summary>
    /// The inverse of <see cref="ToViewPoint"/>: maps a point in the magnified view back to real
    /// screen physical pixels.
    /// </summary>
    /// <param name="effectiveCaptureCenter">The centre of the displayed capture, in physical pixels.</param>
    /// <param name="viewBoundsDip">The view's bounds in DIPs (a window's Left/Top/Width/Height).</param>
    /// <param name="magnification">The view's scale: physical screen pixels per content pixel.</param>
    /// <param name="viewPointPhysical">A point in the view, in physical screen pixels (e.g. the gaze).</param>
    public static PhysicalPoint ToRealPoint(
        PhysicalPoint effectiveCaptureCenter,
        Rect viewBoundsDip,
        double magnification,
        PhysicalPoint viewPointPhysical)
    {
        // The offset must be taken in physical pixels: convert the DIP view geometry up to
        // physical, not the physical view point down to DIP. Dividing a DIP-space offset by
        // magnification silently divides by an extra ScaleX — identical to the physical-space
        // result at 100% scaling, proportionally wrong at every other scale.
        var viewCenterPhysical = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(viewBoundsDip.Left + viewBoundsDip.Width / 2),
            DpiHelper.LogicalToPhysicalY(viewBoundsDip.Top + viewBoundsDip.Height / 2));
        return new PhysicalPoint(
            effectiveCaptureCenter.X + (viewPointPhysical.X - viewCenterPhysical.X) / magnification,
            effectiveCaptureCenter.Y + (viewPointPhysical.Y - viewCenterPhysical.Y) / magnification);
    }

    /// <summary>
    /// Maps a point in the captured content (real screen physical pixels) to its position in the
    /// magnified view.
    /// </summary>
    /// <param name="effectiveCaptureCenter">The centre of the displayed capture, in physical pixels.</param>
    /// <param name="viewCenterDip">The view's centre, in DIPs (the canvas the image is drawn on).</param>
    /// <param name="scaleDipPerContentPixel">The view's scale: DIPs per content physical pixel.</param>
    /// <param name="realPoint">A point in the captured content, in physical pixels.</param>
    public static Point ToViewPoint(PhysicalPoint effectiveCaptureCenter, Point viewCenterDip, double scaleDipPerContentPixel, PhysicalPoint realPoint)
        => new(
            viewCenterDip.X + (realPoint.X - effectiveCaptureCenter.X) * scaleDipPerContentPixel,
            viewCenterDip.Y + (realPoint.Y - effectiveCaptureCenter.Y) * scaleDipPerContentPixel);
}
