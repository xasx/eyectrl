namespace EyeCtrl.Core.Models;

using EyeCtrl.Core.Services;

/// <summary>
/// Represents a point in physical screen pixels (actual hardware resolution).
/// Physical coordinates depend on the display's DPI and must be converted from/to
/// <see cref="LogicalPoint"/> for WPF coordinate space operations.
///
/// This type provides compile-time safety to prevent mixing coordinate spaces and
/// ensures proper DPI handling for click targeting and screen positioning.
///
/// Conversions:
/// - Use <see cref="ToLogical"/> to convert to WPF DIPs.
/// - Use <see cref="ToNormalized"/> to convert to Win32 mouse input coordinates (0-65535).
/// </summary>
/// <param name="X">Horizontal coordinate in physical screen pixels.</param>
/// <param name="Y">Vertical coordinate in physical screen pixels.</param>
public readonly record struct PhysicalPoint(double X, double Y)
{
    /// <summary>
    /// Converts this physical point (screen pixels) to logical coordinates (DIPs).
    /// Uses <see cref="DpiHelper"/> to apply the inverse of the current DPI scale factor.
    /// </summary>
    /// <returns>A <see cref="LogicalPoint"/> representing the same location in DIPs.</returns>
    public LogicalPoint ToLogical()
    {
        return new LogicalPoint(
            DpiHelper.PhysicalToLogicalX(X),
            DpiHelper.PhysicalToLogicalY(Y)
        );
    }

    /// <summary>
    /// Converts this physical point to Win32 normalized mouse coordinates (0-65535 range).
    /// Win32 mouse input uses a fixed 16-bit range across the screen dimensions.
    /// Formula: normalized = (physical / screenSize) * 65535
    /// Coordinates are clamped to screen bounds before normalization and rounded for precision.
    /// </summary>
    /// <param name="screenBounds">The dimensions of the target screen.</param>
    /// <returns>A <see cref="NormalizedPoint"/> in the Win32 mouse input coordinate space.</returns>
    /// <exception cref="ArgumentException">Thrown when screenBounds.Width or screenBounds.Height is not positive.</exception>
    public NormalizedPoint ToNormalized(ScreenBounds screenBounds)
    {
        // Validate screen dimensions are positive
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            throw new ArgumentException("Screen dimensions must be positive.", nameof(screenBounds));

        // Clamp coordinates to valid screen bounds (0 to screen size)
        double clampedX = Math.Clamp(X, 0, screenBounds.Width);
        double clampedY = Math.Clamp(Y, 0, screenBounds.Height);

        // Normalize to 0-65535 range with rounding for better accuracy
        double normalizedX = Math.Round((clampedX / screenBounds.Width) * 65535);
        double normalizedY = Math.Round((clampedY / screenBounds.Height) * 65535);

        // Final clamp to ensure we're in the valid range after rounding
        normalizedX = Math.Clamp(normalizedX, 0, 65535);
        normalizedY = Math.Clamp(normalizedY, 0, 65535);

        return new NormalizedPoint(normalizedX, normalizedY);
    }

    /// <summary>
    /// Adds two physical points component-wise.
    /// </summary>
    /// <param name="left">First point.</param>
    /// <param name="right">Second point.</param>
    /// <returns>A new point with coordinates (left.X + right.X, left.Y + right.Y).</returns>
    public static PhysicalPoint operator +(PhysicalPoint left, PhysicalPoint right)
    {
        return new PhysicalPoint(left.X + right.X, left.Y + right.Y);
    }

    /// <summary>
    /// Subtracts two physical points component-wise.
    /// </summary>
    /// <param name="left">First point.</param>
    /// <param name="right">Second point.</param>
    /// <returns>A new point with coordinates (left.X - right.X, left.Y - right.Y).</returns>
    public static PhysicalPoint operator -(PhysicalPoint left, PhysicalPoint right)
    {
        return new PhysicalPoint(left.X - right.X, left.Y - right.Y);
    }

    /// <summary>
    /// Returns a string representation of this physical point for debugging.
    /// </summary>
    /// <returns>A string in the format "PhysicalPoint(X, Y)".</returns>
    public override string ToString()
    {
        return $"PhysicalPoint({X:0.00}, {Y:0.00})";
    }
}
