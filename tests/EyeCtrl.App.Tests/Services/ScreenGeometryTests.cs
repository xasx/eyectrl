namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Services;

[Collection("DpiHelper Tests (App)")]
public class ScreenGeometryTests
{
    public ScreenGeometryTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void RightEdgeAnchor_AccountsForRadiusAndMargin()
    {
        // 1920 wide, inset 50, radius 50, default margin 10 → button right edge flush at screen edge.
        var anchor = ScreenGeometry.RightEdgeAnchor(
            logicalInset: 50.0, logicalRadius: 50.0,
            logicalWindowMargin: 10,
            logicalScreenWidth: 1920.0, logicalScreenHeight: 1080.0);

        // anchor.X = 1920 - (50 + 50 + 10) = 1810
        Check.That(anchor.X).IsCloseTo(1810.0, 1e-9);
        // anchor.Y = 1080 / 2 = 540
        Check.That(anchor.Y).IsCloseTo(540.0, 1e-9);
    }

    [Fact]
    public void RightEdgeAnchor_AppliesDpiScale()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = ScreenGeometry.RightEdgeAnchor(
                logicalInset: 50.0, logicalRadius: 50.0,
                logicalWindowMargin: 10,
                logicalScreenWidth: 1920.0, logicalScreenHeight: 1080.0);

            // anchor.X = (1920 - 110) * 2 = 1810 * 2 = 3620
            Check.That(anchor.X).IsCloseTo(3620.0, 1e-9);
            Check.That(anchor.Y).IsCloseTo(1080.0, 1e-9);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void RightEdgeAnchor_HonorsCustomWindowMargin()
    {
        var anchor = ScreenGeometry.RightEdgeAnchor(
            logicalInset: 30.0, logicalRadius: 20.0, logicalWindowMargin: 5.0,
            logicalScreenWidth: 1000.0, logicalScreenHeight: 800.0);

        // 1000 - (30 + 20 + 5) = 945
        Check.That(anchor.X).IsCloseTo(945.0, 1e-9);
    }
}
