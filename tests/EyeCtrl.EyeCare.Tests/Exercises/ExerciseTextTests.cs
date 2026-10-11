namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

/// <summary>
/// T08 fixup R1 (mutants M17, M18): every exercise's display name and encouragement line, pinned
/// as English literals. The suite saw none of these before — <c>NearFarFocusExerciseTests</c>
/// pinned one name and no encouragement — so swapping one exercise's key for another's stayed
/// green while the summary card told the user the wrong exercise.
/// </summary>
public class ExerciseTextTests
{
    private static readonly PhysicalPoint P = new(500, 500);
    private static readonly PhysicalPoint Q = new(1500, 900);

    [Fact]
    public void EveryExercise_CarriesItsKeyedEnglishNameAndEncouragement()
    {
        var rows = new (IExercise Exercise, string Name, string Encouragement)[]
        {
            (new Calibration202020Exercise(P, onTargetRadiusPixels: 40),
                "20-20-20", "Eyes relaxed!"),
            (new DirectionalSweepExercise(P, Q, sweepDurationMs: 3000, onTargetRadiusPixels: 100),
                "Directional Sweeps", "Full range of motion!"),
            (new NearFarFocusExercise(P, Q, dwellMs: 3000, onTargetRadiusPixels: 60),
                "Near-Far Focus", "Great focus switching!"),
            (new SaccadeExercise(new[] { P, Q }, dwellPerTargetMs: 1500, onTargetRadiusPixels: 100),
                "Saccade Jumps", "Quick eyes!"),
            (new SmoothPursuitExercise(P, radius: 300, periodMs: 4000, onTargetRadiusPixels: 80),
                "Smooth Pursuit", "Smooth tracking!"),
        };

        foreach (var (exercise, name, encouragement) in rows)
        {
            var at = exercise.GetType().Name;
            Check.WithCustomMessage($"{at}: the HUD name")
                 .That(exercise.Name).IsEqualTo(name);
            Check.WithCustomMessage($"{at}: the summary's encouragement line")
                 .That(exercise.Complete().EncouragementLine).IsEqualTo(encouragement);
        }
    }
}
