namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationPointCollectorTests
{
    [Fact]
    public void End_ReturnsCalibrationPoint_WithAveragedGaze()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(500, 500), settleWindowMs: 0);

        collector.AddRawSample(new PhysicalPoint(510, 490));
        collector.AddRawSample(new PhysicalPoint(530, 510));
        collector.AddRawSample(new PhysicalPoint(510, 500));

        var point = collector.End();

        Assert.Equal(500, point.Target.X);
        Assert.Equal(500, point.Target.Y);
        Assert.Equal(3, point.SampleCount);
        Assert.True(Math.Abs(point.MeanGaze.X - 516.67) < 0.1);
        Assert.True(Math.Abs(point.MeanGaze.Y - 500.0) < 0.1);
        Assert.True(point.StdDevX > 0);
        Assert.True(point.StdDevY > 0);
    }

    [Fact]
    public void SettleWindow_DiscardsEarlySamples()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(100, 100), settleWindowMs: 50);

        collector.AddRawSample(new PhysicalPoint(999, 999));

        Thread.Sleep(60);

        collector.AddRawSample(new PhysicalPoint(105, 95));
        collector.AddRawSample(new PhysicalPoint(95, 105));

        var point = collector.End();

        Assert.Equal(2, point.SampleCount);
        Assert.True(Math.Abs(point.MeanGaze.X - 100) < 10);
        Assert.True(Math.Abs(point.MeanGaze.Y - 100) < 10);
    }

    [Fact]
    public void End_Throws_WhenNoSamplesCollected()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(100, 100), settleWindowMs: 200);
        collector.AddRawSample(new PhysicalPoint(100, 100));
        Assert.Throws<InvalidOperationException>(() => collector.End());
    }

    [Fact]
    public void IsCollecting_TrueBeforeEnd_FalseAfterEnd()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(0, 0), settleWindowMs: 0);
        collector.AddRawSample(new PhysicalPoint(5, 5));
        Assert.True(collector.IsCollecting);
        collector.End();
        Assert.False(collector.IsCollecting);
    }

    [Fact]
    public void MaxSamples_CapsSampleCount()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(0, 0), settleWindowMs: 0, maxSamples: 10);
        for (int i = 0; i < 100; i++)
            collector.AddRawSample(new PhysicalPoint(1, 1));

        var point = collector.End();
        Assert.Equal(10, point.SampleCount);
    }
}
