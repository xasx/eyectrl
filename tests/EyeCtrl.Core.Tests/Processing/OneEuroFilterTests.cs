namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class OneEuroFilterTests
{
    [Fact]
    public void Filter_FirstPoint_ReturnedDirectly()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        var result = filter.Filter(new GazePoint(500.0, 300.0, 0, true));
        Assert.Equal(500.0, result.X, precision: 1);
        Assert.Equal(300.0, result.Y, precision: 1);
    }

    [Fact]
    public void Filter_StationaryGaze_ConvergesToTarget()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        ScreenPoint result = new(0, 0);
        long timestamp = 33_000;
        for (int i = 0; i < 10; i++)
        {
            result = filter.Filter(new GazePoint(500.0, 500.0, timestamp, true));
            timestamp += 33_000;
        }

        Assert.InRange(result.X, 495.0, 505.0);
        Assert.InRange(result.Y, 495.0, 505.0);
    }

    [Fact]
    public void Filter_HighSpeedMovement_LessSmoothing()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 100.0, 0, true));

        var result = filter.Filter(new GazePoint(800.0, 800.0, 33_000, true));

        Assert.True(result.X > 500.0, $"Expected X > 500 but got {result.X}");
        Assert.True(result.Y > 500.0, $"Expected Y > 500 but got {result.Y}");
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastFiltered()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(200.0, 300.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 33_000, false));
        Assert.Equal(200.0, result.X, precision: 1);
        Assert.Equal(300.0, result.Y, precision: 1);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.07, dCutoff: 1.0);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        var result = filter.Filter(new GazePoint(800.0, 900.0, 100_000, true));
        Assert.Equal(800.0, result.X, precision: 1);
        Assert.Equal(900.0, result.Y, precision: 1);
    }
}
