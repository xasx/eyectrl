namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class FixationCentroidTrackerJitterTests
{
    [Fact]
    public void SpeedStdDev_IsZero_WithFewerThanTwoSpeeds()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 1000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);      // first sample — no speed yet
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }

    [Fact]
    public void SpeedStdDev_IsPositive_WhenSpeedsVary()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        // dt = 100 ms per step (100_000 us). Move 1px, then 5px, then 1px → speeds 10, 50, 10 px/s.
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(1, 0), 100_000);
        t.Update(new PhysicalPoint(6, 0), 200_000);
        t.Update(new PhysicalPoint(7, 0), 300_000);
        Check.That(t.SpeedStdDev > 0).IsTrue();
    }

    [Fact]
    public void StartTracking_ResetsJitter()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(50, 0), 100_000);
        Check.That(t.SpeedStdDev >= 0).IsTrue();
        t.StartTracking(); // new fixation resets accumulation
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }

    [Fact]
    public void StopTracking_LatchesLastCompletedJitter_ThenResetsLive()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(1, 0), 100_000);
        t.Update(new PhysicalPoint(6, 0), 200_000);
        t.Update(new PhysicalPoint(7, 0), 300_000);
        double live = t.SpeedStdDev;
        Check.That(live > 0).IsTrue();

        t.StopTracking(); // fixation end: latch the completed jitter, reset the live accumulator

        Check.That(t.LastCompletedSpeedStdDev).IsEqualTo(live);
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }
}
