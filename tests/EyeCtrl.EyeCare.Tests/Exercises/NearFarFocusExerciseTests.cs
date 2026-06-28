namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

public class NearFarFocusExerciseTests
{
    private static readonly PhysicalPoint Near = new(500, 500);
    private static readonly PhysicalPoint Far = new(1500, 900);

    private static NearFarFocusExercise New() =>
        new(near: Near, far: Far, dwellMs: 3000, onTargetRadiusPixels: 60);

    [Fact]
    public void GetTargetAt_NearPhase_ReturnsNear()
    {
        var ex = New();
        var t = ex.GetTargetAt(1_000_000); // 1ms in µs — near phase
        Check.That(t).IsEqualTo(Near);
    }

    [Fact]
    public void GetTargetAt_FarPhase_ReturnsFar()
    {
        var ex = New();
        var t = ex.GetTargetAt(4_000_000);
        Check.That(t).IsEqualTo(Far);
    }

    [Fact]
    public void RecordGaze_TrackingTarget_ThenComplete_HighAccuracy()
    {
        var ex = New();
        ex.RecordGaze(Near, 100_000);
        ex.RecordGaze(Near, 2_000_000);
        ex.RecordGaze(Far, 4_500_000);
        ex.RecordGaze(Far, 5_500_000); // still in far phase (3-6s)

        var result = ex.Complete();

        Check.That(result.AccuracyPercent).IsEqualTo(100.0);
        Check.That(result.ExerciseName).IsEqualTo("Near-Far Focus");
    }

    [Fact]
    public void Complete_AlwaysOffTarget_LowAccuracy()
    {
        var ex = New();
        ex.RecordGaze(new PhysicalPoint(0, 0), 100_000);
        ex.RecordGaze(new PhysicalPoint(0, 0), 4_500_000);

        var result = ex.Complete();

        Check.That(result.AccuracyPercent).IsStrictlyLessThan(50.0);
    }
}
