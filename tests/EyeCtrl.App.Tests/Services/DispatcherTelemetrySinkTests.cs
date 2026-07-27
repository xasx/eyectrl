namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

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

    // Reporting is gated on AdaptationSettings.Enabled (§9.1); the forwarding tests opt in, and
    // Disabled_Drops* below cover the gated-off side.
    private static AdaptationSettings Adaptation(bool enabled)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        return settings.Adaptation;
    }

    private static AdaptationSettings Enabled() => Adaptation(enabled: true);

    private static DwellOutcome Outcome(long ts) => new(
        ts, DwellContext.Toolbar, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void OnThread_ForwardsDirectly()
    {
        var inner = new RecordingSink();
        var sink = new DispatcherTelemetrySink(inner, Enabled(), hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void OffThread_PostsToSerializedChannel()
    {
        var inner = new RecordingSink();
        Action? queued = null;
        var sink = new DispatcherTelemetrySink(inner, Enabled(), hasThreadAccess: () => false, post: a => queued = a);
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).IsEmpty();   // not forwarded until the channel runs it
        queued!();
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void InnerThrows_IsSwallowed()
    {
        var sink = new DispatcherTelemetrySink(new ThrowingSink(), Enabled(), hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1)); // must not throw
    }

    [Fact]
    public void OffThread_PostThrows_IsSwallowed()
    {
        // The post delegate itself can throw (e.g. Dispatcher.BeginInvoke during shutdown). The
        // no-throw contract (design §11) covers it: Report must not propagate the post failure.
        var sink = new DispatcherTelemetrySink(
            new RecordingSink(),
            Enabled(),
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
            Enabled(),
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

    [Fact]
    public void Disabled_DropsOutcome_WithoutForwardingOrPosting()
    {
        // §9.1 enable gate. Asserting `posted == false` matters as much as the empty inner sink: dropping at
        // this boundary is what keeps a gaze-thread report from costing a Dispatcher.BeginInvoke while the
        // feature is off.
        var inner = new RecordingSink();
        bool posted = false;
        var sink = new DispatcherTelemetrySink(
            inner, Adaptation(enabled: false),
            hasThreadAccess: () => false,
            post: _ => posted = true);

        sink.Report(Outcome(1));

        Check.That(inner.Outcomes).IsEmpty();
        Check.That(posted).IsFalse();
    }

    [Fact]
    public void Disabled_DropsGazeQualitySummary()
    {
        var quality = new RecordingQualitySink();
        bool posted = false;
        var sink = new DispatcherTelemetrySink(
            new RecordingSink(), Adaptation(enabled: false),
            hasThreadAccess: () => false,
            post: _ => posted = true,
            innerQuality: quality);

        sink.ReportGazeQuality(new GazeQualitySummary(1, 0.0, 0.0, 0.0, 1));

        Check.That(quality.Summaries).IsEmpty();
        Check.That(posted).IsFalse();
    }

    [Fact]
    public void EnablingAtRuntime_StartsForwarding()
    {
        // The gate is read per report, not latched, so the settings toggle takes effect on the live sink.
        var inner = new RecordingSink();
        var adaptation = Adaptation(enabled: false);
        var sink = new DispatcherTelemetrySink(
            inner, adaptation, hasThreadAccess: () => true, post: _ => { });

        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).IsEmpty();

        adaptation.Enabled = true;
        sink.Report(Outcome(2));
        Check.That(inner.Outcomes).HasSize(1);
    }
}
