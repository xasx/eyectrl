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
            detector.Update(new ScreenPoint(100 + i % 3, 100 + i % 3), time);
            time += 30_000; // microseconds
        }
        Assert.True(fixationStarted);
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
            detector.Update(new ScreenPoint(100, 100), time);
            time += 30_000;
        }
        detector.Update(new ScreenPoint(500, 500), time);
        Assert.True(fixationEnded);
    }

    [Fact]
    public void Update_GazeMovesBeforeThreshold_NoFixation()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        bool fixationStarted = false;
        detector.FixationStarted += (point) => fixationStarted = true;

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(102, 102), 30_000);
        detector.Update(new ScreenPoint(101, 101), 60_000);
        detector.Update(new ScreenPoint(500, 500), 90_000);
        Assert.False(fixationStarted);
    }

    [Fact]
    public void GetProgress_BeforeAnyUpdate_ReturnsZero()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);
        Assert.Equal(0.0, detector.GetProgress());
    }

    [Fact]
    public void GetProgress_DuringFixationTracking_ReturnsElapsedRatio()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 100_000); // 100ms of 200ms = 0.5

        // Not yet fixated (100ms < 200ms), but progress should be ~0.5
        Assert.False(detector.IsFixating);
        Assert.Equal(0.5, detector.GetProgress(), precision: 1);
    }

    [Fact]
    public void GetProgress_AfterGazeMoves_ReturnsZero()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 100_000); // 100ms in
        detector.Update(new ScreenPoint(500, 500), 130_000); // gaze moves away

        Assert.Equal(0.0, detector.GetProgress());
    }

    [Fact]
    public void GetProgress_ClampsAtOne()
    {
        var detector = new FixationDetector(fixationRadiusPixels: 50, fixationTimeMs: 200);

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 300_000); // 300ms > 200ms

        Assert.True(detector.IsFixating);
        Assert.Equal(1.0, detector.GetProgress());
    }
}
