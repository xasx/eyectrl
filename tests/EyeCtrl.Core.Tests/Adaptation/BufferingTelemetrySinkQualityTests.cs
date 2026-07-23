namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class BufferingTelemetrySinkQualityTests
{
    private static GazeQualitySummary Summary(long ts) => new(ts, 0.1, 12.0, 0.0, 30);

    [Fact]
    public void RetainsSummariesInOrder_AndDropsOutOfOrder()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.ReportGazeQuality(Summary(1000));
        sink.ReportGazeQuality(Summary(2000));
        sink.ReportGazeQuality(Summary(500)); // older than last accepted → dropped

        var snap = sink.SnapshotQuality();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(1000L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(2000L);
        Check.That(sink.DroppedOutOfOrderQuality).IsEqualTo(1L);
    }
}
