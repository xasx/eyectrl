namespace EyeCtrl.ViewModels.Helpers;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;

/// <summary>
/// Helper class for calculating ManualSelection overlay layout and positioning.
/// Encapsulates coordinate calculations that were previously in GazeEngineAdapter.
/// </summary>
public static class ManualSelectionLayoutHelper
{
    /// <summary>
    /// Calculates the display point for the ManualSelection overlay, applying position settings.
    /// </summary>
    /// <param name="fallbackPoint">The fallback point to use when position is AtGaze</param>
    /// <param name="position">The configured position setting (AtGaze or AtCenter)</param>
    /// <param name="screenWidthPhysical">Physical screen width in pixels</param>
    /// <param name="screenHeightPhysical">Physical screen height in pixels</param>
    /// <returns>The display point for the ManualSelection overlay in physical coordinates</returns>
    public static PhysicalPoint CalculateDisplayPoint(
        PhysicalPoint fallbackPoint,
        ViewPosition position,
        double screenWidthPhysical,
        double screenHeightPhysical)
    {
        // Apply position setting for ManualSelection view
        return position == ViewPosition.AtCenter
            ? new PhysicalPoint(screenWidthPhysical / 2, screenHeightPhysical / 2)
            : fallbackPoint;
    }
}
