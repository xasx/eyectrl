namespace EyeCtrl.Core.Diagnostics;

using System.Diagnostics;

/// <summary>The probe's view of one recent window: what the queue did just before an owner's lag mark.</summary>
public readonly record struct DispatchWindowSummary(int Count, double MaxMs, double P95Ms, int MaxDepth, double GapMs, double CoverageMs);

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
/// Threading: <see cref="Enqueue"/> runs on the gaze thread, <see cref="Execute(long)"/> on the UI thread.
/// The depth counter is interlocked because both touch it; the histogram is written only by
/// <see cref="Execute(long)"/>, so it stays single-threaded, and the reader is the same UI thread.
/// </para>
/// </summary>
public sealed class DispatchLatencyProbe
{
    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly DurationHistogram _latency;
    private int _depth;
    private int _maxDepth;

    private const int WindowCapacity = 2400;               // 20 s at 120 Hz; preallocated, no per-sample allocation
    private readonly long[] _recentAt = new long[WindowCapacity];
    private readonly double[] _recentLatency = new double[WindowCapacity];
    private readonly int[] _recentDepth = new int[WindowCapacity];
    private int _recentNext;
    private int _recentCount;

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
    /// hand back to <see cref="Execute(long)"/> — carried on the closure the call site already allocates, so
    /// instrumenting costs no extra allocation per sample.
    /// </summary>
    /// <returns>A <see cref="Stopwatch"/> timestamp to pass to <see cref="Execute(long)"/>.</returns>
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
    public void Execute(long enqueuedTimestamp) => Execute(enqueuedTimestamp, Stopwatch.GetTimestamp());

    /// <summary>
    /// The clock-injected form of <see cref="Execute(long)"/>, so tests can drive the recent window
    /// deterministically (the public method passes the live stopwatch reading).
    /// </summary>
    /// <param name="enqueuedTimestamp">The value <see cref="Enqueue"/> returned.</param>
    /// <param name="nowTimestamp">The moment the dispatched work starts, in stopwatch ticks.</param>
    internal void Execute(long enqueuedTimestamp, long nowTimestamp)
    {
        double latency = (nowTimestamp - enqueuedTimestamp) * TicksToMilliseconds;
        _latency.Record(latency);
        LastLatencyMs = latency < 0 ? 0 : latency;

        // The queue length observed at execute, this item included: the burst a drain starts from.
        _recentAt[_recentNext] = nowTimestamp;
        _recentLatency[_recentNext] = LastLatencyMs;
        _recentDepth[_recentNext] = Volatile.Read(ref _depth);
        _recentNext = (_recentNext + 1) % WindowCapacity;
        if (_recentCount < WindowCapacity) _recentCount++;

        Interlocked.Decrement(ref _depth);
    }

    /// <summary>The wait of the most recent execute, in ms. Read on the UI thread, where it is written.</summary>
    public double LastLatencyMs { get; private set; }

    /// <summary>Summarises the executes in (end - window, end]. Single-threaded with Execute (the UI thread).</summary>
    /// <param name="endTimestamp">The window's inclusive end, in stopwatch ticks.</param>
    /// <param name="window">How far back the window reaches.</param>
    /// <returns>The window's counts, or all zeros when no execute falls inside it.</returns>
    public DispatchWindowSummary Summarize(long endTimestamp, TimeSpan window)
    {
        long windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        long start = endTimestamp - windowTicks;
        var values = new List<double>(_recentCount);
        double max = 0, gap = 0;
        int maxDepth = 0;
        long? previous = null, oldestInBuffer = null;

        for (int i = 0; i < _recentCount; i++)
        {
            int index = (_recentNext - _recentCount + i + WindowCapacity) % WindowCapacity;   // oldest -> newest
            long at = _recentAt[index];
            oldestInBuffer ??= at;
            if (at <= start || at > endTimestamp) continue;
            values.Add(_recentLatency[index]);
            if (_recentLatency[index] > max) max = _recentLatency[index];
            if (_recentDepth[index] > maxDepth) maxDepth = _recentDepth[index];
            if (previous is { } p) gap = Math.Max(gap, (at - p) * TicksToMilliseconds);
            previous = at;
        }

        if (values.Count == 0) return new DispatchWindowSummary(0, 0, 0, 0, 0, 0);

        values.Sort();
        int target = Math.Max(1, (int)Math.Ceiling(0.95 * values.Count));
        double p95 = Math.Floor(values[target - 1]);                       // the histogram's lower edge
        double coverage = Math.Min(window.TotalMilliseconds, (endTimestamp - oldestInBuffer!.Value) * TicksToMilliseconds);
        return new DispatchWindowSummary(values.Count, max, p95, maxDepth, gap, coverage);
    }

    /// <summary>Renders one window summary in the LAG MARK line's invariant shape.</summary>
    /// <param name="s">The summary to render.</param>
    /// <returns><c>n=… max=…ms p95=…ms maxQueueDepth=… gapMs=… coverageMs=…</c>.</returns>
    public static string DescribeWindow(DispatchWindowSummary s) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"n={s.Count} max={s.MaxMs:0.##}ms p95={s.P95Ms:0}ms maxQueueDepth={s.MaxDepth} gapMs={s.GapMs:0.##} coverageMs={s.CoverageMs:0}");

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
