namespace EyeCtrl.Platform.Tests;

using EyeCtrl.Platform;

/// <summary>
/// Tests to verify platform-specific constants are defined.
/// </summary>
public class PlatformConstantsTests
{
    [Fact]
    public void Win32AbsoluteCoordinateMax_Is65535()
    {
        Assert.Equal(65535, PlatformConstants.Win32AbsoluteCoordinateMax);
    }

    [Fact]
    public void MouseWheelDelta_Is120()
    {
        Assert.Equal(120, PlatformConstants.MouseWheelDelta);
    }

    [Fact]
    public void StandardDpiScale_Is96()
    {
        Assert.Equal(96, PlatformConstants.StandardDpiScale);
    }

    [Fact]
    public void AllConstants_HaveExpectedValues()
    {
        // Verify all constants are positive
        Assert.True(PlatformConstants.Win32AbsoluteCoordinateMax > 0);
        Assert.True(PlatformConstants.MouseWheelDelta > 0);
        Assert.True(PlatformConstants.StandardDpiScale > 0);
    }
}
