namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;
using Xunit;

public class OutboundRectTrackerTests
{
    private static readonly SdkRect A = new(0, 0, 10, 10);
    private static readonly SdkRect B = new(5, 5, 10, 10);

    [Fact]
    public void FirstSeen_ReturnsRegister()
    {
        var t = new OutboundRectTracker();
        var msg = t.OnRect("x", A, hwnd: 42);
        var reg = Assert.IsType<RegisterMessage>(msg);
        Assert.Equal("x", reg.Id);
        Assert.Equal(42, reg.Hwnd);
        Assert.Equal(A, reg.Rect);
    }

    [Fact]
    public void SameRect_ReturnsNull()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Assert.Null(t.OnRect("x", A, 42));
    }

    [Fact]
    public void ChangedRect_ReturnsUpdate()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        var upd = Assert.IsType<UpdateMessage>(t.OnRect("x", B, 42));
        Assert.Equal(B, upd.Rect);
    }

    [Fact]
    public void AfterReset_ReturnsRegisterAgain()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        t.Reset();
        Assert.IsType<RegisterMessage>(t.OnRect("x", A, 42));
    }

    [Fact]
    public void Unregister_ReturnsUnregister_AndForgets()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Assert.IsType<UnregisterMessage>(t.OnUnregister("x"));
        Assert.IsType<RegisterMessage>(t.OnRect("x", A, 42));
    }
}
