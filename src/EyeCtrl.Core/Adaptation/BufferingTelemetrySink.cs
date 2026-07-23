namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// In-memory <see cref="IStruggleTelemetrySink"/> that retains the most recent outcomes in a bounded
/// ring buffer and drops any whose timestamp precedes the last accepted one (marshaling across
/// threads can reorder near-simultaneous events; design §9). <see cref="Report"/> never throws.
/// Serves as the single consumer behind the App-layer dispatcher sink until the StruggleMonitor
/// lands in Phase 1b.
/// </summary>
public sealed class BufferingTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly DwellOutcome[] _buffer;
    private int _count;
    private int _next;
    private long _lastAcceptedTimestamp = long.MinValue;

    private readonly GazeQualitySummary[] _qualityBuffer;
    private int _qualityCount;
    private int _qualityNext;
    private long _lastAcceptedQualityTimestamp = long.MinValue;

    /// <summary>Number of outcomes dropped because their timestamp preceded the last accepted one.</summary>
    public long DroppedOutOfOrder { get; private set; }

    /// <summary>Number of gaze-quality summaries dropped as out-of-order.</summary>
    public long DroppedOutOfOrderQuality { get; private set; }

    /// <summary>Initializes the sink with the given ring-buffer capacity.</summary>
    /// <param name="capacity">Maximum number of recent outcomes retained; must be positive.</param>
    public BufferingTelemetrySink(int capacity = 256)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _buffer = new DwellOutcome[capacity];
        _qualityBuffer = new GazeQualitySummary[capacity];
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        if (outcome.TimestampMicroseconds < _lastAcceptedTimestamp)
        {
            DroppedOutOfOrder++;
            return;
        }
        _lastAcceptedTimestamp = outcome.TimestampMicroseconds;
        _buffer[_next] = outcome;
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    /// <summary>Returns the retained outcomes, oldest first.</summary>
    /// <returns>A snapshot list of retained outcomes.</returns>
    public IReadOnlyList<DwellOutcome> Snapshot()
    {
        var result = new List<DwellOutcome>(_count);
        int start = (_next - _count + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < _count; i++)
            result.Add(_buffer[(start + i) % _buffer.Length]);
        return result;
    }

    /// <inheritdoc />
    public void ReportGazeQuality(in GazeQualitySummary summary)
    {
        if (summary.TimestampMicroseconds < _lastAcceptedQualityTimestamp)
        {
            DroppedOutOfOrderQuality++;
            return;
        }
        _lastAcceptedQualityTimestamp = summary.TimestampMicroseconds;
        _qualityBuffer[_qualityNext] = summary;
        _qualityNext = (_qualityNext + 1) % _qualityBuffer.Length;
        if (_qualityCount < _qualityBuffer.Length) _qualityCount++;
    }

    /// <summary>Returns the retained gaze-quality summaries, oldest first.</summary>
    /// <returns>A snapshot list of retained summaries.</returns>
    public IReadOnlyList<GazeQualitySummary> SnapshotQuality()
    {
        var result = new List<GazeQualitySummary>(_qualityCount);
        int start = (_qualityNext - _qualityCount + _qualityBuffer.Length) % _qualityBuffer.Length;
        for (int i = 0; i < _qualityCount; i++)
            result.Add(_qualityBuffer[(start + i) % _qualityBuffer.Length]);
        return result;
    }
}
