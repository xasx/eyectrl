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

    /// <summary>
    /// Whether the given point (in the same coordinate space as the rectangle) lies inside
    /// the rectangle, inclusive of all four edges.
    /// </summary>
    public bool Contains(double x, double y)
        => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>
    /// Whether the given point lies inside the rectangle, inclusive of all four edges.
    /// Delegates to <see cref="Contains(double, double)"/>.
    /// </summary>
    public bool Contains(PhysicalPoint point)
        => Contains(point.X, point.Y);

    /// <summary>
    /// Whether this rectangle and <paramref name="other"/> overlap with a non-zero area.
    /// Strict on purpose: rectangles that merely touch along an edge or a corner do
    /// <em>not</em> intersect — a tangential edge kiss must not count as "over".
    /// </summary>
    public bool IntersectsWith(PhysicalRect other)
        => other.X < X + Width && other.X + other.Width > X
            && other.Y < Y + Height && other.Y + other.Height > Y;

    /// <summary>
    /// Creates a rectangle of the given size whose center is <paramref name="center"/>.
    /// </summary>
    /// <param name="center">The center point of the new rectangle.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    public static PhysicalRect FromCenter(PhysicalPoint center, double width, double height)
        => new(center.X - width / 2, center.Y - height / 2, width, height);
}
