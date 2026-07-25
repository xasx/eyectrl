namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Reads the 1a <see cref="BufferingTelemetrySink"/>, filters its snapshots to the momentary evidence
/// window, runs the seven §13.2 signal scorers (<see cref="StruggleSignals"/>), classifies them into
/// per-<see cref="FailureMode"/> assessments, and produces a <see cref="StruggleAssessment"/> per
/// controller tick (design §13.2). Reuses the sink's bounded ring + monotonic guard rather than
/// reimplementing a sink. Single-threaded: <c>Assess</c>/<see cref="ResetSession"/> run on the
/// dispatcher thread — the same serialized thread the sink is fed on (§9) — so no locking is needed.
/// </summary>
public sealed class StruggleMonitor
{
    private readonly BufferingTelemetrySink _sink;
    private readonly EyeCareSettings _eyeCare;
    private readonly long _evidenceMaxAgeMicroseconds;
    private readonly long _idleResetMicroseconds;
    private readonly string? _backspaceTargetId;
    private readonly double _fatigueAmplifyWeight;
    private long _sessionStart = long.MinValue;

    /// <summary>Initializes the monitor over an existing telemetry sink.</summary>
    /// <param name="sink">The 1a buffering sink to read (dwell outcomes + gaze-quality summaries).</param>
    /// <param name="eyeCare">Eye-care settings; only <see cref="EyeCareSettings.FatigueMonitorEnabled"/> is read (live, in <c>Assess</c>), gating the fatigue amplifier.</param>
    /// <param name="evidenceMaxAgeMicroseconds">Rolling evidence-window length. Default 60 000 000 (60 s, §13.1).</param>
    /// <param name="idleResetMicroseconds">A gaze-idle gap this long (inferred from the gaze-quality stream) resets the session boundary. Default 120 000 000 (120 s, §13.1).</param>
    /// <param name="backspaceTargetId">Keyboard Backspace target id for the accidental-activation corroboration; null disables it (wired by the App later).</param>
    /// <param name="fatigueAmplifyWeight">How strongly the fatigue level amplifies the aggregate (multiplicative). Default 0.5 — a tunable starting value.</param>
    public StruggleMonitor(
        BufferingTelemetrySink sink,
        EyeCareSettings eyeCare,
        long evidenceMaxAgeMicroseconds = 60_000_000,
        long idleResetMicroseconds = 120_000_000,
        string? backspaceTargetId = null,
        double fatigueAmplifyWeight = 0.5)
    {
        _sink = sink;
        _eyeCare = eyeCare; // read in Assess (Task 3 fatigue gate)
        _evidenceMaxAgeMicroseconds = evidenceMaxAgeMicroseconds;
        _idleResetMicroseconds = idleResetMicroseconds;
        _backspaceTargetId = backspaceTargetId; // read in Assess (Task 2)
        _fatigueAmplifyWeight = fatigueAmplifyWeight;
    }

    /// <summary>
    /// Starts a fresh momentary session at <paramref name="nowMicroseconds"/>: evidence at or before it
    /// is ignored by the next <c>Assess</c> (even if younger than the rolling window). Invoked by
    /// the coordinator on app-start and on resume from an explicit Pause (§13.1). No buffer mutation —
    /// only the boundary moves.
    /// </summary>
    /// <param name="nowMicroseconds">The session-start timestamp, in microseconds.</param>
    public void ResetSession(long nowMicroseconds) => _sessionStart = nowMicroseconds;

    /// <summary>
    /// Assesses momentary struggle as of <paramref name="nowMicroseconds"/>: selects the in-window
    /// evidence, runs the signal scorers, classifies them into per-failure-mode assessments (context =
    /// null — the Phase-1b momentary response is global per knob, §7.2), and computes the aggregate level.
    /// </summary>
    /// <param name="nowMicroseconds">The current controller-tick timestamp, in microseconds.</param>
    /// <returns>The struggle assessment for this tick, or <see cref="StruggleAssessment.None"/> when no mode shows struggle.</returns>
    public StruggleAssessment Assess(long nowMicroseconds)
    {
        var (dwell, quality) = SelectEvidence(nowMicroseconds);

        SignalScore abandonment = StruggleSignals.ScoreAbandonment(dwell);
        SignalScore accidental = StruggleSignals.ScoreAccidental(dwell, backspaceTargetId: _backspaceTargetId);
        SignalScore jitter = StruggleSignals.ScoreJitter(quality);
        SignalScore poorTracking = StruggleSignals.ScorePoorTracking(quality);
        SignalScore aiming = StruggleSignals.ScoreAiming(dwell);
        SignalScore drift = StruggleSignals.ScoreDrift(dwell);
        SignalScore fatigue = StruggleSignals.ScoreFatigue(quality, _eyeCare.FatigueMonitorEnabled);

        var modes = new List<FailureModeAssessment>(5);
        AddMode(modes, FailureMode.SustainFailure, abandonment, jitter); // §13.2: abandonment(+retries, folded in ③a) + jitter
        AddMode(modes, FailureMode.AccidentalActivation, accidental);
        AddMode(modes, FailureMode.Aiming, aiming);
        AddMode(modes, FailureMode.Drift, drift);
        AddMode(modes, FailureMode.PoorTracking, poorTracking);

        if (modes.Count == 0)
            return StruggleAssessment.None;

        // Fatigue is a pass-through AMPLIFIER (§13.2), not a failure mode: it scales the actionable
        // struggle level but cannot create struggle from nothing (base 0 → 0). Opt-in via EyeCare.
        double baseLevel = PeakGatedScore(modes);
        double aggregate = Math.Clamp(baseLevel * (1.0 + _fatigueAmplifyWeight * fatigue.Score), 0.0, 1.0);
        return new StruggleAssessment(modes, aggregate);
    }

