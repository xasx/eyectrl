namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class StruggleSignalsTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // A dwell outcome with the fields the 1b-③a scorers read. GazePoint is set to the target center
    // (unused by these scorers — NearMiss feeds the Aiming/Drift scorers in 1b-③b).
    private static DwellOutcome Dwell(
        long ts, OutcomeKind kind, double progress,
        DwellContext ctx = DwellContext.ActionMenu, string id = "t",
        double cx = 100, double cy = 100, double radius = 30) => new(
            ts, ctx, id, kind,
            kind == OutcomeKind.Abandoned ? AbandonReason.GazeLeft : AbandonReason.None,
            progress, new PhysicalPoint(cx, cy), new PhysicalPoint(cx, cy), radius, null);

    private static DwellOutcome Activate(long ts, DwellContext ctx = DwellContext.ActionMenu,
        string id = "t", double cx = 100, double cy = 100, double radius = 30) =>
        Dwell(ts, OutcomeKind.Activated, 1.0, ctx, id, cx, cy, radius);

    private static DwellOutcome Abandon(long ts, double progress, DwellContext ctx = DwellContext.ActionMenu,
        string id = "t", double cx = 100, double cy = 100, double radius = 30) =>
        Dwell(ts, OutcomeKind.Abandoned, progress, ctx, id, cx, cy, radius);

    // ---- Abandonment ----

    [Fact]
    public void Abandonment_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAbandonment(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Abandonment_ScoreIsProgressWeightedHighProgressRate()
    {
        // 5 attempts: 3 activations + 2 high-progress abandons (0.8, 1.0), placed last so nothing
        // follows them → no retries. score = (0.8 + 1.0) / 5 = 0.36.
        var window = new List<DwellOutcome>
        {
            Activate(0 * Sec),
            Activate(10 * Sec),
            Activate(20 * Sec),
            Abandon(30 * Sec, 0.8),
            Abandon(40 * Sec, 1.0),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.36, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Abandonment_LowProgressAbandons_ExcludedFromNumerator_ButCountAsAttempts()
    {
        // 4 attempts: 2 low-progress abandons (0.40, 0.45 < 0.5) + 2 activations, no retries → score 0.
        var window = new List<DwellOutcome>
        {
            Abandon(0 * Sec, 0.40),
            Abandon(10 * Sec, 0.45),
            Activate(20 * Sec),
            Activate(30 * Sec),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // 4 attempts meets the default gate
    }

    [Fact]
    public void Abandonment_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 attempts (< default 4): one high-progress abandon (0.8) + one activation → score 0.8/2 = 0.4.
        var window = new List<DwellOutcome> { Abandon(0, 0.8), Activate(10 * Sec) };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.4, 1e-9);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9); // 2 / 4
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Abandonment_RetrySameContextWithinWindow_AddsToScore()
    {
        // Abandon(0.6) then a same-context outcome 1 s later (≤ 3 s) → 1 retry.
        // base = 0.6/4 = 0.15; retry term = 0.5 * (1/4) = 0.125; score = 0.275.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(1 * Sec, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.275, 1e-9);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Abandonment_RetryDifferentContext_NotCounted()
    {
        // Same as above but the following outcome is a DIFFERENT context → no retry → score = 0.15.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(1 * Sec, ctx: DwellContext.Toolbar),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.15, 1e-9);
    }

    [Fact]
    public void Abandonment_RetryBeyondTimeWindow_NotCounted()
    {
        // Same context but 4 s later (> default 3 s retry window) → no retry → score = 0.15.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(4 * Sec, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.15, 1e-9);
    }

    [Fact]
    public void Abandonment_FollowingSameContextAbandon_AlsoCountsAsRetry()
    {
        // A retry is ANY strictly-later same-context outcome — a re-attempt that ALSO fails
        // (abandon → abandon) is still effort evidence (§13.2's "re-engage" definition; see
        // ScoreAbandonment docs). Pins that the follow-up need not be an activation.
        // base = (0.6 + 0.6)/4 = 0.30; #0's follow-up (#1, same ctx, 1 s) → 1 retry; term = 0.5*(1/4)
        // = 0.125; score = 0.425. (A literal "must reactivate" reading would give 0.30.)
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Abandon(1 * Sec, 0.6, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.425, 1e-9);
    }

    // ---- Accidental activation ----

    [Fact]
    public void Accidental_NoActivations_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAccidental(new List<DwellOutcome> { Abandon(0, 0.5) });
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Accidental_ReapproachWithinRadiusSameContext_CountsReversal()
    {
        // Activation at (100,100) r30, then an outcome 0.5 s later at (120,100): distance 20 ≤ 2*30=60,
        // same context → reversal. 3 activations total, 1 reversed → score = 1/3.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Abandon(500_000, 0.5, DwellContext.ActionMenu, "A2", 120, 100, 30),
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        };
        var s = StruggleSignals.ScoreAccidental(window);
        Check.That(s.Score).IsCloseTo(1.0 / 3.0, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0); // 3 activations meets the default gate
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Accidental_ReapproachOutsideRadius_NotReversal()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(500_000, DwellContext.ActionMenu, "B", 200, 100, 30), // dist 100 > 60
            Activate(1_000_000, DwellContext.ActionMenu, "C", 300, 100, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_ReapproachOutsideTimeWindow_NotReversal()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(2 * Sec, DwellContext.ActionMenu, "B", 110, 100, 30), // near, but 2 s > 1.5 s
            Activate(4 * Sec, DwellContext.ActionMenu, "C", 500, 100, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_DifferentContext_NotReapproach()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(500_000, DwellContext.Toolbar, "B", 100, 100, 30), // same center, different context
            Activate(1_000_000, DwellContext.ActionMenu, "C", 500, 500, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_RectTarget_UsesRadiusFloor()
    {
        // Rect target (radius 0): re-approach radius falls back to the 40 px floor. Following outcome at
        // distance 30 ≤ 40 → reversal. 3 activations, 1 reversed → 1/3.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 0),
            Abandon(500_000, 0.5, DwellContext.ActionMenu, "A2", 130, 100, 0), // dist 30 ≤ floor 40
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsCloseTo(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Accidental_KeyboardBackspaceCorroboration_CountsReversal_OnlyWhenIdSupplied()
    {
        // Key "e" activation, then Backspace activation 0.5 s later, FAR away (800 px) so the geometric
        // re-approach path can't catch it — only the backspace corroboration can. 4 activations, 1 reversed.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.Keyboard, "e", 100, 100, 20),
            Activate(500_000, DwellContext.Keyboard, "Back", 900, 100, 20),
            Activate(10 * Sec, DwellContext.Keyboard, "x", 100, 100, 20),
            Activate(20 * Sec, DwellContext.Keyboard, "y", 200, 100, 20),
        };

        // Disabled by default (no backspace id) → the far Backspace is not a reversal → score 0.
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);

        // Enabled → key→Backspace is a reversal → 1/4.
        var s = StruggleSignals.ScoreAccidental(window, backspaceTargetId: "Back");
        Check.That(s.Score).IsCloseTo(0.25, 1e-9);
        Check.That(s.GatePassed).IsTrue();
    }

    // A gaze-quality summary with the fields the gaze-quality scorers read (FatigueScore unused here).
    private static GazeQualitySummary Quality(long ts, double invalidFraction, double jitter, int sampleCount = 30) =>
        new(ts, invalidFraction, jitter, 0.0, sampleCount);

    // ---- Jitter ----

    [Fact]
    public void Jitter_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreJitter(new List<GazeQualitySummary>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Jitter_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 summaries (< default 3), both above the high threshold → score 1, gate false, confidence 2/3.
        var window = new List<GazeQualitySummary> { Quality(0, 0.0, 500), Quality(Sec, 0.0, 500) };
        var s = StruggleSignals.ScoreJitter(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.Confidence).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Jitter_MeanBelowLow_ScoreZero()
    {
        var window = new List<GazeQualitySummary> { Quality(0, 0, 10), Quality(Sec, 0, 20), Quality(2 * Sec, 0, 30) };
        var s = StruggleSignals.ScoreJitter(window); // mean 20 < 50
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Jitter_MeanAboveHigh_ScoreOne()
    {
        var window = new List<GazeQualitySummary> { Quality(0, 0, 500), Quality(Sec, 0, 500), Quality(2 * Sec, 0, 500) };
        Check.That(StruggleSignals.ScoreJitter(window).Score).IsEqualTo(1.0);
    }

    [Fact]
    public void Jitter_MidRamp_IsLinear()
    {
        // mean 225 is the midpoint of the [50, 400] ramp → 0.5.
        var window = new List<GazeQualitySummary> { Quality(0, 0, 225), Quality(Sec, 0, 225), Quality(2 * Sec, 0, 225) };
        Check.That(StruggleSignals.ScoreJitter(window).Score).IsCloseTo(0.5, 1e-9);
    }

    // ---- Poor tracking ----

    [Fact]
    public void PoorTracking_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScorePoorTracking(new List<GazeQualitySummary>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void PoorTracking_SpanBelowGate_GateFalse()
    {
        // Two summaries spanning 1 s (< default 2 s) → gate false, confidence 0.5. Score still computed.
        var window = new List<GazeQualitySummary> { Quality(0, 0.6, 0), Quality(Sec, 0.6, 0) };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(1.0); // mean invalid 0.6 ≥ high 0.5
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void PoorTracking_TimeBasedGate_FiresWithZeroInteractions()
    {
        // Summaries span 2 s with a high invalid fraction and NO dwell outcomes anywhere → gate passes
        // purely on elapsed time (§6.3: tracking loss produces no interactions but must still be detected).
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.6, 0),
            Quality(Sec, 0.6, 0),
            Quality(2 * Sec, 0.6, 0),
        };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void PoorTracking_MeanIsSampleCountWeighted()
    {
        // 90 valid samples then 10 samples at 0.4 invalid → true fraction 0.04 (< low 0.1) → score 0.
        // A naive per-summary mean would be 0.2 and score 0.25 — this asserts the weighting.
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.0, 0, sampleCount: 90),
            Quality(2 * Sec, 0.4, 0, sampleCount: 10),
        };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // 2 s span
    }

    [Fact]
    public void PoorTracking_MidRamp_IsLinear()
    {
        // Uniform invalid fraction 0.3 → midpoint of [0.1, 0.5] ramp → 0.5.
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.3, 0),
            Quality(Sec, 0.3, 0),
            Quality(2 * Sec, 0.3, 0),
        };
        Check.That(StruggleSignals.ScorePoorTracking(window).Score).IsCloseTo(0.5, 1e-9);
    }

    // An abandonment with a controlled NearMiss vector: TargetCenter=(0,0), GazePoint=(nmX,nmY),
    // so NearMissX/Y == nmX/nmY. radius feeds the aiming |NearMiss|÷radius ratio (0 = rect target).
    // Shared by the Aiming and Drift tests.
    private static DwellOutcome AbandonNM(
        long ts, DwellContext ctx, double radius, double nmX, double nmY, string id = "t") => new(
            ts, ctx, id, OutcomeKind.Abandoned, AbandonReason.GazeLeft, 0.5,
            new PhysicalPoint(nmX, nmY), new PhysicalPoint(0, 0), radius, null);

    // ---- Aiming ----

    [Fact]
    public void Aiming_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAiming(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Aiming_NoCircularAbandons_ReturnsNone()
    {
        // Only activations + a rect (radius 0) abandon → nothing usable → None.
        var window = new List<DwellOutcome>
        {
            Activate(0),
            AbandonNM(10 * Sec, DwellContext.ActionMenu, radius: 0, nmX: 90, nmY: 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Aiming_MeanRatioBelowLow_ScoreZero()
    {
        // radius 30, |NearMiss| 15 → ratio 0.5 (< low 1.0) → score 0.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 15, 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Aiming_MeanRatioAboveHigh_ScoreOne()
    {
        // radius 30, |NearMiss| 90 → ratio 3.0 (> high 2.5) → score 1.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 90, 0),
        };
        Check.That(StruggleSignals.ScoreAiming(window).Score).IsEqualTo(1.0);
    }

    [Fact]
    public void Aiming_MidRamp_IsLinear()
    {
        // radius 40, |NearMiss| 70 → ratio 1.75, midpoint of the [1.0, 2.5] ramp → 0.5.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 40, 70, 0),
        };
        Check.That(StruggleSignals.ScoreAiming(window).Score).IsCloseTo(0.5, 1e-9);
    }

    [Fact]
    public void Aiming_RectAndActivationsExcluded_OnlyCircularAbandonsCount()
    {
        // 4 circular abandons at ratio 0.5 → mean 0.5 → score 0. Two poison entries that must be ignored,
        // each chosen so INCLUSION would change the score (so asserting 0.0 discriminates BOTH exclusions):
        //   • an ACTIVATION carrying a large near-miss (150 px ÷ radius 30 = ratio 5) → if counted, mean
        //     rises to (2.0 + 5.0)/5 = 1.4 → score ≈ 0.27. Excluded by the Kind guard.
        //   • a RECT abandon (radius 0) → if counted, |NearMiss| ÷ 0 = +∞ drives the mean → score 1.0.
        //     Excluded by the radius > 0 guard.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            new DwellOutcome(4 * Sec, DwellContext.ActionMenu, "act", OutcomeKind.Activated,
                AbandonReason.None, 1.0, new PhysicalPoint(150, 0), new PhysicalPoint(0, 0), 30, null),
            AbandonNM(5 * Sec, DwellContext.ActionMenu, radius: 0, nmX: 500, nmY: 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // exactly 4 circular abandons meet the gate
    }

    [Fact]
    public void Aiming_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 circular abandons (< default 4) → gate false, confidence 0.5, score still computed (ratio 3 → 1).
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 90, 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }

    // ---- Drift ----

    [Fact]
    public void Drift_Empty_ReturnsZeroUngated()
    {
        var s = StruggleSignals.ScoreDrift(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Drift_SingleContextStrongBias_IsNotDrift()
    {
        // 8 abandons in ONE context all biased hard right. A single-context bias is UI geometry, NOT
        // calibration drift (§6.2/C2) → only 1 qualifying context → gate false, score 0.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 8; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, nmX: 80, nmY: 0));
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9); // 1 qualifying context / min 2
    }

    [Fact]
    public void Drift_TwoContextsCoherent_FiresDrift()
    {
        // Two contexts, 5 abandons each, both mean-biased (50,0). Coherent + magnitude 50 vs the 100 px
        // normalizer → coherence 1.0 × magNorm 0.5 = score 0.5.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsCloseTo(0.5, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Drift_TwoContextsIncoherent_GateFalse()
    {
        // Both contexts qualify (≥5 abandons) but bias 90° apart (right vs down) → not within ±30° →
        // no coherent pair → gate false, score 0. Confidence still 1.0 (two qualifying contexts).
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 0, 50));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Confidence).IsEqualTo(1.0);
    }

    [Fact]
    public void Drift_ContextBelowMinAbandons_DoesNotQualify()
    {
        // Context A: 5 coherent abandons. Context B: only 4 (< 5) → B does not qualify → 1 qualifying
        // context → gate false.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
        for (int i = 0; i < 4; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
    }

    [Fact]
    public void Drift_LargerBias_SaturatesScore()
    {
        // Same coherent-pair shape but magnitude 100 == normalizer → magNorm 1.0 → score 1.0.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 100, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 100, 0));
        }
        Check.That(StruggleSignals.ScoreDrift(window).Score).IsCloseTo(1.0, 1e-9);
    }

    [Fact]
    public void Drift_CoherentPairPlusIncoherentThird_StillFires()
    {
        // A and B coherent (50,0); C (0,50) is incoherent but qualifies. The coherent PAIR must still
        // trigger drift (score 0.5 from {A,B}); an outlier context does not block it.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Toolbar, 30, 0, 50));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsTrue();
        Check.That(s.Confidence).IsEqualTo(1.0);
    }

    // A gaze-quality summary carrying only a fatigue score (other fields 0 — unused by ScoreFatigue).
    private static GazeQualitySummary QualityFat(long ts, double fatigue) => new(ts, 0.0, 0.0, fatigue, 30);

    // ---- Fatigue (opt-in pass-through amplifier) ----

    [Fact]
    public void Fatigue_Disabled_ReturnsNone()
    {
        var window = new List<GazeQualitySummary> { QualityFat(0, 0.9), QualityFat(Sec, 0.9) };
        var s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: false);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Fatigue_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreFatigue(new List<GazeQualitySummary>(), fatigueMonitorEnabled: true);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Fatigue_Enabled_PassesThroughMeanScore()
    {
        var window = new List<GazeQualitySummary> { QualityFat(0, 0.2), QualityFat(Sec, 0.4), QualityFat(2 * Sec, 0.6) };
        var s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: true);
        Check.That(s.Score).IsCloseTo(0.4, 1e-9); // mean of 0.2/0.4/0.6
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }
}
