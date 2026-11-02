namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class ControlFinderTests
{
    // UIA AutomationElement.FromPoint requires an interactive desktop session.
    // On non-interactive CI agents it can hang or behave unpredictably, so skip
    // visibly there via Assert.SkipUnless.
    private static bool DesktopAvailable => Environment.UserInteractive;

    [Fact]
    public void FindTarget_NoWindow_ReturnsNull()
    {
        Assert.SkipUnless(DesktopAvailable, "UIA AutomationElement.FromPoint requires an interactive desktop session.");

        var finder = new ControlFinder(NullLogger<ControlFinder>.Instance, new AutoSelectionSettings());
        var result = finder.FindTarget(new PhysicalPoint(-10000, -10000));
        Check.That(result).IsNull();
    }

    [Fact]
    public void FindTarget_NearTaskbar_DoesNotThrow()
    {
        Assert.SkipUnless(DesktopAvailable, "UIA AutomationElement.FromPoint requires an interactive desktop session.");

        var finder = new ControlFinder(NullLogger<ControlFinder>.Instance, new AutoSelectionSettings());
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var result = finder.FindTarget(new PhysicalPoint(screenW / 2, screenH - 5));
        Check.That(result == null || result.CenterPoint.X > 0).IsTrue();
    }
}
