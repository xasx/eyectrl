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
        Check.That(result.X).IsEqualTo(100.0);
        Check.That(result.Y).IsEqualTo(200.0);
    }

    [Fact]
    public void Filter_SecondPoint_IsSmoothed()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(200.0, 400.0, 1, true));
        // EMA: new = factor * raw + (1-factor) * prev
        Check.That(result.X).IsEqualTo(150.0);
        Check.That(result.Y).IsEqualTo(300.0);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        var result = filter.Filter(new GazePoint(500.0, 600.0, 1, true));
        Check.That(result.X).IsEqualTo(500.0);
        Check.That(result.Y).IsEqualTo(600.0);
    }
}
