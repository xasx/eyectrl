namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellTarget
{
    public string Id { get; }
    public ScreenPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }
    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    public DwellTarget(string id, ScreenPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * 1000L;
    }

    public bool HitTest(ScreenPoint point)
    {
        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
