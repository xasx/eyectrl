namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

public class SmoothPursuitExerciseTests
{
    private static readonly PhysicalPoint Center = new(800, 500);
    private const double Radius = 300;

    private static SmoothPursuitExercise New() =>
        new(center: Center, radius: Radius, periodMs: 4000, onTargetRadiusPixels: 80);

    [Fact]
    public void GetTargetAt_TZero_ReturnsRightmostPoint()
    {
        var ex = New();
        // At t=0 the angle is 0 → (center + radius, center)
        var t = ex.GetTargetAt(0);
        Check.That(t.X).IsCloseTo(Center.X + Radius, 0.01);
        Check.That(t.Y).IsCloseTo(Center.Y, 0.01);
    }

    [Fact]
    public void GetTargetAt_ThreeQuarterPeriod_ReturnsTop()
    {
        var ex = New();
        var t = ex.GetTargetAt(3_000_000); // 3s = 3/4 of 4s period → top (sin(3π/2) = -1)
        Check.That(t.X).IsCloseTo(Center.X, 0.01);
        Check.That(t.Y).IsCloseTo(Center.Y - Radius, 0.01);
    }

    [Fact]
    public void Complete_TrackingCircle_HighSteadiness()
    {
        var ex = New();
        // Record gaze exactly on target at many timestamps
        for (long t = 0; t < 4_000_000; t += 100_000)
            ex.RecordGaze(ex.GetTargetAt(t), t);

        var result = ex.Complete();
        Check.That(result.Steadiness0To5).IsEqualTo(5.0);
    }
}
