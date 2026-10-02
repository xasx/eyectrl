namespace EyeCtrl.ViewModels.Helpers;

using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
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
    /// <returns>The clamped display point</returns>
    public static PhysicalPoint CalculateDisplayPoint(
        PhysicalPoint fallbackPoint,
        ViewPosition position,
        double circleSize,
        double screenWidthPhysical,
        double screenHeightPhysical)
    {
        // Margin keeps the full button ring on-screen. The radius formula lives ONLY in the Core
        // plan (this was its second site until 2026-09-30 — see RingPlan.EffectiveRingRadiusFor);
        // restating it here would let the clamp and the drawn ring disagree.
        // Clamp uses: effectiveRingRadius + ButtonRadius
        var effectiveRingRadius = RingPlan.EffectiveRingRadiusFor(circleSize);

        // Scale margin to physical pixels for clamping against physical gaze coords
        var marginLogical = effectiveRingRadius + LayoutConstants.ActionMenuButtonRadius;

        // Cap each margin at half its axis. Below that the cap does nothing; at or past it the
        // ring cannot fit the screen at all, and WITHOUT the cap the Math.Clamp calls below get
        // min > max and THROW ArgumentException rather than saturating. That throw reaches the
        // dispatcher via GazeEngineAdapter.OnShowActionMenu, and App.xaml.cs deliberately leaves
        // DispatcherUnhandledException unhandled so the process dies for the supervisor to
        // respawn — i.e. opening the action menu would kill the only input channel the user has.
        //
        // Capping instead collapses that axis's clamp range to a single point, pinning the menu
        // to the screen centre. The ring then overlaps the cursor preview circle, which is
        // degraded but reachable — the correct trade when the alternative is a crash. Reachable
        // at the DEFAULT CircleSize on a 1366x768 panel at 125%; see
        // ActionMenuLayoutHelperTests.AScreenTooSmallForTheRing_StillOpensTheMenu.
        var physicalMarginX = Math.Min(DpiHelper.LogicalToPhysicalX(marginLogical), screenWidthPhysical / 2);
        var physicalMarginY = Math.Min(DpiHelper.LogicalToPhysicalY(marginLogical), screenHeightPhysical / 2);

        // Apply position setting for action menu
        var displayPoint = position == ViewPosition.AtCenter
            ? new PhysicalPoint(screenWidthPhysical / 2, screenHeightPhysical / 2)
            : fallbackPoint;

        var clampedPoint = new PhysicalPoint(
            Math.Clamp(displayPoint.X, physicalMarginX, screenWidthPhysical - physicalMarginX),
            Math.Clamp(displayPoint.Y, physicalMarginY, screenHeightPhysical - physicalMarginY));

        return clampedPoint;
    }
}
