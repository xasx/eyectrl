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
}
