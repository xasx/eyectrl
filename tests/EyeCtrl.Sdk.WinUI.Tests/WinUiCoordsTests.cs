namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.WinUI;

public class WinUiCoordsTests
{
    [Fact]
    public void Scale1_NoOffset_PassesThrough()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.0, clientOriginX: 0, clientOriginY: 0);
        Check.That(r).IsEqualTo(new SdkRect(10, 20, 80, 40));
    }

    [Fact]
    public void Scale150_AppliesScaleToPositionAndSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.5, clientOriginX: 0, clientOriginY: 0);
        Check.That(r).IsEqualTo(new SdkRect(15, 30, 120, 60));
    }

    [Fact]
    public void FractionalScale_PreservesSubPixelPrecision()
    {
        var r = WinUiCoords.ToScreenRect(11, 21, 81, 41, scale: 1.25, clientOriginX: 100, clientOriginY: 200);
        Check.That(r).IsEqualTo(new SdkRect(113.75, 226.25, 101.25, 51.25));
    }

    [Fact]
    public void ClientOrigin_OffsetsPositionOnly_NotSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 2.0, clientOriginX: 1000, clientOriginY: 500);
        Check.That(r).IsEqualTo(new SdkRect(1000 + 20, 500 + 40, 160, 80));
    }
}
