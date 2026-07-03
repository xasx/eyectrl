namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class PhysicalPointDistanceTests
{
    [Fact]
    public void DistanceSquaredTo_ReturnsSquaredEuclideanDistance()
    {
        var a = new PhysicalPoint(1, 2);
        var b = new PhysicalPoint(4, 6);
        Check.That(a.DistanceSquaredTo(b)).IsEqualTo(25.0);
        Check.That(b.DistanceSquaredTo(a)).IsEqualTo(25.0);
    }

    [Fact]
    public void DistanceTo_ReturnsEuclideanDistance()
    {
        var a = new PhysicalPoint(1, 2);
        var b = new PhysicalPoint(4, 6);
        Check.That(a.DistanceTo(b)).IsEqualTo(5.0);
    }

    [Fact]
    public void DistanceTo_Self_IsZero()
    {
        var p = new PhysicalPoint(123.45, -67.8);
        Check.That(p.DistanceTo(p)).IsEqualTo(0.0);
        Check.That(p.DistanceSquaredTo(p)).IsEqualTo(0.0);
    }
}
