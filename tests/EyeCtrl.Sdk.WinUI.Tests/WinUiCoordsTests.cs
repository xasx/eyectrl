namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.WinUI;
using Xunit;

public class WinUiCoordsTests
{
    [Fact]
    public void Scale1_NoOffset_PassesThrough()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.0, clientOriginX: 0, clientOriginY: 0);
        Assert.Equal(new SdkRect(10, 20, 80, 40), r);
    }

    [Fact]
    public void Scale150_AppliesScaleToPositionAndSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.5, clientOriginX: 0, clientOriginY: 0);
        Assert.Equal(new SdkRect(15, 30, 120, 60), r);
    }

    [Fact]
    public void ClientOrigin_OffsetsPositionOnly_NotSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 2.0, clientOriginX: 1000, clientOriginY: 500);
        Assert.Equal(new SdkRect(1000 + 20, 500 + 40, 160, 80), r);
    }
}
