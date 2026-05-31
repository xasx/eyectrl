namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Services;

public class ControlFinderTests
{
    [Fact]
    public void FindTarget_NoWindow_ReturnsNull()
    {
        var finder = new ControlFinder();
        var result = finder.FindTarget(new PhysicalPoint(-10000, -10000));
        Assert.Null(result);
    }

    [Fact]
    public void FindTarget_NearTaskbar_DoesNotThrow()
    {
        var finder = new ControlFinder();
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var result = finder.FindTarget(new PhysicalPoint(screenW / 2, screenH - 5));
        Assert.True(result == null || result.CenterPoint.X > 0);
    }
}
