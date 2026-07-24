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

    // 4 near-complete abandons in one context, spaced > 3 s (no retries), tiny near-miss (ratio < 1):
    // gates Abandonment (score 0.8) → SustainFailure, WITHOUT gating Aiming (ratio 0.5) or Drift (1 ctx).
    private static DwellOutcome[] SustainFailureDwell() => new[]
    {
        Abandon(0 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(10 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(20 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(30 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
    };

    [Fact]
    public void Assess_Empty_ReturnsNone()
    {
        var monitor = new StruggleMonitor(SinkWith(), EyeCare());
        var result = monitor.Assess(100 * Sec);
        Check.That(result.Modes).IsEmpty();
        Check.That(result.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void Assess_HighProgressAbandonment_ClassifiesSustainFailure()
    {
        var monitor = new StruggleMonitor(SinkWith(dwell: SustainFailureDwell()), EyeCare());
        var a = monitor.Assess(40 * Sec);

        var sustain = a.ForMode(FailureMode.SustainFailure);
        Check.That(sustain.HasValue).IsTrue();
        Check.That(sustain!.Value.GatePassed).IsTrue();
        Check.That(sustain!.Value.Score > 0).IsTrue();
        Check.That(sustain!.Value.Context.HasValue).IsFalse(); // global momentary response (§7.2)
        // Not misclassified as aiming (ratio 0.5) or drift (single context).
        Check.That(a.ForMode(FailureMode.Aiming).HasValue).IsFalse();
        Check.That(a.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }

    [Fact]
    public void Assess_JitterAlone_ContributesToSustainFailure()
    {
        // No dwell; only high-jitter quality (mean 500 ≥ high 400, ≥ 3 summaries) → jitter gates →
        // SustainFailure present (jitter is a sustain-failure feeder alongside abandonment).
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.0, 500), Quality(Sec, 0.0, 500), Quality(2 * Sec, 0.0, 500),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.SustainFailure)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_ReApproach_ClassifiesAccidentalActivation()
    {
        // 3 activations; the first (center 100,100) is reversed by a same-context follow-up whose CENTER
        // (120,100) is within 2× radius (60 px) and within 1.5 s. The follow-up is built inline so its
        // TargetCenter is controlled — the Abandon helper fixes center at (0,0), which wouldn't re-approach.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            new DwellOutcome(500_000, DwellContext.ActionMenu, "A2", OutcomeKind.Abandoned,
                AbandonReason.GazeLeft, 0.5, new PhysicalPoint(120, 100), new PhysicalPoint(120, 100), 30, null),
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        }), EyeCare());
        var a = monitor.Assess(21 * Sec);

        Check.That(a.ForMode(FailureMode.AccidentalActivation)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_TimeBasedPoorTracking_ClassifiesPoorTracking_WithZeroInteractions()
    {
        // No dwell at all; quality with high invalid fraction over ≥ 2 s → poor-tracking gates on time.
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.6, 0), Quality(Sec, 0.6, 0), Quality(2 * Sec, 0.6, 0),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.PoorTracking)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_HighNearMissLowProgress_ClassifiesAimingOnly()
    {
        // 4 abandons, ratio 3 (nmX 90 / radius 30) but progress 0.35 (< 0.5 high-progress) and spaced
        // > 3 s (no retries): gates Aiming, NOT SustainFailure (abandonment score 0). Single context and
        // < 5 abandons → not Drift.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Abandon(0 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(20 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(30 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
        }), EyeCare());
        var a = monitor.Assess(40 * Sec);

        Check.That(a.ForMode(FailureMode.Aiming)!.Value.GatePassed).IsTrue();
        Check.That(a.ForMode(FailureMode.SustainFailure).HasValue).IsFalse();
        Check.That(a.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }

    [Fact]
    public void Assess_BelowGateStruggle_IsSurfacedButNotGated()
    {
        // 2 high-progress abandons (< 4-attempt gate) → SustainFailure present with score > 0, gate false.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Abandon(0 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        }), EyeCare());
        var a = monitor.Assess(20 * Sec);

        var sustain = a.ForMode(FailureMode.SustainFailure);
        Check.That(sustain.HasValue).IsTrue();
        Check.That(sustain!.Value.Score > 0).IsTrue();
        Check.That(sustain!.Value.GatePassed).IsFalse();
    }

    [Fact]
    public void Assess_AggregateIsPeakGatedModeScore()
    {
        // Aiming (score 1.0, gated) + poor-tracking (invalid 0.3 → score 0.5, gated). Base aggregate =
        // max gated score = 1.0. (Fatigue disabled → no amplification; stable across Task 3.)
        var monitor = new StruggleMonitor(SinkWith(
            dwell: new[]
            {
                Abandon(0 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(10 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(20 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(30 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            },
            quality: new[] { Quality(0, 0.3, 0), Quality(Sec, 0.3, 0), Quality(2 * Sec, 0.3, 0) }),
            EyeCare());
        var a = monitor.Assess(40 * Sec);

        Check.That(a.ForMode(FailureMode.Aiming).HasValue).IsTrue();
        Check.That(a.ForMode(FailureMode.PoorTracking).HasValue).IsTrue();
        Check.That(a.AggregateLevel).IsEqualTo(1.0);
    }
}
