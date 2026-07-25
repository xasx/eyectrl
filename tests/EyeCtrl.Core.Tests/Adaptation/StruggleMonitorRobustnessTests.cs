namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Monitor-level guards from the PR #71 review: surviving a gaze-clock restart, and honouring the fatigue
/// amplifier's confidence gate.
/// </summary>
public class StruggleMonitorRobustnessTests
{
    private static DwellOutcome Abandon(long ts, DwellContext ctx, string id) =>
        new(ts, ctx, id, OutcomeKind.Abandoned, AbandonReason.GazeLeft, 0.9,
            new PhysicalPoint(150, 100), new PhysicalPoint(100, 100), 30, null);

    [Fact]
    public void Assess_AfterClockRegression_ReanchorsSessionInsteadOfGoingBlind()
    {
        // The coordinator resets the session on the DISCONNECT edge, stamping _sessionStart from the old
        // clock. Nothing resets on the reconnect edge, so if the tracker clock restarts lower, _sessionStart
        // stays above every later `now` and the evidence window selects nothing — permanently.
        var sink = new BufferingTelemetrySink(capacity: 64);
        var monitor = new StruggleMonitor(sink, new EyeCareSettings());

        monitor.ResetSession(50_000_000); // session anchored 50 s into the old clock

        // Clock restarts near zero; fresh evidence arrives with small timestamps.
        for (int i = 0; i < 6; i++)
            sink.Report(Abandon(100_000 + i * 100_000, DwellContext.ActionMenu, $"b{i}"));

        var (dwell, _) = monitor.SelectEvidence(700_000);

        Check.That(dwell).HasSize(6);
    }

    [Fact]
    public void Assess_ClockRegression_DoesNotDiscardEvidenceOnceReanchored()
    {
        var sink = new BufferingTelemetrySink(capacity: 64);
        var monitor = new StruggleMonitor(sink, new EyeCareSettings());
        monitor.ResetSession(50_000_000);

        for (int i = 0; i < 6; i++)
            sink.Report(Abandon(100_000 + i * 100_000, DwellContext.ActionMenu, $"b{i}"));

        // First assess re-anchors; a second one must still see the evidence (the anchor moved to the
        // regressed clock, not to "now forever").
        monitor.Assess(700_000);
        var (dwell, _) = monitor.SelectEvidence(800_000);

        Check.That(dwell).HasSize(6);
    }

    [Fact]
    public void Assess_ForwardClock_LeavesSessionBoundaryIntact()
    {
        // The re-anchor must trigger only on regression: evidence at or before an established session start
        // is still correctly excluded.
        var sink = new BufferingTelemetrySink(capacity: 64);
        var monitor = new StruggleMonitor(sink, new EyeCareSettings());

        sink.Report(Abandon(1_000_000, DwellContext.ActionMenu, "old"));
        monitor.ResetSession(2_000_000); // everything at or before 2 s belongs to the prior session
        sink.Report(Abandon(3_000_000, DwellContext.ActionMenu, "new"));

        var (dwell, _) = monitor.SelectEvidence(4_000_000);

        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TargetId).IsEqualTo("new");
    }

    [Fact]
    public void Assess_UngatedFatigue_DoesNotAmplifyTheAggregate()
    {
        // Fatigue is subject to F4 like every other signal: one thin gaze-quality window must not scale the
        // actionable aggregate (which also drives cue intensity).
        var sink = new BufferingTelemetrySink(capacity: 64);
        var eyeCare = new EyeCareSettings { FatigueMonitorEnabled = true };
        var monitor = new StruggleMonitor(sink, eyeCare);

        // Enough abandons to gate the sustain-failure mode.
        for (int i = 0; i < 6; i++)
            sink.Report(Abandon(i * 100_000, DwellContext.ActionMenu, $"b{i}"));
        // A single high-fatigue summary — below ScoreFatigue's 3-summary volume gate.
        sink.ReportGazeQuality(new GazeQualitySummary(600_000, 0.0, 10, 1.0, 30));

        StruggleAssessment assessment = monitor.Assess(700_000);
        double baseLevel = 0.0;
        foreach (var m in assessment.Modes)
            if (m.GatePassed)
                baseLevel = Math.Max(baseLevel, m.Score);

        Check.That(baseLevel > 0.0).IsTrue(); // there IS gated struggle to amplify
        Check.That(assessment.AggregateLevel).IsCloseTo(baseLevel, 1e-9); // but no amplification applied
    }
}
