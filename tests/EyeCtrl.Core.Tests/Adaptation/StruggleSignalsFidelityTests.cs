namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

/// <summary>
/// Guards against the detector confidently mis-reading ordinary use — the false-positive cases found in
/// review of PR #71. These matter more than missed detections: accidental-activation is the only signal
/// that TIGHTENS (longer dwell), and drift raises a user-visible re-calibration suggestion.
/// </summary>
public class StruggleSignalsFidelityTests
{
    private const long Sec = 1_000_000;

    private static DwellOutcome Activate(long ts, DwellContext ctx, string id, double x, double y, double radius) =>
        new(ts, ctx, id, OutcomeKind.Activated, AbandonReason.None, 1.0,
            new PhysicalPoint(x, y), new PhysicalPoint(x, y), radius, null);

    private static DwellOutcome Abandon(long ts, DwellContext ctx, string id, AbandonReason reason,
        double gazeX, double gazeY, double centerX, double centerY, double radius) =>
        new(ts, ctx, id, OutcomeKind.Abandoned, reason, 0.5,
            new PhysicalPoint(gazeX, gazeY), new PhysicalPoint(centerX, centerY), radius, null);

    // ── Accidental activation: repetition is not correction ────────────────────────────────────────

    [Fact]
    public void Accidental_SameKeyTypedTwice_IsNotAReversal()
    {
        // The "ll" in "hello": two activations of the SAME key, 0.5 s apart. Keyboard outcomes carry
        // radius 0, so the re-approach cone is the 40 px floor and a same-target repeat sits at distance 0 —
        // which used to score as a reversal and drive the dwell-lengthening response.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.Keyboard, "l", 100, 100, 0),
            Activate(500_000, DwellContext.Keyboard, "l", 100, 100, 0),
            Activate(10 * Sec, DwellContext.Keyboard, "o", 300, 100, 0),
        };

        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_RepeatedSameButtonPresses_AreNotReversals()
    {
        // Holding a scroll-down / arrow button by re-dwelling it repeatedly is deliberate repetition.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
            window.Add(Activate(i * 400_000, DwellContext.EdgeButtonBar, "scroll-down", 900, 500, 40));

        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_DifferentNearbyTarget_StillCountsAsReversal()
    {
        // The fix must not blunt the real signal: a correction lands on a DIFFERENT nearby target.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.Keyboard, "k", 100, 100, 0),
            Activate(500_000, DwellContext.Keyboard, "l", 120, 100, 0), // 20 px ≤ 40 floor, different key
            Activate(10 * Sec, DwellContext.Keyboard, "z", 900, 100, 0),
        };

        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsCloseTo(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Accidental_SameTargetRepeat_DoesNotMaskALaterGenuineReversal()
    {
        // A same-target repeat must be skipped, not treated as "already counted" — the scan has to keep
        // looking for a genuine different-target reversal inside the window.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.Keyboard, "l", 100, 100, 0),
            Activate(300_000, DwellContext.Keyboard, "l", 100, 100, 0),  // repeat — skipped
            Activate(600_000, DwellContext.Keyboard, "k", 120, 100, 0),  // genuine correction
            Activate(10 * Sec, DwellContext.Keyboard, "z", 900, 100, 0),
        };

        // The first activation finds the "k" reversal; the second finds it too (also within 1.5 s).
        Check.That(StruggleSignals.ScoreAccidental(window).Score > 0.0).IsTrue();
    }

    // ── Aiming / Drift: NearMiss only measures aim for a GazeLeft abandon ──────────────────────────

    [Fact]
    public void Aiming_IgnoresNearestChangedAbandons()
    {
        // NearestChanged means gaze already landed on ANOTHER target, so the near-miss vector is the UI's
        // target spacing, not an aiming error. 300 px off a radius-30 target would otherwise ramp to 1.0.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 6; i++)
            window.Add(Abandon(i * Sec, DwellContext.ActionMenu, "btn", AbandonReason.NearestChanged,
                gazeX: 400, gazeY: 100, centerX: 100, centerY: 100, radius: 30));

        SignalScore s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse(); // no qualifying abandons at all → SignalScore.None
    }

    [Fact]
    public void Aiming_StillScoresGazeLeftAbandons()
    {
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 6; i++)
            window.Add(Abandon(i * Sec, DwellContext.ActionMenu, "btn", AbandonReason.GazeLeft,
                gazeX: 190, gazeY: 100, centerX: 100, centerY: 100, radius: 30)); // ratio 3.0 > highRatio

        SignalScore s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Drift_IgnoresSharedScanDirectionFromNearestChanged()
    {
        // A user scanning two different surfaces left→right produces a coherent +X near-miss bias in both
        // contexts purely from NearestChanged hops. That is exactly the cross-context coherence Drift reads
        // as a calibration slip, and the multi-context guard cannot reject it. It must not gate.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 6; i++)
        {
            window.Add(Abandon(i * Sec, DwellContext.Toolbar, $"t{i}", AbandonReason.NearestChanged,
                gazeX: 200, gazeY: 50, centerX: 100, centerY: 50, radius: 40));
            window.Add(Abandon(i * Sec + 100_000, DwellContext.QuickActions, $"q{i}", AbandonReason.NearestChanged,
                gazeX: 600, gazeY: 400, centerX: 500, centerY: 400, radius: 40));
        }

        SignalScore s = StruggleSignals.ScoreDrift(window);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Drift_StillGatesOnCoherentGazeLeftBiasAcrossContexts()
    {
        // The genuine signature: a consistent +X bias across two contexts on GazeLeft abandons.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 6; i++)
        {
            window.Add(Abandon(i * Sec, DwellContext.Toolbar, $"t{i}", AbandonReason.GazeLeft,
                gazeX: 200, gazeY: 50, centerX: 100, centerY: 50, radius: 40));
            window.Add(Abandon(i * Sec + 100_000, DwellContext.QuickActions, $"q{i}", AbandonReason.GazeLeft,
                gazeX: 600, gazeY: 400, centerX: 500, centerY: 400, radius: 40));
        }

        SignalScore s = StruggleSignals.ScoreDrift(window);
        Check.That(s.GatePassed).IsTrue();
        Check.That(s.Score > 0.0).IsTrue();
    }

    // ── Fatigue amplifier: volume-gated like every other scorer ────────────────────────────────────

    [Fact]
    public void Fatigue_BelowVolumeGate_IsNotGated()
    {
        var window = new List<GazeQualitySummary> { new(0, 0.0, 10, 0.9, 30) };

        SignalScore s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: true);
        Check.That(s.Score).IsEqualTo(0.9); // still reported for monitoring
        Check.That(s.GatePassed).IsFalse(); // but not actionable off one window
    }

    [Fact]
    public void Fatigue_AtVolumeGate_IsGated()
    {
        var window = new List<GazeQualitySummary>
        {
            new(0, 0.0, 10, 0.9, 30),
            new(500_000, 0.0, 10, 0.9, 30),
            new(1_000_000, 0.0, 10, 0.9, 30),
        };

        SignalScore s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: true);
        Check.That(s.GatePassed).IsTrue();
        Check.That(s.Score).IsCloseTo(0.9, 1e-9);
    }
}
