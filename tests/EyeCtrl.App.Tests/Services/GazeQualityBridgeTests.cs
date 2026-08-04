namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using NFluent;

public class GazeQualityBridgeTests
{
    private sealed class FakeEyeCare : IEyeCareService
    {
        public bool Fresh { get; set; } = true;
        public double Score { get; set; }

        public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true) { }
        public void Cancel() { }
        public double FatigueScore => Score;

        public bool TryGetFatigueScore(long sampleTimestampUs, out double score)
        {
            score = Fresh ? Score : 0.0;
            return Fresh;
        }
    }

    private sealed class CapturingQualitySink : IGazeQualitySink
    {
        public readonly List<GazeQualitySummary> Summaries = [];
        public void ReportGazeQuality(in GazeQualitySummary summary) => Summaries.Add(summary);
    }

    [Fact]
    public void PassesTheFatigueScore_WhenEyeCareReportsItFresh()
    {
        var eyeCare = new FakeEyeCare { Fresh = true, Score = 0.75 };
        var (bridge, sink) = NewBridge(eyeCare);

        FeedOneWindow(bridge);

        Check.That(sink.Summaries).Not.IsEmpty();
        Check.That(sink.Summaries[0].FatigueScore).IsEqualTo(0.75);
    }

    [Fact]
    public void SubstitutesZero_WhenEyeCareReportsItStale()
    {
        // The regression: eye care faulted, so its score is frozen at 0.75. The bridge must report 0
        // rather than let a stale value drive adaptation.
        var eyeCare = new FakeEyeCare { Fresh = false, Score = 0.75 };
        var (bridge, sink) = NewBridge(eyeCare);

        FeedOneWindow(bridge);

        Check.That(sink.Summaries).Not.IsEmpty();
        Check.That(sink.Summaries[0].FatigueScore).IsEqualTo(0.0);
    }

    private static (GazeQualityBridge Bridge, CapturingQualitySink Sink) NewBridge(IEyeCareService eyeCare)
    {
        var adaptation = new AdaptationSettings { Enabled = true };
        var sink = new CapturingQualitySink();
        var aggregator = new GazeQualityAggregator(sink, adaptation, windowMs: 500);
        return (new GazeQualityBridge(aggregator, eyeCare, () => 0.0), sink);
    }

    // Drives the aggregator past its 500 ms window so it emits exactly one summary.
    private static void FeedOneWindow(GazeQualityBridge bridge)
    {
        bridge.OnGazeSample(new GazePoint(960, 540, 1_000_000, IsValid: true));
        bridge.OnGazeSample(new GazePoint(960, 540, 1_600_000, IsValid: true));
    }
}
