namespace EyeCtrl.EyeCare.Tests.Scoring;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

public class ExerciseMetricsTests
{
    [Fact]
    public void ComputeAccuracyPercent_AllOnTarget_Returns100()
    {
        var m = new ExerciseMetrics();
        m.RecordDelta(new PhysicalPoint(100, 100), new PhysicalPoint(101, 99)); // within 5px
        m.RecordDelta(new PhysicalPoint(200, 200), new PhysicalPoint(202, 198));

        Check.That(m.ComputeAccuracyPercent(onTargetRadiusPixels: 5)).IsEqualTo(100.0);
    }

    [Fact]
    public void ComputeAccuracyPercent_AllOffTarget_Returns0()
    {
        var m = new ExerciseMetrics();
        m.RecordDelta(new PhysicalPoint(0, 0), new PhysicalPoint(500, 500));

        Check.That(m.ComputeAccuracyPercent(onTargetRadiusPixels: 5)).IsEqualTo(0.0);
    }

    [Fact]
    public void ComputeSteadiness0To5_ZeroError_Returns5()
    {
        var m = new ExerciseMetrics();
        m.RecordDelta(new PhysicalPoint(50, 50), new PhysicalPoint(50, 50));
        m.RecordDelta(new PhysicalPoint(60, 60), new PhysicalPoint(60, 60));

        Check.That(m.ComputeSteadiness0To5()).IsEqualTo(5.0);
    }

    [Fact]
    public void ComputeReactionTimeMs_AveragesCorrectly()
    {
        var m = new ExerciseMetrics();
        m.RecordReactionTime(200_000); // 200ms
        m.RecordReactionTime(300_000); // 300ms

        Check.That(m.ComputeReactionTimeMs()).IsEqualTo(250.0);
    }
}
