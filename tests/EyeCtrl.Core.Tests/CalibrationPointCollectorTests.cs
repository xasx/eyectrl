namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationPointCollectorTests
{
    // A manual clock so settle/arrival timing is deterministic — no sleeps.
    private sealed class ManualClock
    {
        public long NowMs;
        public long Tick(int ms) => NowMs += ms;
    }

    private static CalibrationPointCollector Collector(
        PhysicalPoint target, int settleWindowMs, int maxSamples = int.MaxValue,
        ManualClock? clock = null, double acceptanceRadiusPx = 120)
    {
        var c = clock ?? new ManualClock();
        return new CalibrationPointCollector(
            target, settleWindowMs, maxSamples,
            acceptanceRadiusPx: acceptanceRadiusPx, nowMs: () => c.NowMs);
    }

    [Fact]
    public void End_ReturnsCalibrationPoint_WithAveragedGaze()
    {
        // The FIRST on-target sample is the saccade landing that establishes arrival — it is
        // discarded with the settle window (here zero), so the mean covers the samples after it.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0);

        collector.AddRawSample(new PhysicalPoint(510, 490));
        collector.AddRawSample(new PhysicalPoint(530, 510));
        collector.AddRawSample(new PhysicalPoint(510, 500));

        var point = collector.End();

        Check.That(point.Target.X).IsEqualTo(500);
        Check.That(point.Target.Y).IsEqualTo(500);
        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(Math.Abs(point.MeanGaze.X - 520) < 0.1).IsTrue();
        Check.That(Math.Abs(point.MeanGaze.Y - 505) < 0.1).IsTrue();
        Check.That(point.StdDevX > 0).IsTrue();
        Check.That(point.StdDevY > 0).IsTrue();
    }

    [Fact]
    public void SettleWindow_RunsFromArrival_NotFromCreation()
    {
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(100, 100), settleWindowMs: 50, nowMs: () => clock.NowMs);

        // Off-target wander before the user finds the point: neither counted nor settling.
        collector.AddRawSample(new PhysicalPoint(999, 999));
        clock.Tick(500);
        collector.AddRawSample(new PhysicalPoint(990, 980));

        // Arrival: first on-target sample starts the settle window (and is discarded).
        collector.AddRawSample(new PhysicalPoint(105, 95));

        // Still inside the settle window that started at arrival.
        clock.Tick(20);
        collector.AddRawSample(new PhysicalPoint(103, 97));
        Check.That(collector.HasArrived).IsTrue();

        // Past the settle window: samples count from here.
        clock.Tick(40);
        collector.AddRawSample(new PhysicalPoint(105, 95));
        collector.AddRawSample(new PhysicalPoint(95, 105));

        var point = collector.End();

        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(Math.Abs(point.MeanGaze.X - 100) < 10).IsTrue();
        Check.That(Math.Abs(point.MeanGaze.Y - 100) < 10).IsTrue();
    }

    [Fact]
    public void BeforeArrival_NothingCounts_NoMatterHowLong()
    {
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(100, 100), settleWindowMs: 0, nowMs: () => clock.NowMs);

        // Wandering gaze: every sample far from the target AND the recent window never tightens.
        for (var i = 0; i < 50; i++)
        {
            clock.Tick(100);
            collector.AddRawSample(new PhysicalPoint(900 + i * 20, 900));
        }

        Check.That(collector.HasArrived).IsFalse();
        Check.That(collector.IsComplete).IsFalse();
        Check.ThatCode(() => collector.End()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void StableCluster_AnywhereOnScreen_CountsAsArrival()
    {
        // The calibration chicken-and-egg: raw gaze may be systematically offset from where the
        // user is looking. A tight cluster far from the target means the user IS fixating
        // steadily — that is a valid arrival, and its samples map their intended point.
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(1500, 800), settleWindowMs: 0, nowMs: () => clock.NowMs);

        for (var i = 0; i < 5; i++)
            collector.AddRawSample(new PhysicalPoint(300 + i, 400 - i));   // tight, ~1200 px off target

        Check.That(collector.HasArrived).IsTrue();

        for (var i = 0; i < 3; i++)
            collector.AddRawSample(new PhysicalPoint(302, 398));

        var point = collector.End();
        Check.That(point.SampleCount).IsEqualTo(3);
        Check.That(Math.Abs(point.MeanGaze.X - 302) < 1).IsTrue();
    }

    [Fact]
    public void IsComplete_FlipsAtMaxSamples_PostArrival()
    {
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(0, 0), settleWindowMs: 0, maxSamples: 3, nowMs: () => clock.NowMs);

        collector.AddRawSample(new PhysicalPoint(1, 1));   // arrival (discarded)
        Check.That(collector.IsComplete).IsFalse();

        collector.AddRawSample(new PhysicalPoint(1, 1));
        Check.That(collector.IsComplete).IsFalse();
        collector.AddRawSample(new PhysicalPoint(1, 2));
        Check.That(collector.IsComplete).IsFalse();
        collector.AddRawSample(new PhysicalPoint(2, 1));
        Check.That(collector.IsComplete).IsTrue();
    }

    [Fact]
    public void End_Throws_WhenNoSamplesCollected()
    {
        var collector = Collector(new PhysicalPoint(100, 100), settleWindowMs: 200);
        collector.AddRawSample(new PhysicalPoint(100, 100));
        Check.ThatCode(() => collector.End()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void IsCollecting_TrueBeforeEnd_FalseAfterEnd()
    {
        var collector = Collector(new PhysicalPoint(0, 0), settleWindowMs: 0);
        collector.AddRawSample(new PhysicalPoint(5, 5));   // arrival
        collector.AddRawSample(new PhysicalPoint(5, 5));   // first counted sample
        Check.That(collector.IsCollecting).IsTrue();
        collector.End();
        Check.That(collector.IsCollecting).IsFalse();
    }

    [Fact]
    public void MaxSamples_CapsSampleCount()
    {
        var collector = Collector(new PhysicalPoint(0, 0), settleWindowMs: 0, maxSamples: 10);
        for (int i = 0; i < 100; i++)
            collector.AddRawSample(new PhysicalPoint(1, 1));

        var point = collector.End();
        Check.That(point.SampleCount).IsEqualTo(10);
    }
}
