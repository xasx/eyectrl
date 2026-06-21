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
        Check.That(result.X).IsEqualTo(100.0);
        Check.That(result.Y).IsEqualTo(200.0);
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastValidPoint()
    {
        var filter = new DirectFilter();
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 1, false));
        Check.That(result.X).IsEqualTo(100.0);
        Check.That(result.Y).IsEqualTo(200.0);
    }
}
