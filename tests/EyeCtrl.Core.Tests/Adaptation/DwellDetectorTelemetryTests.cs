namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DwellDetectorTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    // Drives gaze inside a target for `samples` frames at 30 ms steps starting at `startTs`.
    private static long DriveInside(DwellDetector d, PhysicalPoint p, int samples, long startTs)
    {
        long t = startTs;
        for (int i = 0; i < samples; i++) { d.Update(p, t); t += 30_000; }
        return t;
    }

    [Fact]
    public void Activation_EmitsActivatedOutcome_WithContextGeometryAndFullProgress()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(cooldownMs: 0, context: DwellContext.ActionMenu, sink: sink);
        detector.AddTarget(new DwellTarget("btn", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        // 200 ms dwell at 30 ms/sample → activates within ~8 samples.
        DriveInside(detector, new PhysicalPoint(100, 100), 9, startTs: 0);

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(o.Reason).IsEqualTo(AbandonReason.None);
        Check.That(o.Context).IsEqualTo(DwellContext.ActionMenu);
        Check.That(o.TargetId).IsEqualTo("btn");
        Check.That(o.ProgressAtEnd).IsEqualTo(1.0);
        // Geometry / vector (design §12: activation "with correct reason/geometry/vector").
        Check.That(o.GazePoint).IsEqualTo(new PhysicalPoint(100, 100));
        Check.That(o.TargetCenter).IsEqualTo(new PhysicalPoint(100, 100));
        Check.That(o.TargetRadius).IsEqualTo(30.0);
        Check.That(o.TargetBounds.HasValue).IsFalse();
        Check.That(o.NearMissX).IsEqualTo(0.0);
        Check.That(o.NearMissY).IsEqualTo(0.0);
    }
}
