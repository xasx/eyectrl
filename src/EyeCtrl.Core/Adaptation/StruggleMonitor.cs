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
    private long _sessionStart = long.MinValue;

    /// <summary>Initializes the monitor over an existing telemetry sink.</summary>
    /// <param name="sink">The 1a buffering sink to read (dwell outcomes + gaze-quality summaries).</param>
    /// <param name="eyeCare">Eye-care settings; only <see cref="EyeCareSettings.FatigueMonitorEnabled"/> is read (live, in <c>Assess</c>), gating the fatigue amplifier.</param>
    /// <param name="evidenceMaxAgeMicroseconds">Rolling evidence-window length. Default 60 000 000 (60 s, §13.1).</param>
    /// <param name="idleResetMicroseconds">A gaze-idle gap this long (inferred from the gaze-quality stream) resets the session boundary. Default 120 000 000 (120 s, §13.1).</param>
    /// <param name="backspaceTargetId">Keyboard Backspace target id for the accidental-activation corroboration; null disables it (wired by the App later).</param>
    public StruggleMonitor(
        BufferingTelemetrySink sink,
        EyeCareSettings eyeCare,
        long evidenceMaxAgeMicroseconds = 60_000_000,
        long idleResetMicroseconds = 120_000_000,
        string? backspaceTargetId = null)
    {
        _sink = sink;
        _eyeCare = eyeCare; // read in Assess (Task 3 fatigue gate)
        _evidenceMaxAgeMicroseconds = evidenceMaxAgeMicroseconds;
        _idleResetMicroseconds = idleResetMicroseconds;
        _backspaceTargetId = backspaceTargetId; // read in Assess (Task 2)
    }

    /// <summary>
    /// Starts a fresh momentary session at <paramref name="nowMicroseconds"/>: evidence at or before it
    /// is ignored by the next <c>Assess</c> (even if younger than the rolling window). Invoked by
    /// the coordinator on app-start and on resume from an explicit Pause (§13.1). No buffer mutation —
    /// only the boundary moves.
    /// </summary>
    /// <param name="nowMicroseconds">The session-start timestamp, in microseconds.</param>
    public void ResetSession(long nowMicroseconds) => _sessionStart = nowMicroseconds;

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
