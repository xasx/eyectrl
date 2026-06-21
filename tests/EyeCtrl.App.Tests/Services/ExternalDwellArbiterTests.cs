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
        Check.That(a.Process(inside, 0).ActivatedId).IsNull();
        Check.That(a.Process(inside, 400 * Ms).ActivatedId).IsNull();
        var done = a.Process(inside, 800 * Ms);
        Check.That(done.ActivatedId).IsEqualTo("btn");
    }

    [Fact]
    public void Process_WhileAccumulating_ReturnsFeedbackRectAndSuppress()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 400 * Ms);
        Check.That(r.FeedbackId).IsEqualTo("btn");
        Check.That(r.FeedbackRect).IsEqualTo(Btn);
        Check.That(r.Suppress).IsTrue();
        Check.That(r.Progress >= 0.4 && r.Progress <= 0.6).IsTrue();
    }

    [Fact]
    public void Gating_WrongTopmostWindow_NeverAccumulates()
    {
        var a = new ExternalDwellArbiter(_ => 999, cooldownMs: 0);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 5000 * Ms);
        Check.That(r.ActivatedId).IsNull();
        Check.That(r.FeedbackId).IsNull();
        Check.That(r.Suppress).IsFalse();
    }

    [Fact]
    public void GazeOutsideRect_NoFeedback()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var r = a.Process(new PhysicalPoint(1000, 1000), 100 * Ms);
        Check.That(r.FeedbackId).IsNull();
        Check.That(r.Suppress).IsFalse();
    }

    [Fact]
    public void Cooldown_BlocksImmediateRetrigger()
    {
        var a = new ExternalDwellArbiter(_ => 7, cooldownMs: 500);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        Check.That(a.Process(inside, 800 * Ms).ActivatedId).IsEqualTo("btn");
        Check.That(a.Process(inside, 900 * Ms).FeedbackId).IsNull();
        a.Process(inside, 1400 * Ms);
        Check.That(a.Process(inside, 2200 * Ms).ActivatedId).IsEqualTo("btn");
    }

    [Fact]
    public void Remove_StopsTargeting()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        a.Remove("btn");
        var r = a.Process(new PhysicalPoint(140, 120), 5000 * Ms);
        Check.That(r.ActivatedId).IsNull();
        Check.That(r.FeedbackId).IsNull();
    }

}
