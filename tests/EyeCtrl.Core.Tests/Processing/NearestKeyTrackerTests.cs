namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class NearestKeyTrackerTests
{
    [Fact]
    public void FindsNearestKey_ByDistance()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 500);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(200, 100),
            ["c"] = new(300, 100),
        });

        var result = tracker.Update(new ScreenPoint(110, 105), 0);

        Assert.Equal("a", result.NearestKeyId);
    }

    [Fact]
    public void AccumulatesDwell_WhenNearestKeyStaysTheSame()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 1000);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(200, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        var result = tracker.Update(new ScreenPoint(105, 100), 500_000); // 500ms

        Assert.Equal("a", result.NearestKeyId);
        Assert.True(result.Progress > 0.4 && result.Progress < 0.6);
        Assert.False(result.Activated);
    }

    [Fact]
    public void ActivatesKey_WhenDwellTimeExceeded()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 100);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        var result = tracker.Update(new ScreenPoint(100, 100), 200_000); // 200ms > 100ms

        Assert.True(result.Activated);
        Assert.Equal("a", result.NearestKeyId);
    }

    [Fact]
    public void ResetsDwell_WhenNearestKeyChanges()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 1000);
        tracker.SetKeys(new Dictionary<string, ScreenPoint>
        {
            ["a"] = new(100, 100),
            ["b"] = new(300, 100),
        });

        tracker.Update(new ScreenPoint(100, 100), 0);
        tracker.Update(new ScreenPoint(100, 100), 400_000); // 400ms on 'a'

        // Now move to 'b'
        var result = tracker.Update(new ScreenPoint(300, 100), 500_000);

        Assert.Equal("b", result.NearestKeyId);
        Assert.Equal(0, result.Progress); // reset
    }

    [Fact]
    public void ReturnsNull_WhenNoKeys()
    {
        var tracker = new NearestKeyTracker(dwellTimeMs: 500);
        var result = tracker.Update(new ScreenPoint(100, 100), 0);

        Assert.Null(result.NearestKeyId);
    }
}
