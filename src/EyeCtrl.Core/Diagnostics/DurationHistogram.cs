namespace EyeCtrl.Core.Diagnostics;

using System.Globalization;
using System.Text;

/// <summary>
/// Fixed-bucket millisecond histogram with percentiles — the accumulator both Stage-2 UI instruments
/// report through.
/// <para>
/// Buckets rather than a sample list because these instruments run per gaze sample for a whole
/// session: a list would grow without bound on the very path being measured, and an instrument that
/// perturbs its subject measures itself. Memory here is fixed at construction, and
/// <see cref="Record"/> is one array increment.
/// </para>
/// <para>
/// Percentiles are therefore accurate to the bucket width (1 ms), which is the resolution the
/// questions need: "is p95 under one frame (~16 ms)?" does not turn on a tenth of a millisecond.
/// <see cref="MaxMilliseconds"/> is tracked exactly, since the worst case is the one a bucketed
/// estimate would blur most.
/// </para>
/// <para>Not thread-safe: record from one thread (each probe documents which).</para>
/// </summary>
public sealed class DurationHistogram
{
    private readonly int[] _buckets;
    private long _count;
    private double _sum;

    /// <summary>Initializes a histogram covering <paramref name="maxMilliseconds"/> 1 ms buckets plus overflow.</summary>
    /// <param name="maxMilliseconds">Highest millisecond given its own bucket; anything longer lands in overflow.</param>
    public DurationHistogram(int maxMilliseconds = 255)
    {
        if (maxMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMilliseconds), maxMilliseconds, "Must be positive.");
        _buckets = new int[maxMilliseconds + 2];   // 0..max, plus one overflow slot
    }

    /// <summary>Number of recorded measurements.</summary>
    public long Count => _count;

    /// <summary>Largest measurement seen, exact (not bucketed).</summary>
    public double MaxMilliseconds { get; private set; }

    /// <summary>Arithmetic mean of all measurements, in milliseconds.</summary>
    public double MeanMilliseconds => _count == 0 ? 0 : _sum / _count;

    /// <summary>Records one measurement. Negative values are clamped to zero rather than rejected —
    /// a clock that steps backwards must not throw on an instrumentation path.</summary>
    /// <param name="milliseconds">The measured duration.</param>
    public void Record(double milliseconds)
    {
        if (double.IsNaN(milliseconds)) return;
        if (milliseconds < 0) milliseconds = 0;

        _count++;
        _sum += milliseconds;
        if (milliseconds > MaxMilliseconds) MaxMilliseconds = milliseconds;

        int bucket = (int)milliseconds;
        if (bucket >= _buckets.Length - 1) bucket = _buckets.Length - 1;   // overflow
        _buckets[bucket]++;
    }

    /// <summary>
    /// The bucket at or below which <paramref name="fraction"/> of measurements fall, in milliseconds.
    /// Returns the bucket's LOWER edge, so a reported p95 of 15 means "95 % of samples were under
    /// 16 ms" — the conservative reading for a falsifier expressed as an upper bound.
    /// </summary>
    /// <param name="fraction">Percentile as a fraction, e.g. 0.95.</param>
    public double Percentile(double fraction)
    {
        if (_count == 0) return 0;

        long target = (long)Math.Ceiling(fraction * _count);
        if (target < 1) target = 1;

        long running = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            running += _buckets[i];
            if (running >= target)
                return i == _buckets.Length - 1 ? MaxMilliseconds : i;
        }
        return MaxMilliseconds;
    }

    /// <summary>Non-empty buckets as (millisecond, count) pairs, ascending. The overflow bucket is
    /// reported at <c>maxMilliseconds + 1</c>.</summary>
    public IReadOnlyList<(int Milliseconds, int Count)> NonEmptyBuckets()
    {
        var result = new List<(int, int)>();
        for (int i = 0; i < _buckets.Length; i++)
            if (_buckets[i] > 0) result.Add((i, _buckets[i]));
        return result;
    }

    /// <summary>Renders the distribution as a single log-friendly line.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"n={_count} p50={Percentile(0.50):0}ms p95={Percentile(0.95):0}ms ")
          .Append(CultureInfo.InvariantCulture, $"max={MaxMilliseconds:0.##}ms mean={MeanMilliseconds:0.##}ms");
        return sb.ToString();
    }

    /// <summary>Renders the non-empty buckets as a log-friendly line, e.g. <c>0ms:412 1ms:88 33ms:2</c>.</summary>
    public string DescribeBuckets()
    {
        var sb = new StringBuilder();
        foreach (var (ms, count) in NonEmptyBuckets())
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(CultureInfo.InvariantCulture, $"{ms}ms:{count}");
        }
        return sb.Length == 0 ? "(no samples)" : sb.ToString();
    }

    /// <summary>Clears every accumulated statistic.</summary>
    public void Reset()
    {
        Array.Clear(_buckets);
        _count = 0;
        _sum = 0;
        MaxMilliseconds = 0;
    }
}
