namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Services;
using NFluent;
using Xunit;

public class ControlFinderGeometryTests
{
    [Fact]
    public void EnumerateRingPoints_ReturnsEightPoints()
    {
        var origin = new System.Windows.Point(100, 100);
        var points = ControlFinder.EnumerateRingPoints(origin, 16).ToList();

        Check.That(points).HasSize(8);
    }

    [Fact]
    public void EnumerateRingPoints_AreAtRadiusDistance()
    {
        var origin = new System.Windows.Point(100, 100);
        var radius = 16;
        var points = ControlFinder.EnumerateRingPoints(origin, radius).ToList();

        foreach (var p in points)
        {
            var dx = Math.Abs(p.X - origin.X);
            var dy = Math.Abs(p.Y - origin.Y);
            Check.That(dx).IsStrictlyLessThan(radius + 1);
            Check.That(dy).IsStrictlyLessThan(radius + 1);
            // Each point is on at least one edge of the square ring.
            Check.That(dx == radius || dy == radius).IsTrue();
        }
    }

    [Fact]
    public void EnumerateRingPoints_CornersAndEdges()
    {
        var origin = new System.Windows.Point(50, 50);
        var radius = 10;
        var points = ControlFinder.EnumerateRingPoints(origin, radius).ToList();

        // Top-left, top-center, top-right, left, right, bottom-left, bottom-center, bottom-right
        Check.That(points).Contains(new System.Windows.Point(40, 40));
        Check.That(points).Contains(new System.Windows.Point(50, 40));
        Check.That(points).Contains(new System.Windows.Point(60, 40));
        Check.That(points).Contains(new System.Windows.Point(40, 50));
        Check.That(points).Contains(new System.Windows.Point(60, 50));
        Check.That(points).Contains(new System.Windows.Point(40, 60));
        Check.That(points).Contains(new System.Windows.Point(50, 60));
        Check.That(points).Contains(new System.Windows.Point(60, 60));
    }

    [Fact]
    public void DistanceSquaredToRect_PointInsideRect_ReturnsZero()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new System.Windows.Point(50, 50);

        Check.That(ControlFinder.DistanceSquaredToRect(point, rect)).IsEqualTo(0);
    }

    [Fact]
    public void DistanceSquaredToRect_PointOutsideRect_ReturnsPositive()
    {
        var rect = new PhysicalRect(0, 0, 10, 10);
        var point = new System.Windows.Point(20, 5);

        var distance = ControlFinder.DistanceSquaredToRect(point, rect);

        Check.That(distance).IsStrictlyGreaterThan(0);
        Check.That(distance).IsEqualTo(100);
    }

    [Fact]
    public void DistanceSquaredToRect_PointOnEdge_ReturnsZero()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new System.Windows.Point(100, 50);

        Check.That(ControlFinder.DistanceSquaredToRect(point, rect)).IsEqualTo(0);
    }
}
