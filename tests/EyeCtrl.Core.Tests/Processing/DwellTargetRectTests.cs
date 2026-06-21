namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using Xunit;

public class DwellTargetRectTests
{
    [Fact]
    public void RectHitTest_InsideAndEdges_ReturnTrue()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Check.That(t.HitTest(new PhysicalPoint(140, 220))).IsTrue();
        Check.That(t.HitTest(new PhysicalPoint(100, 200))).IsTrue();
        Check.That(t.HitTest(new PhysicalPoint(180, 240))).IsTrue();
    }

    [Fact]
    public void RectHitTest_Outside_ReturnsFalse()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Check.That(t.HitTest(new PhysicalPoint(99, 200))).IsFalse();
        Check.That(t.HitTest(new PhysicalPoint(181, 220))).IsFalse();
        Check.That(t.HitTest(new PhysicalPoint(140, 241))).IsFalse();
    }

    [Fact]
    public void Center_FromRect_IsGeometricCenter()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Check.That(t.Center.X).IsEqualTo(140);
        Check.That(t.Center.Y).IsEqualTo(220);
    }

    [Fact]
    public void Bounds_Settable_UpdatesHitTest()
    {
        var t = new DwellTarget("btn", new PhysicalRect(0, 0, 10, 10), 500);
        Check.That(t.HitTest(new PhysicalPoint(100, 100))).IsFalse();
        t.Bounds = new PhysicalRect(95, 95, 20, 20);
        Check.That(t.HitTest(new PhysicalPoint(100, 100))).IsTrue();
    }

    [Fact]
    public void CircleHitTest_Unchanged()
    {
        var t = new DwellTarget("circle", new PhysicalPoint(500, 300), 30, 700);
        Check.That(t.HitTest(new PhysicalPoint(520, 300))).IsTrue();
        Check.That(t.HitTest(new PhysicalPoint(540, 300))).IsFalse();
    }
}
