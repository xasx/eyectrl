namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class SmoothedFilterTests
{
    [Fact]
    public void Filter_FirstPoint_ReturnedDirectly()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        var result = filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }

    [Fact]
    public void Filter_SecondPoint_IsSmoothed()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(200.0, 400.0, 1, true));
        // EMA: new = factor * raw + (1-factor) * prev
        Assert.Equal(150.0, result.X);
        Assert.Equal(300.0, result.Y);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        var result = filter.Filter(new GazePoint(500.0, 600.0, 1, true));
        Assert.Equal(500.0, result.X);
        Assert.Equal(600.0, result.Y);
    }
}
