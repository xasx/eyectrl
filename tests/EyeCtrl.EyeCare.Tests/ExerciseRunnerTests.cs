namespace EyeCtrl.EyeCare.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;

public class ExerciseRunnerTests
{
    private sealed class StubExercise : ExerciseBase
    {
        private readonly string _name;
        public StubExercise(string name) { _name = name; OnTargetRadiusPixels = 10; }
        public override string Name => _name;
        public override double OnTargetRadiusPixels { get; }
        protected override string EncouragementLine => "ok";
        public override PhysicalPoint GetTargetAt(long timestampMicroseconds) => new(0, 0);
        public override ExerciseResult Complete() => BuildResult();
    }

    [Fact]
    public void NextExercise_NeverRepeatsConsecutively()
    {
        var pool = new IExercise[] { new StubExercise("A"), new StubExercise("B"), new StubExercise("C") };
        var runner = new ExerciseRunner(pool, seed: 42);

        var first = runner.NextExercise();
        var second = runner.NextExercise();
        var third = runner.NextExercise();

        Check.That(second).IsNotEqualTo(first);
        Check.That(third).IsNotEqualTo(second);
    }

    [Fact]
    public void NextExercise_WithSingleExercise_StillReturnsIt()
    {
        var pool = new IExercise[] { new StubExercise("only") };
        var runner = new ExerciseRunner(pool, seed: 1);
        Check.That(runner.NextExercise().Name).IsEqualTo("only");
    }

    [Fact]
    public void NextExercise_PoolsFromAllFive_Works()
    {
        var pool = new IExercise[]
        {
            new NearFarFocusExercise(new(0,0), new(100,100), 1000, 10),
            new SmoothPursuitExercise(new(50,50), 30, 1000, 10),
            new Calibration202020Exercise(new(0,0), 10),
            new SaccadeExercise(new[] { new PhysicalPoint(0,0), new(100,100) }, 500, 10),
            new DirectionalSweepExercise(new(0,0), new(100,100), 500, 10),
        };
        var runner = new ExerciseRunner(pool, seed: 7);
        for (int i = 0; i < 20; i++)
        {
            var cur = runner.NextExercise();
            Check.That(cur).IsNotNull();
        }
    }
}
