namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Pure geometry for a magnified view of a captured screen region. The capture square is always
/// centred on the requested point (<see cref="CaptureRegionMath.Place"/>); near an edge the
/// off-screen part is transparent and the effective centre is still that point. Mapping between
/// the view and real screen coordinates uses the effective centre, which is also where a commit
/// must be clamped to the screen: the visible disc can extend past an edge, and every selected
/// point has to land on the screen.
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
    /// centred placement <see cref="ScreenCaptureService.CaptureRegion(PhysicalPoint, int)"/>
    /// applies, so a caller that needs the centre before the image arrives (the action-menu preview
    /// marker) gets the same value the capture will: the requested centre whenever the placement
    /// exists.
    /// </summary>
    public static PhysicalPoint EffectiveCaptureCenter(PhysicalPoint requestedCenter, int radius, int screenWidth, int screenHeight)
    {
        var placement = CaptureRegionMath.Place((int)requestedCenter.X, (int)requestedCenter.Y, radius, screenWidth, screenHeight);
        return placement is null
            ? requestedCenter
            : CaptureRegionMath.CenterOf((placement.Value.OriginX, placement.Value.OriginY, placement.Value.Size));
    }

    /// <summary>
    /// Clamps a real-screen point to the screen's physical bounds. The magnified disc can extend
    /// past an edge now that the capture is centred on P, so a view point over the transparent
    /// off-screen part maps onto the nearest screen pixel rather than outside the screen.
    /// </summary>
    public static PhysicalPoint ClampToScreen(PhysicalPoint point, int screenWidth, int screenHeight)
        => new(
            Math.Clamp(point.X, 0, Math.Max(0, screenWidth - 1)),
            Math.Clamp(point.Y, 0, Math.Max(0, screenHeight - 1)));

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
