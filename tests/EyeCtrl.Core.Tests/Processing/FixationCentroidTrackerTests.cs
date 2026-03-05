namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class FixationCentroidTrackerTests
{
    // 30ms intervals in microseconds (matches Tobii sample rate)
    private const long Dt = 30_000;

    [Fact]
    public void StableCentroid_BeforeTracking_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StableCentroid_AfterStartTracking_NoUpdates_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StableCentroid_SlowSamples_ReturnsMean()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // Slow movement: 1px in 30ms = ~33 px/s < 100 threshold
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.Update(new ScreenPoint(101, 201), Dt);
        tracker.Update(new ScreenPoint(100, 200), Dt * 2);

        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(100.33, centroid!.X, precision: 1);
        Assert.Equal(200.33, centroid.Y, precision: 1);
    }

    [Fact]
    public void StableCentroid_FastSamplesOnly_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // First sample has no velocity, so it IS included
        // Second sample: 300px in 30ms = 10000 px/s >> 100 threshold -> excluded
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.Update(new ScreenPoint(400, 500), Dt);

        // Only the first sample (velocity=0 <= threshold) contributes
        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(100, centroid!.X);
        Assert.Equal(200, centroid.Y);
    }

    [Fact]
    public void StableCentroid_MixedSamples_OnlySlowContribute()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        tracker.Update(new ScreenPoint(100, 100), 0);           // first, velocity=0 -> included
        tracker.Update(new ScreenPoint(100, 100), Dt);          // slow (0 px/s) -> included
        tracker.Update(new ScreenPoint(500, 500), Dt * 2);      // fast -> excluded
        tracker.Update(new ScreenPoint(102, 102), Dt * 3);      // fast from (500,500) -> excluded
        tracker.Update(new ScreenPoint(103, 103), Dt * 4);      // slow from (102,102) ~33px/s -> included

        // Mean of included: (100,100), (100,100), (103,103) -> (101, 101)
        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(101, centroid!.X);
        Assert.Equal(101, centroid.Y);
    }

    [Fact]
    public void StopTracking_ClearsStableCentroid()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.StopTracking();
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StartTracking_AfterStop_FreshAccumulation()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.StopTracking();

        tracker.StartTracking();
        tracker.Update(new ScreenPoint(300, 400), 0);

        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(300, centroid!.X);
    }
}
