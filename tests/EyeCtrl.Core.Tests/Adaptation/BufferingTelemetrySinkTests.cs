namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class BufferingTelemetrySinkTests
{
    private static DwellOutcome Outcome(long ts) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void RetainsReportsInOrder()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(10));
        sink.Report(Outcome(20));

        var snap = sink.Snapshot();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(10L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(20L);
    }

    [Fact]
    public void DropsOutOfOrderReports()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(100));
        sink.Report(Outcome(50)); // older than last accepted → dropped

        Check.That(sink.Snapshot()).HasSize(1);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(1L);
    }

    [Fact]
    public void BoundedToCapacity_KeepsNewest()
    {
        var sink = new BufferingTelemetrySink(capacity: 2);
        sink.Report(Outcome(1));
        sink.Report(Outcome(2));
        sink.Report(Outcome(3)); // evicts oldest (ts=1)

        var snap = sink.Snapshot();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(2L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(3L);
    }
}
