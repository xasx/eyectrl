namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

public class Calibration202020ExerciseTests
{
    private static readonly PhysicalPoint Marker = new(960, 540);

    [Fact]
    public void GetTargetAt_AlwaysReturnsMarker()
    {
        var ex = new Calibration202020Exercise(Marker, onTargetRadiusPixels: 40);
        Check.That(ex.GetTargetAt(0)).IsEqualTo(Marker);
        Check.That(ex.GetTargetAt(20_000_000)).IsEqualTo(Marker);
    }

    [Fact]
    public void Complete_RockSteadyGaze_MaxSteadiness()
    {
        var ex = new Calibration202020Exercise(Marker, onTargetRadiusPixels: 40);
        for (long t = 0; t < 5_000_000; t += 200_000)
            ex.RecordGaze(Marker, t);

        var result = ex.Complete();
        Check.That(result.Steadiness0To5).IsEqualTo(5.0);
    }
}
