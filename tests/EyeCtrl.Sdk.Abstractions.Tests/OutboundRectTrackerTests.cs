namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

public class OutboundRectTrackerTests
{
    private static readonly SdkRect A = new(0, 0, 10, 10);
    private static readonly SdkRect B = new(5, 5, 10, 10);

    [Fact]
    public void FirstSeen_ReturnsRegister()
    {
        var t = new OutboundRectTracker();
        var msg = t.OnRect("x", A, hwnd: 42);
        Check.That(msg).IsInstanceOf<RegisterMessage>();
        var reg = (RegisterMessage)msg!;
        Check.That(reg.Id).IsEqualTo("x");
        Check.That(reg.Hwnd).IsEqualTo(42);
        Check.That(reg.Rect).IsEqualTo(A);
    }

    [Fact]
    public void SameRect_ReturnsNull()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Check.That(t.OnRect("x", A, 42)).IsNull();
    }

    [Fact]
    public void ChangedRect_ReturnsUpdate()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        var result = t.OnRect("x", B, 42);
        Check.That(result).IsInstanceOf<UpdateMessage>();
        var upd = (UpdateMessage)result!;
        Check.That(upd.Rect).IsEqualTo(B);
    }

    [Fact]
    public void AfterReset_ReturnsRegisterAgain()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        t.Reset();
        Check.That(t.OnRect("x", A, 42)).IsInstanceOf<RegisterMessage>();
    }

    [Fact]
    public void Unregister_ReturnsUnregister_AndForgets()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Check.That(t.OnUnregister("x")).IsInstanceOf<UnregisterMessage>();
        Check.That(t.OnRect("x", A, 42)).IsInstanceOf<RegisterMessage>();
    }
}
