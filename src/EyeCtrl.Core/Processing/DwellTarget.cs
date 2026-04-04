namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Represents a target area for dwell-based gaze interactions.
/// All coordinates are in physical screen pixels.
/// </summary>
public class DwellTarget
{
    public string Id { get; }
    /// <summary>Center position in physical screen pixels.</summary>
    public PhysicalPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }
    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Tests if a point (in physical screen pixels) is within this target's radius.
    /// </summary>
    public bool HitTest(PhysicalPoint point)
    {
        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
