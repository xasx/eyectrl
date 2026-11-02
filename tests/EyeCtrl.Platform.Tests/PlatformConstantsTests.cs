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
        Check.That(PlatformConstants.Win32AbsoluteCoordinateMax).IsEqualTo(65535);
    }

    [Fact]
    public void MouseWheelDelta_Is120()
    {
        Check.That(PlatformConstants.MouseWheelDelta).IsEqualTo(120);
    }

    [Fact]
    public void StandardDpiScale_Is96()
    {
        Check.That(PlatformConstants.StandardDpiScale).IsEqualTo(96);
    }
}
