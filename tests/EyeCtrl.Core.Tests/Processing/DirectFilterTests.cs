namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DirectFilterTests
{
    [Fact]
    public void Filter_ReturnsGazePointUnchanged()
    {
        var filter = new DirectFilter();
        var gaze = new GazePoint(100.0, 200.0, 0, true);
        var result = filter.Filter(gaze);
        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastValidPoint()
    {
        var filter = new DirectFilter();
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 1, false));
        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }
}