    // Adds a per-mode assessment from its feeding signals. The mode is GATED iff at feeder gated, and the
    // mode's actionable SCORE/CONFIDENCE come from the GATED feeders only — a high score on an UNGATED
    // feeder must never drive adaptation (F4: below-gate evidence never drives). Concretely, a gated-but-
    // zero-score feeder (e.g. zero-jitter meeting the volume gate) combined with an ungated-but-high-score
    // feeder (e.g. below-gate abandonment) yields a gated mode whose score is 0 (no gated evidence) rather
    // than 0.8 (the ungated evidence) — so it surfaces but does not act. Score = PEAK among gated feeders
    // (not a sum: correlated feeders must not double-count); Confidence = peak gated-feeder confidence.
    // Context is null (the Phase-1b momentary response is global per knob, §7.2). A mode with no gated
    // feeder AND no score is omitted entirely; a mode with score>0 but no gated feeder is surfaced
    // (GatePassed=false) so monitoring can show "warming up" without acting on it.
    private static void AddMode(List<FailureModeAssessment> modes, FailureMode mode, params SignalScore[] feeders)
    {
        double gatedScore = 0.0, gatedConfidence = 0.0;
        double rawScore = 0.0;
        foreach (SignalScore s in feeders)
        {
            rawScore = Math.Max(rawScore, s.Score);
            if (s.GatePassed)
            {
                gatedScore = Math.Max(gatedScore, s.Score);
                gatedConfidence = Math.Max(gatedConfidence, s.Confidence);
            }
        }
        if (rawScore <= 0.0)
            return; // no feeder shows any struggle → omit (ForMode returns null)
        bool anyGated = Array.Exists(feeders, static s => s.GatePassed);
        // Actionable score/confidence come from gated feeders; if none gated the mode surfaces with the
        // (non-actionable) raw score and GatePassed=false, so the controller ignores it but monitoring sees it.
        double score = anyGated ? gatedScore : rawScore;
        double confidence = anyGated ? gatedConfidence : 0.0;
        modes.Add(new FailureModeAssessment(mode, score, confidence, anyGated, null));
    }

    // Peak struggle among GATED modes (the actionable ones). Uses gated SCORES (not confidences), so a
    // single biased surface cannot inflate it — Drift scores 0 unless genuinely cross-context-coherent
    // (F4, §6.2/C2). Base for the (Task-3) fatigue-amplified aggregate.
    private static double PeakGatedScore(List<FailureModeAssessment> modes)
    {
        double peak = 0.0;
        foreach (FailureModeAssessment m in modes)
            if (m.GatePassed)
                peak = Math.Max(peak, m.Score);
        return peak;
    }

    // Selects the evidence in [start, now], start = max(sessionStart, now − maxAge, idle-boundary). The
    // sink guarantees oldest-first, non-decreasing timestamps (§9); filtering preserves that order, so the
    // scorers' DEBUG AssertOrdered holds. No buffer mutation.
    internal (IReadOnlyList<DwellOutcome> Dwell, IReadOnlyList<GazeQualitySummary> Quality) SelectEvidence(long now)
    {
        IReadOnlyList<DwellOutcome> dwell = _sink.Snapshot();
        IReadOnlyList<GazeQualitySummary> quality = _sink.SnapshotQuality();

        long start = Math.Max(_sessionStart, now - _evidenceMaxAgeMicroseconds);

        // Idle-gap auto-reset (§13.1): a gap > idleReset in the continuous ~2 Hz gaze-quality stream marks
        // an idle period; evidence before it belongs to a prior session. Belt-and-suspenders under the
        // default 60 s max-age (a >120 s gap cannot fit a 60 s window) but spec-required and correct.
        long idleBoundary = LatestIdleBoundary(quality, now);
        if (idleBoundary > start)
            start = idleBoundary;

        return (Within(dwell, start, now, static o => o.TimestampMicroseconds),
                Within(quality, start, now, static q => q.TimestampMicroseconds));
    }

    // Timestamp of the summary immediately AFTER the latest idle gap (> idleReset) in the ordered stream,
    // or long.MinValue if none. Ordered ascending, so a later gap overwrites an earlier one.
    private long LatestIdleBoundary(IReadOnlyList<GazeQualitySummary> quality, long now)
    {
        long boundary = long.MinValue;
        long prev = long.MinValue;
        bool hasPrev = false;
        for (int i = 0; i < quality.Count; i++)
        {
            long ts = quality[i].TimestampMicroseconds;
            if (ts > now)
                break; // ordered → nothing later is ≤ now
            if (hasPrev && ts - prev > _idleResetMicroseconds)
                boundary = ts;
            prev = ts;
            hasPrev = true;
        }
        return boundary;
    }

    // Order-preserving filter to [start, now]. Ordered input → break once past now.
    private static IReadOnlyList<T> Within<T>(IReadOnlyList<T> items, long start, long now, Func<T, long> timestamp)
    {
        var result = new List<T>();
        for (int i = 0; i < items.Count; i++)
        {
            long ts = timestamp(items[i]);
            if (ts > now)
                break;
            if (ts >= start)
                result.Add(items[i]);
        }
        return result;
    }
}
