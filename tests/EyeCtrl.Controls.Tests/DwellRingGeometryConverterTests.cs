namespace EyeCtrl.Controls.Tests;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// B8b fixup-01 R1: the tile's dwell feedback is a rim arc drawn in the face's 100 DIP box. The
/// B114 fit gate measures the RENDERED arc's radius against the inner rim; this pins the geometry
/// itself — where it starts, which way it sweeps, and that 1 is a closed ring — so a converter that
/// draws a mirrored or half-size arc cannot pass by staying inside the rim.
/// </summary>
public class DwellRingGeometryConverterTests
{
    private static IReadOnlyList<Point> Points(Geometry geometry)
    {
        var points = new List<Point>();
        foreach (var figure in geometry.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute).Figures)
        {
            points.Add(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment polyline:
                        points.AddRange(polyline.Points);
                        break;
                    case LineSegment line:
                        points.Add(line.Point);
                        break;
                }
            }
        }

        return points;
    }

    [Fact]
    public void ZeroProgress_DrawsNothing()
    {
        Check.That(DwellRingGeometryConverter.For(0).GetFlattenedPathGeometry().Figures).IsEmpty();
    }

    [Fact]
    public void FullProgress_IsAClosedRingAtTheRingRadius()
    {
        var ring = DwellRingGeometryConverter.For(1);
        var ellipse = ring as EllipseGeometry;
        Check.WithCustomMessage("full progress must be a closed ring, not a degenerate arc")
            .That(ellipse).IsNotNull();
        Check.That(ellipse!.Center.X).IsCloseTo(DwellRingGeometryConverter.FaceSize / 2, 1e-9);
        Check.That(ellipse.Center.Y).IsCloseTo(DwellRingGeometryConverter.FaceSize / 2, 1e-9);
        Check.That(ellipse.RadiusX).IsCloseTo(DwellRingGeometryConverter.RingRadius, 1e-9);
        Check.That(ellipse.RadiusY).IsCloseTo(DwellRingGeometryConverter.RingRadius, 1e-9);
    }

    [Fact]
    public void AQuarterProgress_SweepsClockwiseFromTwelveToThreeOClock()
    {
        var points = Points(DwellRingGeometryConverter.For(0.25));
        var centre = new Point(
            DwellRingGeometryConverter.FaceSize / 2, DwellRingGeometryConverter.FaceSize / 2);
        var radius = DwellRingGeometryConverter.RingRadius;

        Check.WithCustomMessage("the arc must start at 12 o'clock")
            .That(points[0].X).IsCloseTo(centre.X, 1e-9);
        Check.That(points[0].Y).IsCloseTo(centre.Y - radius, 1e-9);
        Check.WithCustomMessage("a quarter turn must end at 3 o'clock (clockwise)")
            .That(points[^1].X).IsCloseTo(centre.X + radius, 1e-9);
        Check.That(points[^1].Y).IsCloseTo(centre.Y, 1e-9);

        // Every flattened point stays on the circle — the arc never leaves the ring band.
        foreach (var point in points)
        {
            var dx = point.X - centre.X;
            var dy = point.Y - centre.Y;
            Check.That(Math.Sqrt((dx * dx) + (dy * dy))).IsCloseTo(radius, 0.05);
        }
    }

    [Fact]
    public void MoreThanHalfATurn_TakesTheLongWayThroughSixOClock()
    {
        // 0.75 must be flagged large, or WPF takes the short way round and the feedback runs
        // backwards for the second half of the dwell. A short arc from 12 to 9 o'clock never
        // dips below the centre; the long one passes through 6 o'clock.
        var points = Points(DwellRingGeometryConverter.For(0.75));
        var centre = new Point(
            DwellRingGeometryConverter.FaceSize / 2, DwellRingGeometryConverter.FaceSize / 2);

        Check.WithCustomMessage("a three-quarter dwell must sweep through 6 o'clock")
            .That(points.Any(p => p.Y > centre.Y + (DwellRingGeometryConverter.RingRadius * 0.7))).IsTrue();
    }

    [Fact]
    public void TheRing_StaysInsideTheInnerRimAtBothThemeBorders()
    {
        // The template's stroke is stroked centred on the arc: 4 DIP splits 2 either side. The
        // drawn face's inner rim is r49 at the default 1 DIP border and r48 under Contrast's 2,
        // so the painted outer edge must not pass r48 — B114 measures the rendered result, this
        // records the constant relationship it rests on.
        const double stroke = 4;
        Check.WithCustomMessage("the painted arc's outer edge leaves the inner rim")
            .That(DwellRingGeometryConverter.RingRadius + (stroke / 2) <= 48).IsTrue();
    }
}
