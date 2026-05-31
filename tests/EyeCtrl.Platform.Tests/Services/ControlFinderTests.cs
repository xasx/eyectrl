namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class ControlFinderTests
{
    // UIA AutomationElement.FromPoint requires an interactive desktop session.
    // On non-interactive CI agents it can hang or behave unpredictably, so skip
    // gracefully there (consistent with the project's environment-dependent test pattern).
    private static bool DesktopAvailable => Environment.UserInteractive;

    [Fact]
    public void FindTarget_NoWindow_ReturnsNull()
    {
        if (!DesktopAvailable) return;

        var finder = new ControlFinder(NullLogger<ControlFinder>.Instance);
        var result = finder.FindTarget(new PhysicalPoint(-10000, -10000));
        Assert.Null(result);
    }

    [Fact]
    public void FindTarget_NearTaskbar_DoesNotThrow()
    {
        if (!DesktopAvailable) return;

        var finder = new ControlFinder(NullLogger<ControlFinder>.Instance);
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var result = finder.FindTarget(new PhysicalPoint(screenW / 2, screenH - 5));
        Assert.True(result == null || result.CenterPoint.X > 0);
    }
}
