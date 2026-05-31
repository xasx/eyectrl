namespace EyeCtrl.Core.Models;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)
{
    /// <summary>Horizontal center of the rectangle.</summary>
    public double CenterX => X + Width / 2;
    /// <summary>Vertical center of the rectangle.</summary>
    public double CenterY => Y + Height / 2;
    /// <summary>Center point of the rectangle.</summary>
    public PhysicalPoint Center => new(CenterX, CenterY);
}
