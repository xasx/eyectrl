namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// In-memory <see cref="IStruggleTelemetrySink"/> that retains the most recent outcomes in a bounded
/// ring buffer and drops any that arrive out of order (marshaling across threads can reorder
/// near-simultaneous events; design §9). <see cref="Report"/> never throws.
/// Serves as the single consumer behind the App-layer dispatcher sink until the StruggleMonitor
/// lands in Phase 1b.
/// <para>
/// Ordering is tracked <em>per <see cref="DwellContext"/></em>, not globally: surfaces do not share a
/// delivery path. Overlay view-models report synchronously on the UI thread, while
/// <c>ExternalDwellService</c> runs its detector on the gaze thread and is therefore marshaled
/// asynchronously — a global watermark would let one surface's queued outcome be discarded merely
/// because another surface reported a newer one first.
/// </para>
/// <para>
/// Not thread-safe: all <see cref="Report"/>, <see cref="ReportGazeQuality"/>, <see cref="Snapshot"/>
/// and <see cref="SnapshotQuality"/> calls must come from one thread (the UI dispatcher, which the
/// App-layer sink wrapper serializes onto).
/// </para>
/// </summary>
public sealed class BufferingTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly DwellOutcome[] _buffer;
    private int _count;
    private int _next;

    // Per-context high-water mark; index is the DwellContext value. Sized from the enum so adding a
    // context cannot silently fall back to a shared watermark.
    private readonly long[] _lastAcceptedByContext;

    private readonly GazeQualitySummary[] _qualityBuffer;
    private int _qualityCount;
    private int _qualityNext;
    private long _lastAcceptedQualityTimestamp = long.MinValue;

    /// <summary>Number of outcomes dropped because their timestamp preceded the last accepted one
    /// for the same <see cref="DwellContext"/>.</summary>
    public long DroppedOutOfOrder { get; private set; }

    /// <summary>Number of gaze-quality summaries dropped as out-of-order.</summary>
    public long DroppedOutOfOrderQuality { get; private set; }

    /// <summary>Number of times a backward timestamp jump was interpreted as a gaze-clock restart
    /// and the ordering watermarks were resynchronized instead of dropping the event.</summary>
    public long ClockRestarts { get; private set; }

    /// <summary>Initializes the sink with the given ring-buffer capacity.</summary>
    /// <param name="capacity">Maximum number of recent outcomes retained; must be positive.</param>
    public BufferingTelemetrySink(int capacity = 256)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _buffer = new DwellOutcome[capacity];
        _qualityBuffer = new GazeQualitySummary[capacity];

        int contextCount = 0;
        foreach (var c in Enum.GetValues<DwellContext>())
            contextCount = Math.Max(contextCount, (int)c + 1);
        _lastAcceptedByContext = new long[contextCount];
        Array.Fill(_lastAcceptedByContext, long.MinValue);
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        int slot = (int)outcome.Context;
        // Defensive: an out-of-range value can only come from an unchecked cast, but the sink must not
        // throw on the telemetry path (design §11). Fall back to the aggregate context.
        if (slot < 0 || slot >= _lastAcceptedByContext.Length) slot = (int)DwellContext.Other;

        long last = _lastAcceptedByContext[slot];
        if (outcome.TimestampMicroseconds < last)
        {
            if (last - outcome.TimestampMicroseconds < TelemetryThresholds.ClockRestartMicroseconds)
            {
                // Small backward step: genuine cross-thread reordering — drop it.
                DroppedOutOfOrder++;
                return;
            }
            // Large backward step: the gaze clock restarted (device reconnect). Accept and resync every
            // watermark, otherwise the pre-restart marks would reject the rest of the session.
            OnClockRestart();
        }
        _lastAcceptedByContext[slot] = outcome.TimestampMicroseconds;
        _buffer[_next] = outcome;
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    /// <summary>Returns the retained outcomes, oldest first.</summary>
    /// <returns>A snapshot list of retained outcomes.</returns>
    /// <remarks>
    /// Allocates a list sized to the retained count. Callers on the tick path should prefer
    /// <see cref="OutcomeCount"/> + <see cref="OutcomeAt"/>, which read the ring in place; at a full
    /// 256-entry ring this snapshot is ~35 KB of garbage per call, and the <c>StruggleMonitor</c> called it
    /// every tick only to copy the result into a reused buffer.
    /// </remarks>
    public IReadOnlyList<DwellOutcome> Snapshot()
    {
        var result = new List<DwellOutcome>(_count);
        for (int i = 0; i < _count; i++)
            result.Add(OutcomeAt(i));
        return result;
    }

    /// <summary>Number of retained dwell outcomes.</summary>
    public int OutcomeCount => _count;

    /// <summary>
    /// The retained outcome at <paramref name="index"/> in oldest-first order (0 = oldest), read directly
    /// from the ring — the allocation-free counterpart to <see cref="Snapshot"/>. Returned by readonly
    /// reference to avoid copying the struct on the tick path.
    /// <para>
    /// The reference points into the live ring, so it is valid only until the next <see cref="Report"/>
    /// (which may overwrite that slot) — copy the value out rather than holding the reference. Both calls
    /// come from the same serialized thread (§9), so this is a discipline, not a race.
    /// </para>
    /// </summary>
    /// <param name="index">Zero-based index into the retained outcomes, oldest first.</param>
    /// <returns>A readonly reference to the outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside
    /// <c>[0, <see cref="OutcomeCount"/>)</c>.</exception>
    public ref readonly DwellOutcome OutcomeAt(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the retained outcomes.");
        int start = (_next - _count + _buffer.Length) % _buffer.Length;
        return ref _buffer[(start + index) % _buffer.Length];
    }

    /// <inheritdoc />
    public void ReportGazeQuality(in GazeQualitySummary summary)
    {
        if (summary.TimestampMicroseconds < _lastAcceptedQualityTimestamp)
        {
            if (_lastAcceptedQualityTimestamp - summary.TimestampMicroseconds
                < TelemetryThresholds.ClockRestartMicroseconds)
            {
                DroppedOutOfOrderQuality++;
                return;
            }
            OnClockRestart();
        }
        _lastAcceptedQualityTimestamp = summary.TimestampMicroseconds;
        _qualityBuffer[_qualityNext] = summary;
        _qualityNext = (_qualityNext + 1) % _qualityBuffer.Length;
        if (_qualityCount < _qualityBuffer.Length) _qualityCount++;
    }

    // A restarted gaze clock invalidates BOTH the watermarks and everything already retained: pre-restart
    // timestamps are not comparable with post-restart ones, so keeping them would make Snapshot() emit a
    // descending step. Consumers rely on the ordering — StruggleSignals documents an oldest-first,
    // non-decreasing window and DEBUG-asserts it, and its early-break pruning and span gates silently
    // mis-score otherwise. Dropping the stale evidence is also semantically right: a reconnect starts a new
    // session, and evidence from before it belongs to the old one.
    private void OnClockRestart()
    {
        ClockRestarts++;
        Array.Fill(_lastAcceptedByContext, long.MinValue);
        _lastAcceptedQualityTimestamp = long.MinValue;
        _count = 0;
        _next = 0;
        _qualityCount = 0;
        _qualityNext = 0;
    }

    /// <summary>Returns the retained gaze-quality summaries, oldest first.</summary>
    /// <returns>A snapshot list of retained summaries.</returns>
    /// <remarks>Allocates; see the note on <see cref="Snapshot"/>. Tick-path callers should prefer
    /// <see cref="QualityCount"/> + <see cref="QualityAt"/>.</remarks>
    public IReadOnlyList<GazeQualitySummary> SnapshotQuality()
    {
        var result = new List<GazeQualitySummary>(_qualityCount);
        for (int i = 0; i < _qualityCount; i++)
            result.Add(QualityAt(i));
        return result;
    }

    /// <summary>Number of retained gaze-quality summaries.</summary>
    public int QualityCount => _qualityCount;

    /// <summary>
    /// The retained summary at <paramref name="index"/> in oldest-first order (0 = oldest), read directly
    /// from the ring — the allocation-free counterpart to <see cref="SnapshotQuality"/>.
    /// </summary>
    /// <param name="index">Zero-based index into the retained summaries, oldest first.</param>
    /// <returns>A readonly reference to the summary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside
    /// <c>[0, <see cref="QualityCount"/>)</c>.</exception>
    public ref readonly GazeQualitySummary QualityAt(int index)
    {
        if ((uint)index >= (uint)_qualityCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the retained summaries.");
        int start = (_qualityNext - _qualityCount + _qualityBuffer.Length) % _qualityBuffer.Length;
        return ref _qualityBuffer[(start + index) % _qualityBuffer.Length];
    }
}
