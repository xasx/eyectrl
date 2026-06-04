namespace EyeCtrl.Contracts;

/// <summary>
/// A rectangle in physical screen pixels. Wire format between SDK heads and the
/// EyeCtrl dwell service. Intentionally separate from EyeCtrl.Core.Models.PhysicalRect
/// so the SDK never references Core.
/// </summary>
public readonly record struct SdkRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
