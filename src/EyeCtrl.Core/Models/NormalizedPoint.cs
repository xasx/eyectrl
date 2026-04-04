namespace EyeCtrl.Core.Models;

/// <summary>
/// Represents a point in Win32 normalized mouse input coordinates (0-65535 range).
/// This coordinate system is used by Windows SendInput API for absolute mouse positioning.
/// The full screen width maps to 0-65535 horizontally, and full screen height maps to 0-65535 vertically.
///
/// This type provides compile-time safety and must be converted from <see cref="PhysicalPoint"/>
/// using screen dimensions for accurate positioning.
///
/// Conversion: Use <see cref="ToPhysical"/> to convert to physical screen pixels.
/// </summary>
/// <param name="X">Horizontal coordinate in Win32 normalized range (0-65535).</param>
/// <param name="Y">Vertical coordinate in Win32 normalized range (0-65535).</param>
public readonly record struct NormalizedPoint(double X, double Y)
{
    /// <summary>
    /// Converts this normalized point (0-65535 range) to physical screen pixels.
    /// Formula: physical = (normalized / 65535) * screenSize
    /// </summary>
    /// <param name="screenBounds">The dimensions of the target screen.</param>
    /// <returns>A <see cref="PhysicalPoint"/> representing the same location in screen pixels.</returns>
    public PhysicalPoint ToPhysical(ScreenBounds screenBounds)
    {
        double physicalX = (X / 65535) * screenBounds.Width;
        double physicalY = (Y / 65535) * screenBounds.Height;
        return new PhysicalPoint(physicalX, physicalY);
    }

    /// <summary>
    /// Returns a string representation of this normalized point for debugging.
    /// </summary>
    /// <returns>A string in the format "NormalizedPoint(X, Y)".</returns>
    public override string ToString()
    {
        return $"NormalizedPoint({X}, {Y})";
    }
}
