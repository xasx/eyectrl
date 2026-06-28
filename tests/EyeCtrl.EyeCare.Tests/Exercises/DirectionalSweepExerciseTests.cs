namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

public class DirectionalSweepExerciseTests
{
    private static readonly PhysicalPoint TopLeft = new(100, 100);
    private static readonly PhysicalPoint BottomRight = new(1820, 980);

    private static DirectionalSweepExercise New() =>
        new(TopLeft, BottomRight, sweepDurationMs: 3000, onTargetRadiusPixels: 100);

    [Fact]
    public void GetTargetAt_TZero_ReturnsLeftEdge()
    {
        var ex = New();
        var t = ex.GetTargetAt(0);
        Check.That(t.X).IsCloseTo(TopLeft.X, 0.01);
        Check.That(t.Y).IsCloseTo(TopLeft.Y, 0.01);
    }

    [Fact]
    public void GetTargetAt_HalfwayToRightEdge_ReturnsRightEdge()
    {
        var ex = New();
        var t = ex.GetTargetAt(1_500_000); // 1.5s = halfway through ping-pong → right edge
        Check.That(t.X).IsCloseTo(BottomRight.X, 1.0);
    }

    [Fact]
    public void Complete_TrackingSweeps_HighAccuracy()
    {
        var ex = New();
        for (long t = 0; t < 9_000_000; t += 100_000)
            ex.RecordGaze(ex.GetTargetAt(t), t);

        var result = ex.Complete();
        Check.That(result.AccuracyPercent).IsEqualTo(100.0);
    }
}
