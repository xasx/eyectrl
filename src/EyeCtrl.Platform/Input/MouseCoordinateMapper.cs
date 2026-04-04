namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Models;

/// <summary>
/// Default implementation of IMouseCoordinateMapper.
/// Maps screen coordinates to normalized Win32 absolute mouse input coordinates (0-65535 range).
/// </summary>
public class MouseCoordinateMapper : IMouseCoordinateMapper
{
    /// <inheritdoc/>
    public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(PhysicalPoint point, int screenWidth, int screenHeight)
    {
        // Validate coordinates are finite (not NaN or Infinity)
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Screen coordinates must be finite numbers.", nameof(point));

        // Validate screen dimensions are positive
        if (screenWidth <= 0 || screenHeight <= 0)
            throw new InvalidOperationException("Unable to get valid screen dimensions.");

        // Clamp coordinates to valid screen bounds (0 to screen size)
        // This prevents overflow and ensures cursor stays on screen
        var clampedX = Math.Clamp(point.X, 0, screenWidth);
        var clampedY = Math.Clamp(point.Y, 0, screenHeight);

        // Normalize to 0-65535 range with rounding for better accuracy
        // Using Math.Round instead of truncating cast
        int normalizedX = (int)Math.Round((clampedX * PlatformConstants.Win32AbsoluteCoordinateMax) / screenWidth);
        int normalizedY = (int)Math.Round((clampedY * PlatformConstants.Win32AbsoluteCoordinateMax) / screenHeight);

        // Final clamp to ensure we're in the valid range after rounding
        normalizedX = Math.Clamp(normalizedX, 0, PlatformConstants.Win32AbsoluteCoordinateMax);
        normalizedY = Math.Clamp(normalizedY, 0, PlatformConstants.Win32AbsoluteCoordinateMax);

        return (normalizedX, normalizedY);
    }
}
