namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// A target area for dwell-based gaze interactions. All coordinates are physical screen pixels.
/// Circular by default; rectangular when <see cref="Bounds"/> is set.
/// </summary>
public class DwellTarget
{
    public string Id { get; }
    /// <summary>Center position in physical screen pixels (used for nearest-target selection).</summary>
    public PhysicalPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }
    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    /// <summary>Optional rectangular bounds. When set, HitTest uses rect containment; null ⇒ circle.</summary>
    public PhysicalRect? Bounds { get; set; }

    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = null;
    }

    public DwellTarget(string id, PhysicalRect bounds, int dwellTimeMs)
    {
        Id = id;
        Center = bounds.Center;
        Radius = 0;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = bounds;
    }

    /// <summary>Rect containment when <see cref="Bounds"/> is set; otherwise circle distance.</summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;

        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
