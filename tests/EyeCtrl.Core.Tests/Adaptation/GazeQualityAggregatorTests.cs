namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class GazeQualityAggregatorTests
{
    private sealed class RecordingQualitySink : IGazeQualitySink
    {
        public List<GazeQualitySummary> Summaries { get; } = new();
        public void ReportGazeQuality(in GazeQualitySummary summary) => Summaries.Add(summary);
    }

    private static GazePoint Sample(long ts, bool valid) => new()
    {
        X = 100,
        Y = 100,
        IsValid = valid,
        TimestampMicroseconds = ts,
    };

    [Fact]
    public void EmitsSummary_OncePerWindow_WithInvalidFraction()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        // Window is 500 ms = 500_000 us. Feed 4 samples across the window; 1 invalid.
        agg.OnSample(Sample(0, valid: true), fixationJitter: 10, fatigueScore: 0.2);
        agg.OnSample(Sample(150_000, valid: false), 10, 0.2);
        agg.OnSample(Sample(300_000, valid: true), 10, 0.2);
        agg.OnSample(Sample(500_000, valid: true), fixationJitter: 25, fatigueScore: 0.4); // closes window

        Check.That(sink.Summaries).HasSize(1);
        var s = sink.Summaries[0];
        Check.That(s.SampleCount).IsEqualTo(4);
        Check.That(s.InvalidFraction).IsEqualTo(0.25);
        Check.That(s.JitterStdDev).IsEqualTo(25.0);  // latest jitter at window close
        Check.That(s.FatigueScore).IsEqualTo(0.4);   // latest fatigue at window close
    }

    [Fact]
    public void EmitsMultipleSummaries_AcrossMultipleWindows()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        for (long t = 0; t <= 1_200_000; t += 100_000)
            agg.OnSample(Sample(t, valid: true), 10, 0.0);

        Check.That(sink.Summaries.Count >= 2).IsTrue();
    }

    [Fact]
    public void EmitsNothing_BeforeWindowElapses()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        agg.OnSample(Sample(0, valid: true), 10, 0.0);
        agg.OnSample(Sample(100_000, valid: true), 10, 0.0);

        Check.That(sink.Summaries).IsEmpty();
    }
}
