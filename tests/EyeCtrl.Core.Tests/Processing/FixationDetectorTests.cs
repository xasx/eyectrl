namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class FixationDetectorTests
{
    [Fact]
    public void Update_GazeStableWithinRadius_RaisesFixationStarted()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        bool fixationStarted = false;
        detector.FixationStarted += (point) => fixationStarted = true;

        long time = 0;
        for (int i = 0; i < 9; i++) // 9 * 30ms = 270ms > 200ms
        {
            detector.Update(new PhysicalPoint(100 + i % 3, 100 + i % 3), time);
            time += 30_000; // microseconds
        }
        Check.That(fixationStarted).IsTrue();
    }

    [Fact]
    public void Update_GazeMovesOutsideRadius_RaisesFixationEnded()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        bool fixationEnded = false;
        detector.FixationEnded += () => fixationEnded = true;

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }
        detector.Update(new PhysicalPoint(500, 500), time);
        Check.That(fixationEnded).IsTrue();
    }

    [Fact]
    public void Update_GazeMovesBeforeThreshold_NoFixation()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        bool fixationStarted = false;
        detector.FixationStarted += (point) => fixationStarted = true;

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(102, 102), 30_000);
        detector.Update(new PhysicalPoint(101, 101), 60_000);
        detector.Update(new PhysicalPoint(500, 500), 90_000);
        Check.That(fixationStarted).IsFalse();
    }

    [Fact]
    public void GetProgress_BeforeAnyUpdate_ReturnsZero()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        Check.That(detector.GetProgress()).IsEqualTo(0.0);
    }

    [Fact]
    public void GetProgress_DuringFixationTracking_ReturnsElapsedRatio()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 100_000); // 100ms of 200ms = 0.5

        // Not yet fixated (100ms < 200ms), but progress should be ~0.5
        Check.That(detector.IsFixating).IsFalse();
        Check.That(detector.GetProgress()).IsCloseTo(0.5, 0.1);
    }

    [Fact]
    public void GetProgress_AfterGazeMoves_ReturnsZero()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 100_000); // 100ms in
        detector.Update(new PhysicalPoint(500, 500), 130_000); // gaze moves away

        Check.That(detector.GetProgress()).IsEqualTo(0.0);
    }

    [Fact]
    public void GetProgress_ClampsAtOne()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 300_000); // 300ms > 200ms

        Check.That(detector.IsFixating).IsTrue();
        Check.That(detector.GetProgress()).IsEqualTo(1.0);
    }
}
