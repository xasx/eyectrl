namespace EyeCtrl.Contracts;

/// <summary>
/// A rectangle in physical screen pixels. Wire format between SDK heads and the
/// EyeCtrl dwell service. Intentionally separate from EyeCtrl.Core.Models.PhysicalRect
/// so the SDK never references Core.
/// </summary>
/// <param name="X">Left edge in physical screen pixels.</param>
/// <param name="Y">Top edge in physical screen pixels.</param>
/// <param name="Width">Width in physical screen pixels.</param>
/// <param name="Height">Height in physical screen pixels.</param>
public readonly record struct SdkRect(double X, double Y, double Width, double Height)
{
    /// <summary>The horizontal center (X + Width / 2).</summary>
    public double CenterX => X + Width / 2;

    /// <summary>The vertical center (Y + Height / 2).</summary>
    public double CenterY => Y + Height / 2;
}
