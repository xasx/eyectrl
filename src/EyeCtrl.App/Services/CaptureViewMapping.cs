namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Core.Models;

/// <summary>
/// Pure geometry for a magnified view of a captured screen region, in view (DIP) coordinates.
/// The capture rectangle is clamped to the screen (<see cref="CaptureRegionMath"/>), so near an
/// edge the centre of the captured image — the effective centre — is not the centre that was
/// requested. Mapping between the view and real screen coordinates must use the effective centre:
/// assuming the requested centre silently misplaces points by up to the capture radius near any
/// screen edge.
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
    /// Maps a point in the captured content (real screen physical pixels) to its position in the
    /// magnified view, whose centre is <paramref name="viewCenter"/> and whose scale is
    /// <paramref name="scale"/> view DIP per physical pixel.
    /// </summary>
    public static Point ToViewPoint(PhysicalPoint effectiveCaptureCenter, Point viewCenter, double scale, PhysicalPoint realPoint)
        => new(
            viewCenter.X + (realPoint.X - effectiveCaptureCenter.X) * scale,
            viewCenter.Y + (realPoint.Y - effectiveCaptureCenter.Y) * scale);

    /// <summary>
    /// The inverse of <see cref="ToViewPoint"/>: maps a point in the magnified view back to real
    /// screen physical pixels.
    /// </summary>
    public static PhysicalPoint ToRealPoint(PhysicalPoint effectiveCaptureCenter, Point viewCenter, double scale, Point viewPoint)
        => new(
            effectiveCaptureCenter.X + (viewPoint.X - viewCenter.X) / scale,
            effectiveCaptureCenter.Y + (viewPoint.Y - viewCenter.Y) / scale);
}
