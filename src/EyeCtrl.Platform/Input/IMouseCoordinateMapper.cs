namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Models;

/// <summary>
/// Maps screen coordinates to normalized Win32 absolute mouse input coordinates (0-65535 range).
/// Extracted from Win32InputSimulator to enable testing without sending actual OS input.
/// </summary>
public interface IMouseCoordinateMapper
{
    /// <summary>
    /// Normalizes screen coordinates to Win32 absolute mouse input space (0-65535).
    /// Validates, clamps, and normalizes coordinates for use with MOUSEEVENTF_ABSOLUTE.
    /// </summary>
    /// <param name="point">Physical point with pixel coordinates</param>
    /// <param name="screenWidth">Screen width in pixels</param>
    /// <param name="screenHeight">Screen height in pixels</param>
    /// <returns>Normalized coordinates in 0-65535 range</returns>
    /// <exception cref="ArgumentException">Thrown when coordinates are not finite (NaN or Infinity)</exception>
    /// <exception cref="InvalidOperationException">Thrown when screen dimensions are invalid</exception>
    (int normalizedX, int normalizedY) NormalizeScreenCoordinate(PhysicalPoint point, int screenWidth, int screenHeight);
}
