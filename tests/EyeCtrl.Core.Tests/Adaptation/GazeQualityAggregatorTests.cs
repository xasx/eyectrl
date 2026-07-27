namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeQualityAggregatorTests
{
    // Aggregation is gated on AdaptationSettings.Enabled (§9.1), so the behavioural tests opt in;
    // Disabled_* below cover the gated-off side.
    private static AdaptationSettings Adaptation(bool enabled = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = enabled;
        return settings.Adaptation;
    }

    private static AdaptationSettings Enabled() => Adaptation(enabled: true);

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
        var agg = new GazeQualityAggregator(sink, Enabled(), windowMs: 500);

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
        var agg = new GazeQualityAggregator(sink, Enabled(), windowMs: 500);

        for (long t = 0; t <= 1_200_000; t += 100_000)
            agg.OnSample(Sample(t, valid: true), 10, 0.0);

        Check.That(sink.Summaries.Count >= 2).IsTrue();
    }

    [Fact]
    public void EmitsNothing_BeforeWindowElapses()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, Enabled(), windowMs: 500);

        agg.OnSample(Sample(0, valid: true), 10, 0.0);
        agg.OnSample(Sample(100_000, valid: true), 10, 0.0);

        Check.That(sink.Summaries).IsEmpty();
    }

    [Fact]
    public void Disabled_EmitsNothing_AndDoesNoAggregation()
    {
        // §9.1 "disabled = current cost": with adaptation off — the default — the gaze path must carry no
        // aggregation at all, not merely a suppressed summary.
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, Adaptation(enabled: false), windowMs: 500);

        for (long t = 0; t <= 2_000_000; t += 100_000)
            agg.OnSample(Sample(t, valid: t % 300_000 != 0), 10, 0.2);

        Check.That(sink.Summaries).IsEmpty();
    }

    [Fact]
    public void ReEnabling_StartsAFreshWindow_DiscardingTheDisabledStretch()
    {
        // A window that straddled the toggle would report an invalid fraction computed over samples the
        // feature was not even watching, so the partial window is dropped when the gate closes.
        var sink = new RecordingQualitySink();
        var adaptation = Adaptation(enabled: true);
        var agg = new GazeQualityAggregator(sink, adaptation, windowMs: 500);

        agg.OnSample(Sample(0, valid: false), 10, 0.0);      // opens a window, 1 invalid sample
        adaptation.Enabled = false;
        agg.OnSample(Sample(100_000, valid: false), 10, 0.0); // gated: discards the partial window
        adaptation.Enabled = true;

        agg.OnSample(Sample(200_000, valid: true), 10, 0.0);  // opens a FRESH window here
        agg.OnSample(Sample(700_000, valid: true), 10, 0.0);  // closes it exactly 500 ms later

        Check.That(sink.Summaries).HasSize(1);
        Check.That(sink.Summaries[0].SampleCount).IsEqualTo(2);   // not 3, not 4
        Check.That(sink.Summaries[0].InvalidFraction).IsEqualTo(0.0); // the invalid ones are not carried over
    }
}
