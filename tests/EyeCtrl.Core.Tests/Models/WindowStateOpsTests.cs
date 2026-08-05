namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class WindowStateOpsTests
{
    [Fact]
    public void DecideWindowOp_NormalWindow_Maximizes()
    {
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: false, isIconic: false))
             .IsEqualTo(WindowStateOp.Maximize);
    }

    [Fact]
    public void DecideWindowOp_Maximized_Restores()
    {
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: true, isIconic: false))
             .IsEqualTo(WindowStateOp.Restore);
    }

    [Fact]
    public void DecideWindowOp_MinimizedNormal_Restores()
    {
        // Minimized-from-normal: un-minimize.
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: false, isIconic: true))
             .IsEqualTo(WindowStateOp.Restore);
    }

    [Fact]
    public void DecideWindowOp_MinimizedFromMaximized_Restores()
    {
        // IsIconic wins over IsZoomed — un-minimize first (a minimized-from-maximized window
        // keeps WS_MAXIMIZE, so IsZoomed is also true, but the right op is Restore, not a
        // second maximize). This is the truth-table row v1 duplicated incorrectly.
        Check.That(WindowStateOps.DecideWindowOp(isZoomed: true, isIconic: true))
             .IsEqualTo(WindowStateOp.Restore);
    }
}
