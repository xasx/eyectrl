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
        Check.That(tracker.StableCentroid).IsNull();
    }

    [Fact]
    public void StableCentroid_AfterStartTracking_NoUpdates_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        Check.That(tracker.StableCentroid).IsNull();
    }

    [Fact]
    public void StableCentroid_SlowSamples_ReturnsMean()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // Slow movement: 1px in 30ms = ~33 px/s < 100 threshold
        tracker.Update(new PhysicalPoint(100, 200), 0);
        tracker.Update(new PhysicalPoint(101, 201), Dt);
        tracker.Update(new PhysicalPoint(100, 200), Dt * 2);

        var centroid = tracker.StableCentroid;
        Check.That(centroid).IsNotNull();
        Check.That(centroid!.Value.X).IsCloseTo(100.33, 0.1);
        Check.That(centroid!.Value.Y).IsCloseTo(200.33, 0.1);
    }

    [Fact]
    public void StableCentroid_FastSamplesOnly_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // First sample has no velocity, so it IS included
        // Second sample: 300px in 30ms = 10000 px/s >> 100 threshold -> excluded
        tracker.Update(new PhysicalPoint(100, 200), 0);
        tracker.Update(new PhysicalPoint(400, 500), Dt);

        // Only the first sample (velocity=0 <= threshold) contributes
        var centroid = tracker.StableCentroid;
        Check.That(centroid).IsNotNull();
        Check.That(centroid!.Value.X).IsEqualTo(100);
        Check.That(centroid!.Value.Y).IsEqualTo(200);
    }

    [Fact]
    public void StableCentroid_MixedSamples_OnlySlowContribute()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        tracker.Update(new PhysicalPoint(100, 100), 0);           // first, velocity=0 -> included
        tracker.Update(new PhysicalPoint(100, 100), Dt);          // slow (0 px/s) -> included
        tracker.Update(new PhysicalPoint(500, 500), Dt * 2);      // fast -> excluded
        tracker.Update(new PhysicalPoint(102, 102), Dt * 3);      // fast from (500,500) -> excluded
        tracker.Update(new PhysicalPoint(103, 103), Dt * 4);      // slow from (102,102) ~33px/s -> included

        // Mean of included: (100,100), (100,100), (103,103) -> (101, 101)
        var centroid = tracker.StableCentroid;
        Check.That(centroid).IsNotNull();
        Check.That(centroid!.Value.X).IsEqualTo(101);
        Check.That(centroid!.Value.Y).IsEqualTo(101);
    }

    [Fact]
    public void StopTracking_ClearsStableCentroid()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new PhysicalPoint(100, 200), 0);
        tracker.StopTracking();
        Check.That(tracker.StableCentroid).IsNull();
    }

    [Fact]
    public void StartTracking_AfterStop_FreshAccumulation()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new PhysicalPoint(100, 200), 0);
        tracker.StopTracking();

        tracker.StartTracking();
        tracker.Update(new PhysicalPoint(300, 400), 0);

        var centroid = tracker.StableCentroid;
        Check.That(centroid).IsNotNull();
        Check.That(centroid!.Value.X).IsEqualTo(300);
    }
}
