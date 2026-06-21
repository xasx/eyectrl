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

        Check.That(point.Target.X).IsEqualTo(500);
        Check.That(point.Target.Y).IsEqualTo(500);
        Check.That(point.SampleCount).IsEqualTo(3);
        Check.That(Math.Abs(point.MeanGaze.X - 516.67) < 0.1).IsTrue();
        Check.That(Math.Abs(point.MeanGaze.Y - 500.0) < 0.1).IsTrue();
        Check.That(point.StdDevX > 0).IsTrue();
        Check.That(point.StdDevY > 0).IsTrue();
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

        Check.That(point.SampleCount).IsEqualTo(2);
        Check.That(Math.Abs(point.MeanGaze.X - 100) < 10).IsTrue();
        Check.That(Math.Abs(point.MeanGaze.Y - 100) < 10).IsTrue();
    }

    [Fact]
    public void End_Throws_WhenNoSamplesCollected()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(100, 100), settleWindowMs: 200);
        collector.AddRawSample(new PhysicalPoint(100, 100));
        Check.ThatCode(() => collector.End()).Throws<InvalidOperationException>();
    }

    [Fact]
    public void IsCollecting_TrueBeforeEnd_FalseAfterEnd()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(0, 0), settleWindowMs: 0);
        collector.AddRawSample(new PhysicalPoint(5, 5));
        Check.That(collector.IsCollecting).IsTrue();
        collector.End();
        Check.That(collector.IsCollecting).IsFalse();
    }

    [Fact]
    public void MaxSamples_CapsSampleCount()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(0, 0), settleWindowMs: 0, maxSamples: 10);
        for (int i = 0; i < 100; i++)
            collector.AddRawSample(new PhysicalPoint(1, 1));

        var point = collector.End();
        Check.That(point.SampleCount).IsEqualTo(10);
    }
}
