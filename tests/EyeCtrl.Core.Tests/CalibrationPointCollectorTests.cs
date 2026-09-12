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
        ManualClock? clock = null, double bandRadiusPx = 120)
    {
        var c = clock ?? new ManualClock();
        return new CalibrationPointCollector(
            target, settleWindowMs, maxSamples,
            bandRadiusPx: bandRadiusPx, nowMs: () => c.NowMs);
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
    public void StableCluster_FarFromTheTarget_IsNotArrival()
    {
        // REVERSED on 2026-09-13. This test previously asserted the opposite, on the reasoning that
        // raw gaze may be systematically offset, so a tight cluster anywhere means the user IS
        // fixating and their samples map their intended point.
        //
        // The reasoning was wrong in a way the 2026-09-12 session made concrete: "steadily fixating
        // somewhere else" is indistinguishable from "steadily looking at the wrong thing", and this
        // path certified arrival on the top-left target from a cluster that finished with a mean
        // 911 px away. Every sample after it entered the fit. A calibration is a map from gaze to
        // TARGET; a point whose gaze was never near the target carries no information about that map,
        // and including it bends the transform for every other point too.
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(1500, 800), settleWindowMs: 0, nowMs: () => clock.NowMs);

        for (var i = 0; i < 5; i++)
            collector.AddRawSample(new PhysicalPoint(300 + i, 400 - i));   // tight, ~1200 px off target

        Check.That(collector.HasArrived).IsFalse();
        Check.ThatCode(() => collector.End()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void StableCluster_InsideTheBand_IsStillArrival()
    {
        // The contrast case, and the reason the stability path was capped rather than deleted: a user
        // whose gaze hovers just off the target without any single sample landing inside it still
        // arrives, as long as the cluster itself is in the band. Without this the previous test would
        // pass against a collector that had simply dropped stability arrival altogether.
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(1500, 800), settleWindowMs: 0, bandRadiusPx: 120, nowMs: () => clock.NowMs);

        // Centroid ~100 px from the target - inside the band - while the cluster spans only ~4 px.
        for (var i = 0; i < 5; i++)
            collector.AddRawSample(new PhysicalPoint(1580 + i, 860 - i));

        Check.That(collector.HasArrived).IsTrue();
    }

    // -- In-band gating and coverage -------------------------------------------

    [Fact]
    public void PostArrivalSamples_OutsideTheBand_DoNotCount()
    {
        // The single change that turns sigma from a motion number into a noise number. Before it,
        // arrival opened the gate and nothing closed it, so everything the user did for the next
        // second entered the mean and sigma.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, bandRadiusPx: 100);

        collector.AddRawSample(new PhysicalPoint(505, 505));    // arrival (discarded)
        collector.AddRawSample(new PhysicalPoint(510, 500));    // in band  - counts
        collector.AddRawSample(new PhysicalPoint(2000, 2000));  // wandered - seen, not counted
        collector.AddRawSample(new PhysicalPoint(1200, 500));   // wandered - seen, not counted
        collector.AddRawSample(new PhysicalPoint(490, 495));    // back in band - counts

        var point = collector.End();

        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(point.AttemptedSamples).IsEqualTo(4);
        Check.That(point.Coverage).IsEqualTo(0.5);

        // And the mean is not dragged towards the wandering: under the old collector it would have
        // sat near (1050, 874), some 620 px from the target, and been fitted as if it meant something.
        Check.That(point.MeanGaze.X).IsCloseTo(500.0, 15.0);
        Check.That(point.MeanGaze.Y).IsCloseTo(497.5, 15.0);
    }

    [Fact]
    public void The20260912Shape_StableAwayGaze_ProducesNoCalibrationPoint()
    {
        // The recorded failure, end to end: the user settles steadily ~900 px from the target and
        // holds there. Previously this certified arrival, collected thirty samples, and reported a
        // confident mean 911 px out. It must now produce nothing at all rather than a plausible point.
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(768, 480), settleWindowMs: 200, maxSamples: 30,
            bandRadiusPx: 240, nowMs: () => clock.NowMs);

        for (var i = 0; i < 60; i++)
        {
            clock.Tick(33);
            collector.AddRawSample(new PhysicalPoint(1650 + (i % 5), 700 - (i % 5)));
        }

        Check.That(collector.HasArrived).IsFalse();
        Check.That(collector.InBandSamples).IsEqualTo(0);
        Check.ThatCode(() => collector.End()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void ALowSigmaCannotHideBehindLowCoverage()
    {
        // The property the whole ticket exists for. Two in-band samples one pixel apart give a
        // gorgeous sigma; the coverage figure beside it says the user was on target 2% of the time.
        // Reporting sigma alone is precisely how the 2026-09-12 calibration passed for precision.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, bandRadiusPx: 100);

        collector.AddRawSample(new PhysicalPoint(500, 500));   // arrival (discarded)
        collector.AddRawSample(new PhysicalPoint(500, 500));
        collector.AddRawSample(new PhysicalPoint(501, 500));
        for (var i = 0; i < 98; i++)
            collector.AddRawSample(new PhysicalPoint(3000, 2000));

        var point = collector.End();

        Check.That(point.StdDevX).IsStrictlyLessThan(1.0);      // beautiful, and meaningless alone
        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(point.AttemptedSamples).IsEqualTo(100);
        Check.That(point.Coverage).IsEqualTo(0.02);
    }

    [Theory]
    // Sample rate, hold length and sample budget all change how many samples arrive AFTER a target is
    // already satisfied. None of them should be able to move coverage, because none of them is
    // something the user did.
    [InlineData(33, 15, 30)]    // shipped defaults, ~33 Hz
    [InlineData(60, 15, 60)]    // faster tracker
    [InlineData(90, 15, 120)]   // faster still
    [InlineData(33, 5, 100)]    // small sample budget, long linger
    [InlineData(33, 30, 200)]   // large budget, long linger
    public void AUserWhoNeverLeavesTheBand_HasFullCoverage_AtAnyRateOrHoldLength(
        int sampleRateHz, int samplesPerPoint, int extraSamplesAfterCompletion)
    {
        // The regression this exists for. A finished target stays on screen until the session advances
        // it, and a user doing exactly the right thing keeps looking at it — so samples keep arriving
        // while the in-band count is frozen at the cap. Counting them as "attempted" made coverage
        // decay with linger time: 60 % at shipped defaults, 23 % at 90 Hz, 16 % at the longest hold.
        // The coverage gate would then have rejected a PERFECT calibration, and told the user it was
        // their fault.
        var clock = new ManualClock();
        var collector = new CalibrationPointCollector(
            new PhysicalPoint(1000, 800), settleWindowMs: 0, maxSamples: samplesPerPoint,
            bandRadiusPx: 240, nowMs: () => clock.NowMs);

        int periodMs = Math.Max(1, 1000 / sampleRateHz);
        void Look()
        {
            clock.Tick(periodMs);
            collector.AddRawSample(new PhysicalPoint(1002, 798));   // dead on target, always
        }

        Look();                                                     // arrival (discarded)
        for (var i = 0; i < samplesPerPoint; i++) Look();
        Check.That(collector.IsComplete).IsTrue();

        // The linger: the target has not been advanced yet and the user is still looking at it.
        for (var i = 0; i < extraSamplesAfterCompletion; i++) Look();

        var point = collector.End();

        Check.That(point.SampleCount).IsEqualTo(samplesPerPoint);
        Check.That(point.AttemptedSamples).IsEqualTo(samplesPerPoint);
        Check.That(point.Coverage).IsEqualTo(1.0);
    }

    [Fact]
    public void OutOfBandSamplesAfterCompletion_DoNotDiluteCoverageEither()
    {
        // The other half: once the target is satisfied, looking AWAY is equally irrelevant. The user
        // has already done what was asked; where they look while waiting for the next dot is not
        // evidence about the point just collected.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, maxSamples: 3,
            bandRadiusPx: 100);

        collector.AddRawSample(new PhysicalPoint(500, 500));   // arrival
        for (var i = 0; i < 3; i++) collector.AddRawSample(new PhysicalPoint(500, 500));
        Check.That(collector.IsComplete).IsTrue();

        for (var i = 0; i < 500; i++) collector.AddRawSample(new PhysicalPoint(3000, 2000));

        var point = collector.End();

        Check.That(point.AttemptedSamples).IsEqualTo(3);
        Check.That(point.Coverage).IsEqualTo(1.0);
    }

    [Fact]
    public void WanderingBeforeCompletion_StillCountsAgainstCoverage()
    {
        // The contrast that keeps the fix honest. Ignoring post-completion samples must not turn into
        // ignoring inconvenient ones: everything that happened while the target still NEEDED samples
        // is evidence, and that is exactly what the 2026-09-12 session was full of.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, maxSamples: 2,
            bandRadiusPx: 100);

        collector.AddRawSample(new PhysicalPoint(500, 500));   // arrival
        collector.AddRawSample(new PhysicalPoint(500, 500));   // in band, 1 of 2
        for (var i = 0; i < 8; i++)
            collector.AddRawSample(new PhysicalPoint(3000, 2000));   // wander, still needed
        collector.AddRawSample(new PhysicalPoint(500, 500));   // in band, 2 of 2 — now complete

        var point = collector.End();

        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(point.AttemptedSamples).IsEqualTo(10);
        Check.That(point.Coverage).IsEqualTo(0.2);
    }

    [Fact]
    public void Coverage_IsNullBeforeAnyPostSettleSample()
    {
        // Unknown, not zero. A target nobody has tried yet must not read as a target the user failed.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 5000);

        collector.AddRawSample(new PhysicalPoint(500, 500));   // arrival; settle has not elapsed

        Check.That(collector.Coverage).IsNull();
        Check.That(collector.AttemptedSamples).IsEqualTo(0);
    }

    [Fact]
    public void IsComplete_CountsInBandSamplesOnly_SoWanderingNeverAdvancesATarget()
    {
        // Pacing follows quality, not the clock: an unlimited amount of out-of-band gaze leaves the
        // target exactly where it was, so the session waits rather than advancing on rubbish.
        var collector = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, maxSamples: 3,
            bandRadiusPx: 100);

        collector.AddRawSample(new PhysicalPoint(500, 500));   // arrival
        for (var i = 0; i < 500; i++)
            collector.AddRawSample(new PhysicalPoint(3000, 2000));

        Check.That(collector.IsComplete).IsFalse();

        collector.AddRawSample(new PhysicalPoint(500, 500));
        collector.AddRawSample(new PhysicalPoint(501, 501));
        collector.AddRawSample(new PhysicalPoint(499, 499));

        Check.That(collector.IsComplete).IsTrue();
    }

    [Fact]
    public void End_SaysWhetherTheTargetWasReachedOrOnlyNeverHeld()
    {
        // Two different user errors needing opposite advice - "you never looked at it" versus "you
        // looked at it but could not hold it". The old message said neither.
        var never = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, bandRadiusPx: 50);
        never.AddRawSample(new PhysicalPoint(3000, 2000));
        Check.ThatCode(() => never.End()).Throws<InvalidOperationException>()
             .WithMessage("Gaze never reached the target.");

        var reached = Collector(new PhysicalPoint(500, 500), settleWindowMs: 0, bandRadiusPx: 50);
        reached.AddRawSample(new PhysicalPoint(500, 500));     // arrival
        reached.AddRawSample(new PhysicalPoint(3000, 2000));   // then gone

        var thrown = Check.ThatCode(() => reached.End()).Throws<InvalidOperationException>().Value;
        Check.That(thrown.Message).Contains("never stayed within");
        Check.That(thrown.Message).Contains("1 samples seen");   // the attempt is named, not just the failure
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
