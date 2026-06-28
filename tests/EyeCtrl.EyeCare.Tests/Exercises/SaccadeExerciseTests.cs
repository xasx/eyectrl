namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

public class SaccadeExerciseTests
{
    private static readonly PhysicalPoint[] Targets =
    {
        new(200, 200), new(1600, 300), new(400, 800), new(1500, 900)
    };

    private static SaccadeExercise New() => new(Targets, dwellPerTargetMs: 1500, onTargetRadiusPixels: 100);

    [Fact]
    public void GetTargetAt_RoundsThroughTargets()
    {
        var ex = New();
        Check.That(ex.GetTargetAt(0)).IsEqualTo(Targets[0]);
        Check.That(ex.GetTargetAt(1_500_000)).IsEqualTo(Targets[1]); // 1.5s in
        Check.That(ex.GetTargetAt(3_000_000)).IsEqualTo(Targets[2]);
        Check.That(ex.GetTargetAt(4_500_000)).IsEqualTo(Targets[3]);
    }

    [Fact]
    public void Complete_AcquiringEachTarget_RecordsReactionTime()
    {
        var ex = New();
        // Acquire each target 200ms after it appears
        ex.RecordGaze(Targets[0], 200_000);   // target 0 appeared at 0
        ex.RecordGaze(Targets[1], 1_700_000); // target 1 appeared at 1.5s
        ex.RecordGaze(Targets[2], 3_200_000); // target 2 appeared at 3.0s

        var result = ex.Complete();

        Check.That(result.ReactionTimeMs).IsNotNull();
        Check.That(result.ReactionTimeMs!.Value).IsCloseTo(200.0, 1.0);
    }
}
