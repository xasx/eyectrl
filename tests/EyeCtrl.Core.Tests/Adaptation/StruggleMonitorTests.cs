namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class StruggleMonitorTests
{
    private const long Sec = 1_000_000; // microseconds per second

    private static EyeCareSettings EyeCare(bool fatigue = false) => new() { FatigueMonitorEnabled = fatigue };

    private static BufferingTelemetrySink SinkWith(
        IEnumerable<DwellOutcome>? dwell = null,
        IEnumerable<GazeQualitySummary>? quality = null)
    {
        var sink = new BufferingTelemetrySink(capacity: 512);
        if (dwell is not null)
            foreach (var d in dwell) sink.Report(d);      // must be fed ascending per the monotonic guard
        if (quality is not null)
            foreach (var q in quality) sink.ReportGazeQuality(q);
        return sink;
    }

    // A controlled abandonment: TargetCenter=(0,0), GazePoint=(nmX,nmY) → NearMissX/Y == nmX/nmY.
    private static DwellOutcome Abandon(long ts, DwellContext ctx, double progress, double radius,
        double nmX = 0, double nmY = 0, string id = "t") => new(
            ts, ctx, id, OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
            new PhysicalPoint(nmX, nmY), new PhysicalPoint(0, 0), radius, null);

    private static DwellOutcome Activate(long ts, DwellContext ctx, string id, double cx, double cy, double radius) =>
        new(ts, ctx, id, OutcomeKind.Activated, AbandonReason.None, 1.0,
            new PhysicalPoint(cx, cy), new PhysicalPoint(cx, cy), radius, null);

    private static GazeQualitySummary Quality(long ts, double invalid, double jitter, double fatigue = 0.0, int count = 30) =>
        new(ts, invalid, jitter, fatigue, count);

    [Fact]
    public void SelectEvidence_RollingMaxAge_ExcludesOlderThanWindow()
    {
        // Default 60 s window. now=100 s → window [40 s, 100 s]. Dwell at 10 s excluded, 90 s included.
        var sink = SinkWith(dwell: new[]
        {
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.5, 30),
            Abandon(90 * Sec, DwellContext.ActionMenu, 0.5, 30),
        });
        var monitor = new StruggleMonitor(sink, EyeCare());

        var (dwell, _) = monitor.SelectEvidence(100 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(90 * Sec);
    }

    [Fact]
    public void SelectEvidence_ResetSession_IgnoresPreBoundaryEvidence_EvenIfWithinWindow()
    {
        // Both dwell items are < 60 s old at now=100 s, but ResetSession(80 s) discards the 50 s one.
        var sink = SinkWith(dwell: new[]
        {
            Abandon(50 * Sec, DwellContext.ActionMenu, 0.5, 30),
            Abandon(90 * Sec, DwellContext.ActionMenu, 0.5, 30),
        });
        var monitor = new StruggleMonitor(sink, EyeCare());
        monitor.ResetSession(80 * Sec);

        var (dwell, _) = monitor.SelectEvidence(100 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(90 * Sec);
    }

    [Fact]
    public void SelectEvidence_IdleGap_ResetsBoundary_IsolatedFromWindow()
    {
        // Large max-age (600 s) so the rolling window does NOT do the exclusion — isolates the idle logic.
        // A 180 s gap in the quality stream (20 s → 200 s) is idle; pre-gap evidence (both streams) drops.
        var sink = SinkWith(
            dwell: new[]
            {
                Abandon(15 * Sec, DwellContext.ActionMenu, 0.5, 30),  // pre-gap → excluded
                Abandon(205 * Sec, DwellContext.ActionMenu, 0.5, 30), // post-gap → kept
            },
            quality: new[]
            {
                Quality(10 * Sec, 0.1, 5),
                Quality(20 * Sec, 0.1, 5),   // last before the gap
                Quality(200 * Sec, 0.1, 5),  // first after the 180 s gap → boundary
                Quality(210 * Sec, 0.1, 5),
            });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (dwell, quality) = monitor.SelectEvidence(250 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(205 * Sec);
        Check.That(quality).HasSize(2);
        Check.That(quality[0].TimestampMicroseconds).IsEqualTo(200 * Sec);
    }

    [Fact]
    public void SelectEvidence_GapBelowIdleThreshold_KeepsAll()
    {
        // 100 s gap (< 120 s) is not idle; with a large window all summaries are kept.
        var sink = SinkWith(quality: new[]
        {
            Quality(0, 0.1, 5),
            Quality(100 * Sec, 0.1, 5),
        });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (_, quality) = monitor.SelectEvidence(150 * Sec);
        Check.That(quality).HasSize(2);
    }

    [Fact]
    public void SelectEvidence_ExcludesEvidenceAfterNow()
    {
        var sink = SinkWith(dwell: new[] { Abandon(200 * Sec, DwellContext.ActionMenu, 0.5, 30) });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (dwell, _) = monitor.SelectEvidence(100 * Sec); // evidence is in the future relative to now
        Check.That(dwell).IsEmpty();
    }
}
