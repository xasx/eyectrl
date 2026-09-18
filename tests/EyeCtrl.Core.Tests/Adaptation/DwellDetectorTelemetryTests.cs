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

    [Fact]
    public void GazeLeavesAfterEnoughProgress_EmitsAbandoned_GazeLeft()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        // Enter at t=0, dwell to ~50% (5 samples spans 120 ms of the 300 ms), then leave to empty space.
        long t = DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0);
        detector.Update(new PhysicalPoint(700, 700), t); // no target under gaze → nearest is null

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(o.Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(o.Context).IsEqualTo(DwellContext.Keyboard);
        Check.That(o.ProgressAtEnd >= 0.3).IsTrue();
        Check.That(o.NearMissX).IsEqualTo(600.0); // 700 - 100
    }

    [Fact]
    public void NearestSwitchesAfterEnoughProgress_EmitsAbandoned_NearestChanged()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Toolbar, sink: sink);
        detector.AddTarget(new DwellTarget("A", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        detector.AddTarget(new DwellTarget("B", new PhysicalPoint(400, 100), radius: 30, dwellTimeMs: 300));

        long t = DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // dwell A to ~50%
        detector.Update(new PhysicalPoint(400, 100), t); // gaze jumps to B → A abandoned, B now nearest

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
    }

    [Fact]
    public void GazeLeavesBelowThreshold_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        // Only ~1 sample of accumulation (~10% of 300 ms) before leaving — below the 0.3 threshold.
        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 30_000);
        detector.Update(new PhysicalPoint(700, 700), 60_000);

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void AbandonAtExactlyThreshold_Emits()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        detector.Update(new PhysicalPoint(100, 100), 0);       // enter, EnteredTimestamp = 0
        detector.Update(new PhysicalPoint(700, 700), 60_000);  // leave → progress = 60000/200000 = 0.3 exactly

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsEqualTo(0.3);
    }

    /// <summary>
    /// The abandon/telemetry path must not credit a gap either. Without the clamp, the stale
    /// <c>EnteredTimestamp</c> made a 330 ms silence mid-dwell read as ~780 ms of held gaze, so a
    /// blink that interrupted a 1000 ms dwell was reported as an abandonment at high progress.
    /// </summary>
    [Fact]
    public void GapBeforeLeaving_DoesNotEmitAbandoned()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 1000));

        // 480 ms of the dwell, then 330 ms in which the caller skips every gap-held sample, then
        // the first real sample leaves the target.
        long t = DriveInside(detector, new PhysicalPoint(100, 100), 16, startTs: 0);
        detector.Update(new PhysicalPoint(700, 700), t + 300_000);

        Check.WithCustomMessage(
                "A silent interval mid-dwell produced an abandonment outcome: the gap was credited "
                + "to the stale EnteredTimestamp, so telemetry reported ~780 ms of held gaze for an "
                + "interaction a blink interrupted. The abandon path needs the same clamp as "
                + "activation.")
            .That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void FilterExcludesTargetMidDwell_EmitsNothing()
    {
        // Regression: an administratively excluded target (gaze physically unchanged) must NOT
        // be reported as an abandonment. Mirrors Update_FilterFlipsMidDwell_ExcludedTargetStateResets.
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.ActionMenu, sink: sink);
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 150_000);                       // ~75% progress
        detector.Update(new PhysicalPoint(100, 100), 180_000, targetFilter: _ => false); // excluded, gaze unchanged

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void ResetAllState_AfterPartialDwell_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // ~50%, no completion
        detector.ResetAllState(); // programmatic reset — not a struggle signal

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void TargetRemoval_AfterPartialDwell_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Notes, sink: sink);
        detector.AddTarget(new DwellTarget("n", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        long t = DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // ~50%
        detector.RemoveTarget("n");
        detector.ClearTargets();
        // Gaze leaves to empty space AFTER removal: a subsequent Update must not emit an
        // abandonment for the no-longer-tracked target (it left the snapshot).
        detector.Update(new PhysicalPoint(700, 700), t + 30_000);

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void Cooldown_AfterActivation_NeverEmitsAbandoned()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(cooldownMs: 300, context: DwellContext.Toolbar, sink: sink);
        detector.AddTarget(new DwellTarget("b", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        // Keep gazing through activation and into the cooldown window.
        DriveInside(detector, new PhysicalPoint(100, 100), 20, startTs: 0);

        Check.That(sink.Outcomes).Not.IsEmpty();
        Check.That(sink.Outcomes.TrueForAll(o => o.Kind == OutcomeKind.Activated)).IsTrue();
    }

    [Fact]
    public void NullSink_NeverThrows_AndStillActivates()
    {
        var detector = new DwellDetector(); // default: context Other, no sink
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));
        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        DriveInside(detector, new PhysicalPoint(100, 100), 9, startTs: 0);

        Check.That(activated).IsTrue();
    }
}
