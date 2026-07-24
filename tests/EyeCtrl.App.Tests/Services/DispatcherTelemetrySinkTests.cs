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

    [Fact]
    public void OffThread_PostThrows_IsSwallowed()
    {
        // The post delegate itself can throw (e.g. Dispatcher.BeginInvoke during shutdown). The
        // no-throw contract (design §11) covers it: Report must not propagate the post failure.
        var sink = new DispatcherTelemetrySink(
            new RecordingSink(),
            hasThreadAccess: () => false,
            post: _ => throw new InvalidOperationException("post failed"));
        sink.Report(Outcome(1)); // must not throw
    }

    [Fact]
    public void OffThread_PostThrows_IsSwallowed_GazeQuality()
    {
        var quality = new GazeQualitySummary(1, 0.0, 0.0, 0.0, 1);
        var sink = new DispatcherTelemetrySink(
            new RecordingSink(),
            hasThreadAccess: () => false,
            post: _ => throw new InvalidOperationException("post failed"),
            innerQuality: new RecordingQualitySink());
        sink.ReportGazeQuality(quality); // must not throw
    }

    private sealed class RecordingQualitySink : IGazeQualitySink
    {
        public List<GazeQualitySummary> Summaries { get; } = new();
        public void ReportGazeQuality(in GazeQualitySummary summary) => Summaries.Add(summary);
    }
}
