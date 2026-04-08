namespace EyeCtrl.Core.Models;

using System.Globalization;
using EyeCtrl.Core.Services;

/// <summary>
/// Represents a point in WPF Device-Independent Units (DIPs) at the 96 DPI baseline.
/// Logical coordinates are DPI-agnostic and must be converted to <see cref="PhysicalPoint"/>
/// for actual screen pixel operations.
///
/// This type provides compile-time safety to prevent mixing coordinate spaces and
/// causing off-by-DPI bugs (e.g., click targeting errors on high-DPI displays).
///
/// Conversion: Use <see cref="ToPhysical"/> to convert to physical screen pixels.
/// </summary>
/// <param name="X">Horizontal coordinate in DIPs (Device-Independent Pixels).</param>
/// <param name="Y">Vertical coordinate in DIPs (Device-Independent Pixels).</param>
public readonly record struct LogicalPoint(double X, double Y)
{
    /// <summary>
    /// Converts this logical point (DIPs) to physical screen pixels.
    /// Uses <see cref="DpiHelper"/> to apply the current DPI scale factor.
    /// </summary>
    /// <returns>A <see cref="PhysicalPoint"/> representing the same location in physical screen pixels.</returns>
    public PhysicalPoint ToPhysical()
    {
        return new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(X),
            DpiHelper.LogicalToPhysicalY(Y)
        );
    }

    /// <summary>
    /// Adds two logical points component-wise.
    /// </summary>
    /// <param name="left">First point.</param>
    /// <param name="right">Second point.</param>
    /// <returns>A new point with coordinates (left.X + right.X, left.Y + right.Y).</returns>
    public static LogicalPoint operator +(LogicalPoint left, LogicalPoint right)
    {
        return new LogicalPoint(left.X + right.X, left.Y + right.Y);
    }

    /// <summary>
    /// Subtracts two logical points component-wise.
    /// </summary>
    /// <param name="left">First point.</param>
    /// <param name="right">Second point.</param>
    /// <returns>A new point with coordinates (left.X - right.X, left.Y - right.Y).</returns>
    public static LogicalPoint operator -(LogicalPoint left, LogicalPoint right)
    {
        return new LogicalPoint(left.X - right.X, left.Y - right.Y);
    }

    /// <summary>
    /// Returns a string representation of this logical point for debugging.
    /// </summary>
    /// <returns>A string in the format "LogicalPoint(X, Y)".</returns>
    public override string ToString()
    {
        return $"LogicalPoint({X.ToString("0.00", CultureInfo.InvariantCulture)}, {Y.ToString("0.00", CultureInfo.InvariantCulture)})";
    }
}
