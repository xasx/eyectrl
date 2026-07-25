namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

/// <summary>
/// The gaze clock is monotonic but has an arbitrary epoch, so a device reconnect can move it
/// backwards. Before the resync guard, one backward jump left the ordering watermark permanently in
/// the future and every later event was dropped for the rest of the session.
/// </summary>
public class TelemetryClockRestartTests
{
    private static DwellOutcome Outcome(long ts, DwellContext ctx = DwellContext.Toolbar) => new(
        ts, ctx, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void LargeBackwardJump_IsTreatedAsClockRestart_AndKeepsAccepting()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(10_000_000));       // established watermark, 10 s in
        sink.Report(Outcome(50_000));           // clock restarted near zero — accepted, resyncs
        sink.Report(Outcome(80_000));           // and the session keeps recording
        sink.Report(Outcome(110_000));

        // Pre-restart evidence is dropped: its timestamps are not comparable with post-restart ones.
        Check.That(sink.Snapshot()).HasSize(3);
        Check.That(sink.ClockRestarts).IsEqualTo(1L);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(0L);
    }

    [Fact]
    public void ClockRestart_KeepsSnapshotsNonDecreasing()
    {
        // StruggleSignals documents an oldest-first, non-decreasing window and DEBUG-asserts it; its
        // early-break pruning and span gates silently mis-score otherwise. Retaining pre-restart entries
        // would put a descending step in the middle of the snapshot.
        var sink = new BufferingTelemetrySink(capacity: 16);
        sink.Report(Outcome(9_000_000, DwellContext.Toolbar));
        sink.Report(Outcome(9_500_000, DwellContext.Keyboard));
        sink.ReportGazeQuality(new GazeQualitySummary(9_200_000, 0.1, 5, 0, 30));

        sink.Report(Outcome(40_000, DwellContext.Toolbar)); // clock restart
        sink.Report(Outcome(70_000, DwellContext.Keyboard));
        sink.ReportGazeQuality(new GazeQualitySummary(60_000, 0.1, 5, 0, 30));

        AssertNonDecreasing(sink.Snapshot().Select(o => o.TimestampMicroseconds).ToList());
        AssertNonDecreasing(sink.SnapshotQuality().Select(q => q.TimestampMicroseconds).ToList());

        // Both streams are purged by either stream's restart detection, so neither retains a stale entry.
        Check.That(sink.Snapshot()).HasSize(2);
        Check.That(sink.SnapshotQuality()).HasSize(1);
    }

    private static void AssertNonDecreasing(IReadOnlyList<long> timestamps)
    {
        for (int i = 1; i < timestamps.Count; i++)
            Check.WithCustomMessage($"snapshot must be non-decreasing; index {i} went backwards")
                .That(timestamps[i] >= timestamps[i - 1]).IsTrue();
    }

    [Fact]
    public void SmallBackwardStep_IsStillDroppedAsReordering()
    {
        // Below the restart threshold this is genuine cross-thread marshaling skew, not a new epoch.
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(1_000_000));
        sink.Report(Outcome(999_000)); // 1 ms earlier

        Check.That(sink.Snapshot()).HasSize(1);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(1L);
        Check.That(sink.ClockRestarts).IsEqualTo(0L);
    }

    [Fact]
    public void OrderingIsTrackedPerContext_SoOneSurfaceCannotStarveAnother()
    {
        // ExternalDwellService runs its detector on the gaze thread and is marshaled asynchronously,
        // while overlay VMs report synchronously on the UI thread. A shared watermark would discard the
        // queued ExternalDwell outcome purely because a different surface reported a newer one first.
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(500_000, DwellContext.Toolbar));
        sink.Report(Outcome(400_000, DwellContext.ExternalDwell)); // older, but a different surface

        Check.That(sink.Snapshot()).HasSize(2);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(0L);
    }

    [Fact]
    public void OutOfOrderWithinTheSameContext_IsStillDropped()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(500_000, DwellContext.Toolbar));
        sink.Report(Outcome(400_000, DwellContext.Toolbar));

        Check.That(sink.Snapshot()).HasSize(1);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(1L);
    }

    [Fact]
    public void GazeQuality_LargeBackwardJump_KeepsAccepting()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.ReportGazeQuality(new GazeQualitySummary(10_000_000, 0.1, 5, 0, 30));
        sink.ReportGazeQuality(new GazeQualitySummary(50_000, 0.1, 5, 0, 30));
        sink.ReportGazeQuality(new GazeQualitySummary(550_000, 0.1, 5, 0, 30));

        // The pre-restart summary is purged (see ClockRestart_KeepsSnapshotsNonDecreasing); the two
        // post-restart ones are retained, and emission continues rather than wedging.
        Check.That(sink.SnapshotQuality()).HasSize(2);
        Check.That(sink.ClockRestarts).IsEqualTo(1L);
    }

    [Fact]
    public void Aggregator_BackwardJump_ResyncsInsteadOfWedging()
    {
        var recorded = new List<GazeQualitySummary>();
        var probe = new DelegateQualitySink(recorded.Add);
        var agg = new GazeQualityAggregator(probe, windowMs: 500);

        // Establish a window far into the session, then restart the clock near zero.
        agg.OnSample(new GazePoint(1, 1, 10_000_000, true), 5, 0);
        agg.OnSample(new GazePoint(1, 1, 50_000, true), 5, 0);   // backward jump → resync
        Check.That(agg.ClockRestarts).IsEqualTo(1L);

        // Without the resync, _windowStart stayed at 10 s and this never closed a window.
        agg.OnSample(new GazePoint(1, 1, 300_000, true), 5, 0);
        agg.OnSample(new GazePoint(1, 1, 560_000, true), 5, 0);  // 510 ms past the resynced start

        Check.That(recorded).Not.IsEmpty();
    }

    private sealed class DelegateQualitySink(Action<GazeQualitySummary> onReport) : IGazeQualitySink
    {
        public void ReportGazeQuality(in GazeQualitySummary summary) => onReport(summary);
    }
}
