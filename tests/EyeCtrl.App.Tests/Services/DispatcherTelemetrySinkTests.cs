namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class DispatcherTelemetrySinkTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private sealed class ThrowingSink : IStruggleTelemetrySink
    {
        public void Report(in DwellOutcome outcome) => throw new InvalidOperationException("boom");
    }

    private static DwellOutcome Outcome(long ts) => new(
        ts, DwellContext.Toolbar, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void OnThread_ForwardsDirectly()
    {
        var inner = new RecordingSink();
        var sink = new DispatcherTelemetrySink(inner, hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void OffThread_PostsToSerializedChannel()
    {
        var inner = new RecordingSink();
        Action? queued = null;
        var sink = new DispatcherTelemetrySink(inner, hasThreadAccess: () => false, post: a => queued = a);
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).IsEmpty();   // not forwarded until the channel runs it
        queued!();
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void InnerThrows_IsSwallowed()
    {
        var sink = new DispatcherTelemetrySink(new ThrowingSink(), hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1)); // must not throw
    }
}
