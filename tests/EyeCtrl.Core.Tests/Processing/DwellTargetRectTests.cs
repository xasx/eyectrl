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
        Assert.True(t.HitTest(new PhysicalPoint(140, 220)));
        Assert.True(t.HitTest(new PhysicalPoint(100, 200)));
        Assert.True(t.HitTest(new PhysicalPoint(180, 240)));
    }

    [Fact]
    public void RectHitTest_Outside_ReturnsFalse()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Assert.False(t.HitTest(new PhysicalPoint(99, 200)));
        Assert.False(t.HitTest(new PhysicalPoint(181, 220)));
        Assert.False(t.HitTest(new PhysicalPoint(140, 241)));
    }

    [Fact]
    public void Center_FromRect_IsGeometricCenter()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Assert.Equal(140, t.Center.X);
        Assert.Equal(220, t.Center.Y);
    }

    [Fact]
    public void Bounds_Settable_UpdatesHitTest()
    {
        var t = new DwellTarget("btn", new PhysicalRect(0, 0, 10, 10), 500);
        Assert.False(t.HitTest(new PhysicalPoint(100, 100)));
        t.Bounds = new PhysicalRect(95, 95, 20, 20);
        Assert.True(t.HitTest(new PhysicalPoint(100, 100)));
    }

    [Fact]
    public void CircleHitTest_Unchanged()
    {
        var t = new DwellTarget("circle", new PhysicalPoint(500, 300), 30, 700);
        Assert.True(t.HitTest(new PhysicalPoint(520, 300)));
        Assert.False(t.HitTest(new PhysicalPoint(540, 300)));
    }
}
