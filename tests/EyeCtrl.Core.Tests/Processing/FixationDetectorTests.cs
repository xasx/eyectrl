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
    public void GetProgress_DuringFixationTracking_ReturnsCreditedRatio()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        // Ticket 3's clock is the CREDITED age: each dt is capped at AgeCreditCapMs (66 ms), so the
        // 100 ms inter-sample step below credits 66 ms of the 200 ms requirement, not 100 ms.
        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 100_000);

        Check.That(detector.IsFixating).IsFalse();
        Check.That(detector.CreditedAgeUs).IsEqualTo(FixationDetector.AgeCreditCapMs * 1000L);
        Check.That(detector.GetProgress()).IsCloseTo(66.0 / 200.0, 1e-9);
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

        // Credited age is per-sample capped, so the requirement is reached by real samples, not one jump.
        long ts = 0;
        for (int i = 0; i < 8; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), ts);
            ts += 30_000;
        }

        Check.That(detector.IsFixating).IsTrue();
        Check.That(detector.GetProgress()).IsEqualTo(1.0);
    }
}
