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

        // Collected into a reused buffer, then copied into an exactly-sized list only if any mode surfaced.
        // The no-struggle tick — the overwhelmingly common one — therefore allocates nothing at all.
        int modeCount = 0;
        // §13.3: every mode is single-feeder. SustainFailure is abandonment(+retries, folded in ③a) alone;
        // jitter is SignalNoise. Tolerance's drive is unchanged because the controller takes the peak across
        // both modes — max(max(a, j), drift) == max(a, j, drift) — see §13.3.
        AddMode(ref modeCount, FailureMode.SustainFailure, abandonment);
        AddMode(ref modeCount, FailureMode.AccidentalActivation, accidental);
        AddMode(ref modeCount, FailureMode.Aiming, aiming);
        AddMode(ref modeCount, FailureMode.Drift, drift);
        AddMode(ref modeCount, FailureMode.PoorTracking, poorTracking);
        AddMode(ref modeCount, FailureMode.SignalNoise, jitter);

        if (modeCount == 0)
            return StruggleAssessment.None;

        var modes = new List<FailureModeAssessment>(modeCount);
        for (int i = 0; i < modeCount; i++)
            modes.Add(_modeBuffer[i]);

        // Fatigue is a pass-through AMPLIFIER (§13.2), not a failure mode: it scales the actionable
        // struggle level but cannot create struggle from nothing (base 0 → 0). Opt-in via EyeCare.
        double baseLevel = PeakGatedScore(modes);
        // The amplifier is subject to F4 like every other signal: it raises the actionable aggregate (and
        // cue intensity), so below-gate fatigue evidence must not drive it.
        double fatigueLevel = fatigue.GatePassed ? fatigue.Score : 0.0;
        double aggregate = Math.Clamp(baseLevel * (1.0 + _fatigueAmplifyWeight * fatigueLevel), 0.0, 1.0);
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
    //
    // Two explicit arities instead of `params SignalScore[]`: every mode has one or two feeders, and a params
    // array allocated five times per tick is pure waste on a 4 Hz dispatcher path.
    private void AddMode(ref int count, FailureMode mode, SignalScore only) =>
        AddMode(ref count, mode, only, SignalScore.None);

    private void AddMode(ref int count, FailureMode mode, SignalScore first, SignalScore second)
    {
        if (ClassifyMode(mode, first, second) is { } assessment)
            _modeBuffer[count++] = assessment;
    }

    /// <summary>
    /// The pure fold from one or two feeding signals to a mode assessment, or <c>null</c> when the mode is
    /// omitted because no feeder shows any struggle. Extracted from <c>AddMode</c> so the multi-feeder fold
    /// contract stays directly testable (design §13.3): after the `SignalNoise` split every mode in the live
    /// map is single-feeder, so the two-feeder path is no longer exercised incidentally through
    /// <see cref="Assess"/>. <c>internal</c> for <c>EyeCtrl.Core.Tests</c> (the `InternalsVisibleTo` seam in
    /// `EyeCtrl.Core.csproj`); production callers go through <c>AddMode</c>.
    /// </summary>
    /// <param name="mode">The failure mode being classified.</param>
    /// <param name="first">The first feeding signal.</param>
    /// <param name="second">The second feeding signal, or <see cref="SignalScore.None"/> for a single-feeder mode.</param>
    /// <returns>The mode assessment, or <c>null</c> when the mode should be omitted entirely.</returns>
    internal static FailureModeAssessment? ClassifyMode(FailureMode mode, SignalScore first, SignalScore second)
    {
        double rawScore = Math.Max(first.Score, second.Score);
        if (rawScore <= 0.0)
            return null; // no feeder shows any struggle → omit (ForMode returns null)

        double gatedScore = 0.0, gatedConfidence = 0.0;
        bool anyGated = false;
        Fold(first, ref gatedScore, ref gatedConfidence, ref anyGated);
        Fold(second, ref gatedScore, ref gatedConfidence, ref anyGated);

        // Actionable score/confidence come from gated feeders; if none gated the mode surfaces with the
        // (non-actionable) raw score and GatePassed=false, so the controller ignores it but monitoring sees it.
        double score = anyGated ? gatedScore : rawScore;
        double confidence = anyGated ? gatedConfidence : 0.0;
        return new FailureModeAssessment(mode, score, confidence, anyGated, null);
    }

    // SignalScore.None is gate-failed with score 0, so folding it as the absent second feeder is a no-op.
    private static void Fold(SignalScore s, ref double gatedScore, ref double gatedConfidence, ref bool anyGated)
    {
        if (!s.GatePassed)
            return;
        anyGated = true;
        gatedScore = Math.Max(gatedScore, s.Score);
        gatedConfidence = Math.Max(gatedConfidence, s.Confidence);
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
        // Gaze-clock regression guard. `now` is a gaze timestamp (the tracker clock is monotonic but has an
        // arbitrary epoch), and the coordinator's session resets stamp _sessionStart from whatever the clock
        // read at the time. A reconnect that restarts the clock lower therefore leaves _sessionStart pinned
        // ABOVE every subsequent `now`, so the window below would select nothing — permanently, because
        // nothing resets on the reconnect edge (only on disconnect/enable/resume).
        //
        // Drop the boundary rather than move it to `now`: the old value is expressed in a clock that no
        // longer exists, and re-anchoring to `now` would discard the post-restart evidence that has already
        // arrived (it is older than `now`, though only by milliseconds). Unanchored, the rolling max-age
        // window governs — which is the correct behaviour on a fresh clock. Pre-restart evidence cannot leak
        // in: BufferingTelemetrySink purges its buffers on the same restart detection.
        if (now < _sessionStart)
            _sessionStart = long.MinValue;

        long start = Math.Max(_sessionStart, now - _evidenceMaxAgeMicroseconds);

        // Idle-gap auto-reset (§13.1): a gap > idleReset in the continuous ~2 Hz gaze-quality stream marks
        // an idle period; evidence before it belongs to a prior session. Belt-and-suspenders under the
        // default 60 s max-age (a >120 s gap cannot fit a 60 s window) but spec-required and correct.
        long idleBoundary = LatestIdleBoundary(now);
        if (idleBoundary > start)
            start = idleBoundary;

        // Filtered straight out of the sink's ring into the reused buffers. Snapshot()/SnapshotQuality()
        // would each allocate a list first (~35 KB per tick at a full ring) only to be copied here and
        // dropped — the whole point of the reused buffers.
        _dwellWindow.Clear();
        for (int i = 0; i < _sink.OutcomeCount; i++)
        {
            ref readonly DwellOutcome o = ref _sink.OutcomeAt(i);
            if (o.TimestampMicroseconds > now)
                break; // ordered → nothing later is ≤ now
            if (o.TimestampMicroseconds >= start)
                _dwellWindow.Add(o);
        }

        _qualityWindow.Clear();
        for (int i = 0; i < _sink.QualityCount; i++)
        {
            ref readonly GazeQualitySummary q = ref _sink.QualityAt(i);
            if (q.TimestampMicroseconds > now)
                break;
            if (q.TimestampMicroseconds >= start)
                _qualityWindow.Add(q);
        }

        return (_dwellWindow, _qualityWindow);
    }

    // Timestamp of the summary immediately AFTER the latest idle gap (> idleReset) in the ordered stream,
    // or long.MinValue if none. Ordered ascending, so a later gap overwrites an earlier one. Reads the
    // sink's ring in place — this runs before the quality window is filtered, and needs the whole stream
    // anyway (a gap that starts before `start` still moves the boundary).
    private long LatestIdleBoundary(long now)
    {
        long boundary = long.MinValue;
        long prev = long.MinValue;
        bool hasPrev = false;
        for (int i = 0; i < _sink.QualityCount; i++)
        {
            long ts = _sink.QualityAt(i).TimestampMicroseconds;
            if (ts > now)
                break; // ordered → nothing later is ≤ now
            if (hasPrev && ts - prev > _idleResetMicroseconds)
                boundary = ts;
            prev = ts;
            hasPrev = true;
        }
        return boundary;
    }

    // Reused across ticks so the filter does not allocate a fresh list every tick (single-threaded: Assess
    // runs only on the dispatcher). The scorers neither retain nor mutate the windows they are handed —
    // they only read within the call — so reusing the backing lists is safe. Assess's returned
    // StruggleAssessment holds a separately-allocated mode list, not these. They reach steady-state
    // capacity within the first few ticks and then never reallocate.
    private readonly List<DwellOutcome> _dwellWindow = new();
    private readonly List<GazeQualitySummary> _qualityWindow = new();

    // Reused per-tick staging for the classified modes (at most one per FailureMode), copied into an
    // exactly-sized list only when at least one mode surfaced. Sized from the enum so adding a failure mode
    // cannot silently overflow it.
    private readonly FailureModeAssessment[] _modeBuffer =
        new FailureModeAssessment[Enum.GetValues<FailureMode>().Length];
}
