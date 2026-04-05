namespace EyeCtrl.ViewModels.Helpers;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// Helper class for calculating ActionMenu layout and positioning.
/// Encapsulates coordinate calculations and clamping logic that was previously in GazeEngineAdapter.
/// </summary>
public static class ActionMenuLayoutHelper
{
    /// <summary>
    /// Calculates the display point for the action menu, applying position settings and screen bounds clamping.
    /// </summary>
    /// <param name="fallbackPoint">The fallback point to use when position is AtGaze</param>
    /// <param name="position">The configured position setting (AtGaze or AtCenter)</param>
    /// <param name="circleSize">The cursor circle size in logical DIPs</param>
    /// <param name="screenWidthPhysical">Physical screen width in pixels</param>
    /// <param name="screenHeightPhysical">Physical screen height in pixels</param>
    /// <returns>A tuple containing the clamped display point and the physical margin used for clamping</returns>
    public static (PhysicalPoint ClampedPoint, double PhysicalMargin) CalculateDisplayPoint(
        PhysicalPoint fallbackPoint,
        ViewPosition position,
        double circleSize,
        double screenWidthPhysical,
        double screenHeightPhysical)
    {
        // Margin keeps the full button ring on-screen.
        // EffectiveRingRadius = CircleSize/2 + ButtonRadius + ButtonOuterGap
        // Clamp uses: effectiveRingRadius + ButtonRadius
        var effectiveRingRadius = circleSize / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;

        // Scale margin to physical pixels for clamping against physical gaze coords
        var physicalMarginX = DpiHelper.LogicalToPhysicalX(effectiveRingRadius + LayoutConstants.ActionMenuButtonRadius);
        var physicalMarginY = DpiHelper.LogicalToPhysicalY(effectiveRingRadius + LayoutConstants.ActionMenuButtonRadius);

        // Apply position setting for action menu
        var displayPoint = position == ViewPosition.AtCenter
            ? new PhysicalPoint(screenWidthPhysical / 2, screenHeightPhysical / 2)
            : fallbackPoint;

        var clampedPoint = new PhysicalPoint(
            Math.Clamp(displayPoint.X, physicalMarginX, screenWidthPhysical - physicalMarginX),
            Math.Clamp(displayPoint.Y, physicalMarginY, screenHeightPhysical - physicalMarginY));

        return (clampedPoint, physicalMarginX);
    }
}
