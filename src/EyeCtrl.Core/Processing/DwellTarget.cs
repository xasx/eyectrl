namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// A target area for dwell-based gaze interactions. All coordinates are physical screen pixels.
/// Circular by default; rectangular when <see cref="Bounds"/> is set.
/// </summary>
public class DwellTarget
{
    /// <summary>Unique identifier for this dwell target.</summary>
    public string Id { get; }
    /// <summary>Center position in physical screen pixels (used for nearest-target selection).</summary>
    public PhysicalPoint Center { get; set; }
    /// <summary>Hit-test radius in physical pixels (used when <see cref="Bounds"/> is null).</summary>
    public double Radius { get; internal set; }
    /// <summary>Required dwell time in microseconds for activation.</summary>
    public long DwellTimeMicroseconds { get; internal set; }
    /// <summary>Timestamp in microseconds when gaze first entered this target, or null.</summary>
    public long? EnteredTimestamp { get; set; }
    /// <summary>Whether gaze is currently inside this target's hit area.</summary>
    public bool IsGazeInside { get; set; }

    /// <summary>Optional rectangular bounds. When set, HitTest uses rect containment; null ⇒ circle.</summary>
    public PhysicalRect? Bounds { get; set; }

    /// <summary>
    /// Initializes a new circular dwell target.
    /// </summary>
    /// <param name="id">Unique identifier for this target.</param>
    /// <param name="center">Center position in physical screen pixels.</param>
    /// <param name="radius">Hit-test radius in physical pixels.</param>
    /// <param name="dwellTimeMs">Required dwell time in milliseconds.</param>
    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = null;
    }

    /// <summary>
    /// Initializes a new rectangular dwell target.
    /// </summary>
    /// <param name="id">Unique identifier for this target.</param>
    /// <param name="bounds">Rectangular bounds in physical screen pixels.</param>
    /// <param name="dwellTimeMs">Required dwell time in milliseconds.</param>
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

        return point.DistanceSquaredTo(Center) <= Radius * Radius;
    }
}
