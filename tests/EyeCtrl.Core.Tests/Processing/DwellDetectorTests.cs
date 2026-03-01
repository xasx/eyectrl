namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellDetectorTests
{
    [Fact]
    public void Update_DwellOnTarget_RaisesActivated()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        string? activatedId = null;
        detector.TargetActivated += (id) => { activated = true; activatedId = id; };

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new ScreenPoint(105, 105), time);
            time += 30_000;
        }

        Assert.True(activated);
        Assert.Equal("test", activatedId);
    }

    [Fact]
    public void Update_GazeLeavesTarget_ResetsProgress()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        detector.TargetActivated += (id) => activated = true;

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 30_000);
        detector.Update(new ScreenPoint(100, 100), 60_000);
        detector.Update(new ScreenPoint(500, 500), 90_000); // leave
        detector.Update(new ScreenPoint(100, 100), 120_000);
        detector.Update(new ScreenPoint(100, 100), 150_000);
        detector.Update(new ScreenPoint(100, 100), 180_000);

        Assert.False(activated);
    }

    [Fact]
    public void GetProgress_ReturnsDwellFraction()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 300);
        detector.AddTarget(target);

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 150_000);

        double progress = detector.GetProgress("test");
        Assert.InRange(progress, 0.4, 0.6);
    }

    [Fact]
    public void Update_OverlappingTargets_OnlyNearestAccumulatesDwell()
    {
        var detector = new DwellDetector();
        // Two targets at (100,100) and (130,100) with radius 40 — they overlap
        var targetA = new DwellTarget("A", new ScreenPoint(100, 100), radius: 40, dwellTimeMs: 200);
        var targetB = new DwellTarget("B", new ScreenPoint(130, 100), radius: 40, dwellTimeMs: 200);
        detector.AddTarget(targetA);
        detector.AddTarget(targetB);

        var progressIds = new List<string>();
        detector.TargetProgress += (id, progress) =>
        {
            if (progress > 0) progressIds.Add(id);
        };

        // Gaze at (110, 100) — inside both circles, but closer to A (dist=10) than B (dist=20)
        long time = 0;
        for (int i = 0; i < 5; i++)
        {
            detector.Update(new ScreenPoint(110, 100), time);
            time += 30_000;
        }

        // Only A should have received positive progress
        Assert.Contains("A", progressIds);
        Assert.DoesNotContain("B", progressIds);
    }
}
