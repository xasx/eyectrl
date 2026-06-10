namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using Xunit;

public class ExternalDwellArbiterTests
{
    private const long Ms = 1000;
    private static readonly PhysicalRect Btn = new(100, 100, 80, 40);

    private static ExternalDwellArbiter MakeArbiter(long rootHwnd = 7) =>
        new(_ => rootHwnd, cooldownMs: 0);

    [Fact]
    public void DwellOnGatedTarget_ActivatesAfterDwellTime()
    {
        var a = MakeArbiter(rootHwnd: 7);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);

        var inside = new PhysicalPoint(140, 120);
        Assert.Null(a.Process(inside, 0).ActivatedId);
        Assert.Null(a.Process(inside, 400 * Ms).ActivatedId);
        var done = a.Process(inside, 800 * Ms);
        Assert.Equal("btn", done.ActivatedId);
    }

    [Fact]
    public void Process_WhileAccumulating_ReturnsFeedbackRectAndSuppress()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 400 * Ms);
        Assert.Equal("btn", r.FeedbackId);
        Assert.Equal(Btn, r.FeedbackRect);
        Assert.True(r.Suppress);
        Assert.InRange(r.Progress, 0.4, 0.6);
    }

    [Fact]
    public void Gating_WrongTopmostWindow_NeverAccumulates()
    {
        var a = new ExternalDwellArbiter(_ => 999, cooldownMs: 0);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 5000 * Ms);
        Assert.Null(r.ActivatedId);
        Assert.Null(r.FeedbackId);
        Assert.False(r.Suppress);
    }

    [Fact]
    public void GazeOutsideRect_NoFeedback()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var r = a.Process(new PhysicalPoint(1000, 1000), 100 * Ms);
        Assert.Null(r.FeedbackId);
        Assert.False(r.Suppress);
    }

    [Fact]
    public void Cooldown_BlocksImmediateRetrigger()
    {
        var a = new ExternalDwellArbiter(_ => 7, cooldownMs: 500);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        Assert.Equal("btn", a.Process(inside, 800 * Ms).ActivatedId);
        Assert.Null(a.Process(inside, 900 * Ms).FeedbackId);
        a.Process(inside, 1400 * Ms);
        Assert.Equal("btn", a.Process(inside, 2200 * Ms).ActivatedId);
    }

    [Fact]
    public void Remove_StopsTargeting()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        a.Remove("btn");
        var r = a.Process(new PhysicalPoint(140, 120), 5000 * Ms);
        Assert.Null(r.ActivatedId);
        Assert.Null(r.FeedbackId);
    }

}
