namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Services;

[Collection("DpiHelper Tests (App)")]
public class ScreenGeometryLeftEdgeTests
{
    public ScreenGeometryLeftEdgeTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void LeftEdgeAnchor_MirrorsRightEdge_AtTheSameHeight()
    {
        var left = ScreenGeometry.LeftEdgeAnchor(30, 50, 10, 1920, 1200);
        var right = ScreenGeometry.RightEdgeAnchor(30, 50, 10, 1920, 1200);

        Check.That(left.X).IsEqualTo(DpiHelper.LogicalToPhysicalX(90));
        Check.That(left.Y).IsEqualTo(right.Y);
    }
}
