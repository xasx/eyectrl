namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;

/// <summary>
/// The accumulator both UI instruments report through. Its percentiles decide a pre-registered
/// falsifier ("is p95 under one frame?"), so the arithmetic has to be right and its rounding has to be
/// in the conservative direction.
/// </summary>
public class DurationHistogramTests
{
    [Fact]
    public void Empty_ReportsZeroes()
    {
        var h = new DurationHistogram();

        Check.That(h.Count).IsEqualTo(0L);
        Check.That(h.Percentile(0.95)).IsEqualTo(0.0);
        Check.That(h.MaxMilliseconds).IsEqualTo(0.0);
        Check.That(h.MeanMilliseconds).IsEqualTo(0.0);
    }

    [Fact]
    public void Percentile_RecoversAKnownDistribution()
    {
        // 100 samples, one per millisecond 0..99: p50 is the 50th, p95 the 95th.
        var h = new DurationHistogram();
        for (int i = 0; i < 100; i++) h.Record(i);

        Check.That(h.Count).IsEqualTo(100L);
        Check.That(h.Percentile(0.50)).IsEqualTo(49.0);
        Check.That(h.Percentile(0.95)).IsEqualTo(94.0);
        Check.That(h.MaxMilliseconds).IsEqualTo(99.0);
    }

    [Fact]
    public void Percentile_ReturnsTheBucketsLowerEdge_WhichIsTheConservativeReading()
    {
        // Every sample is 15.9 ms — under one frame, but only just. Reporting the bucket's lower edge
        // (15) means "95 % were under 16 ms", which is exactly how the falsifier is phrased. Rounding
        // up instead would report 16 and fail a criterion the data actually meets.
        var h = new DurationHistogram();
        for (int i = 0; i < 50; i++) h.Record(15.9);

        Check.That(h.Percentile(0.95)).IsEqualTo(15.0);
        Check.That(h.MaxMilliseconds).IsEqualTo(15.9);   // the max stays exact
    }

    [Fact]
    public void Max_IsExactNotBucketed()
    {
        var h = new DurationHistogram();
        h.Record(3.25);
        h.Record(41.75);

        Check.That(h.MaxMilliseconds).IsEqualTo(41.75);
    }

    [Fact]
    public void Mean_IsTheArithmeticMean()
    {
        var h = new DurationHistogram();
        h.Record(10);
        h.Record(20);
        h.Record(30);

        Check.That(h.MeanMilliseconds).IsCloseTo(20.0, 1e-9);
    }

    [Fact]
    public void OverflowSamples_LandInTheLastBucket_AndStillCount()
    {
        // A stall far past the configured range must not be dropped: it is the most interesting sample
        // in the session, and losing it would make a pathological queue look healthy.
        var h = new DurationHistogram(maxMilliseconds: 10);
        for (int i = 0; i < 9; i++) h.Record(1);
        h.Record(5000);

        Check.That(h.Count).IsEqualTo(10L);
        Check.That(h.MaxMilliseconds).IsEqualTo(5000.0);
        Check.That(h.Percentile(1.0)).IsEqualTo(5000.0);   // the top percentile reports the exact max
    }

    [Fact]
    public void NegativeAndNaN_AreToleratedNotThrown()
    {
        // An instrument on a hot path must never throw. A backwards clock step is clamped; NaN is
        // dropped rather than poisoning the mean.
        var h = new DurationHistogram();

        Check.ThatCode(() => { h.Record(-5); h.Record(double.NaN); }).DoesNotThrow();
        Check.That(h.Count).IsEqualTo(1L);            // NaN not counted
        Check.That(h.MaxMilliseconds).IsEqualTo(0.0); // the negative was clamped to zero
    }

    [Fact]
    public void Buckets_ReportOnlyWhatWasObserved()
    {
        var h = new DurationHistogram();
        h.Record(0);
        h.Record(0);
        h.Record(33);

        Check.That(h.NonEmptyBuckets()).ContainsExactly((0, 2), (33, 1));
        Check.That(h.DescribeBuckets()).IsEqualTo("0ms:2 33ms:1");
    }

    [Fact]
    public void Describe_IsInvariantCulture()
    {
        // German is this machine's culture; a comma decimal separator in a log line that gets parsed
        // downstream is a silent corruption.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var h = new DurationHistogram();
            h.Record(1.5);

            Check.That(h.Describe()).Contains("1.5");
            Check.That(h.Describe()).Not.Contains("1,5");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var h = new DurationHistogram();
        h.Record(42);

        h.Reset();

        Check.That(h.Count).IsEqualTo(0L);
        Check.That(h.MaxMilliseconds).IsEqualTo(0.0);
        Check.That(h.NonEmptyBuckets()).IsEmpty();
    }
}
