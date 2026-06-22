namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>
/// Contract for finding the nearest interactive UI control near a gaze point.
/// Used by Auto Selection to snap the click target to the closest button, link, or field.
/// </summary>
public interface IControlFinder
{
    /// <summary>
    /// Finds the interactive UI control closest to the given physical screen point.
    /// </summary>
    /// <param name="point">Search origin in physical screen pixels.</param>
    /// <returns>The nearest <see cref="ControlInfo"/> within search range, or <c>null</c> if no control is found.</returns>
    ControlInfo? FindTarget(PhysicalPoint point);
}
