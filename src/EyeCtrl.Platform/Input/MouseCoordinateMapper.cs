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
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Screen coordinates must be finite numbers.", nameof(point));

        if (screenWidth <= 0 || screenHeight <= 0)
            throw new InvalidOperationException("Unable to get valid screen dimensions.");

        var normalized = point.ToNormalized(new ScreenBounds(screenWidth, screenHeight));
        return ((int)normalized.X, (int)normalized.Y);
    }
}
