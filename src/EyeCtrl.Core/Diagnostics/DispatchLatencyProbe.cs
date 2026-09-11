namespace EyeCtrl.Core.Diagnostics;

using System.Diagnostics;

/// <summary>
/// Instrument 1: measures how long a gaze sample waits between being posted to the UI dispatcher and
/// actually running there, plus a proxy for how deep the queue got.
/// <para>
/// The question it settles is pre-registered and falsifiable. <c>GazeEngineAdapter</c> posts one
/// uncoalesced <c>BeginInvoke</c> per sample at Normal priority; the hypothesis is that under UI load
/// the queue grows and then drains as a burst of stale positions — jitter with no signal-processing
/// cause at all. If p95 enqueue→execute turns out to be under one frame (~16 ms), the coalescing
/// proposal is WITHDRAWN rather than implemented. The instrument exists to be capable of killing the
/// idea it was built for.
/// </para>
/// <para>
/// Measurement only: it changes nothing about how or when anything is dispatched.
/// </para>
/// <para>
/// Threading: <see cref="Enqueue"/> runs on the gaze thread, <see cref="Execute"/> on the UI thread.
/// The depth counter is interlocked because both touch it; the histogram is written only by
/// <see cref="Execute"/>, so it stays single-threaded, and the reader is the same UI thread.
/// </para>
/// </summary>
public sealed class DispatchLatencyProbe
{
    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly DurationHistogram _latency;
    private int _depth;
    private int _maxDepth;

    /// <summary>Initializes the probe.</summary>
    /// <param name="maxMilliseconds">Largest latency given its own histogram bucket.</param>
    public DispatchLatencyProbe(int maxMilliseconds = 255)
    {
        _latency = new DurationHistogram(maxMilliseconds);
    }

    /// <summary>Enqueue→execute latency distribution. Read from the UI thread.</summary>
    public DurationHistogram Latency => _latency;

    /// <summary>Items posted but not yet executed, right now.</summary>
    public int CurrentDepth => Volatile.Read(ref _depth);

    /// <summary>Deepest the queue got over the session — the burst evidence the hypothesis predicts.</summary>
    public int MaxDepth => Volatile.Read(ref _maxDepth);

    /// <summary>
    /// Call immediately before posting to the dispatcher, on the gaze thread. Returns the timestamp to
    /// hand back to <see cref="Execute"/> — carried on the closure the call site already allocates, so
    /// instrumenting costs no extra allocation per sample.
    /// </summary>
    /// <returns>A <see cref="Stopwatch"/> timestamp to pass to <see cref="Execute"/>.</returns>
    public long Enqueue()
    {
        int depth = Interlocked.Increment(ref _depth);

        // Interlocked max: a plain compare-and-assign would lose a concurrent update, and this counter
        // is the whole point of the instrument — an under-reported peak would falsely acquit the queue.
        int observed = Volatile.Read(ref _maxDepth);
        while (depth > observed)
        {
            int previous = Interlocked.CompareExchange(ref _maxDepth, depth, observed);
            if (previous == observed) break;
            observed = previous;
        }

        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Call at the start of the dispatched work, on the UI thread, before doing anything else — the
    /// number wanted is the wait, not the wait plus the work.
    /// </summary>
    /// <param name="enqueuedTimestamp">The value <see cref="Enqueue"/> returned.</param>
    public void Execute(long enqueuedTimestamp)
    {
        _latency.Record((Stopwatch.GetTimestamp() - enqueuedTimestamp) * TicksToMilliseconds);
        Interlocked.Decrement(ref _depth);
    }

    /// <summary>
    /// The verdict the pre-registered rule turns on: whether p95 is under one frame.
    /// </summary>
    /// <param name="frameMilliseconds">One frame at the compositor's rate; 16.67 ms at 60 Hz.</param>
    /// <returns><c>true</c> when p95 is under a frame, i.e. the coalescing proposal is withdrawn.</returns>
    public bool P95IsUnderOneFrame(double frameMilliseconds = 1000.0 / 60.0) =>
        _latency.Count > 0 && _latency.Percentile(0.95) < frameMilliseconds;

    /// <summary>Renders the session summary, including the falsifier's verdict, as one log line.</summary>
    public string Describe(double frameMilliseconds = 1000.0 / 60.0)
    {
        if (_latency.Count == 0) return "dispatch latency: no samples";

        string verdict = P95IsUnderOneFrame(frameMilliseconds)
            ? $"p95 < one frame ({frameMilliseconds:0.##}ms) — UI coalescing (C) is WITHDRAWN per the pre-registered rule"
            : $"p95 >= one frame ({frameMilliseconds:0.##}ms) — UI coalescing (C) is NOT withdrawn";

        return $"dispatch latency: {_latency.Describe()} maxQueueDepth={MaxDepth}; {verdict}";
    }
}
