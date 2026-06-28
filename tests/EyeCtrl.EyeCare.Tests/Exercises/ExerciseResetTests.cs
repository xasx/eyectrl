namespace EyeCtrl.EyeCare.Tests.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;

/// <summary>Exercises are reused across breaks (singleton pool), so Reset() must clear all
/// per-run state — otherwise scores average across every past run and duration grows unbounded.</summary>
public class ExerciseResetTests
{
    private static readonly PhysicalPoint Near = new(500, 500);
    private static readonly PhysicalPoint Far = new(1500, 900);

    private static NearFarFocusExercise New() =>
        new(near: Near, far: Far, dwellMs: 3000, onTargetRadiusPixels: 60);

    [Fact]
    public void Reset_DiscardsPriorRunSamples()
    {
        var ex = New();

        // Run 1: entirely off-target.
        ex.RecordGaze(new PhysicalPoint(0, 0), 100_000);
        ex.RecordGaze(new PhysicalPoint(0, 0), 1_000_000);
        Check.That(ex.Complete().AccuracyPercent).IsStrictlyLessThan(50.0);

        ex.Reset();

        // Run 2: entirely on-target (near phase, t < 3s). With state cleared, accuracy reflects
        // ONLY run 2; without Reset it would average run 1 + run 2 ≈ 50%.
        ex.RecordGaze(Near, 100_000);
        ex.RecordGaze(Near, 1_000_000);

        Check.That(ex.Complete().AccuracyPercent).IsEqualTo(100.0);
    }

    [Fact]
    public void Reset_RestartsDurationClock()
    {
        var ex = New();

        // Run 1 spans t = 0 .. 2s.
        ex.RecordGaze(Near, 0);
        ex.RecordGaze(Near, 2_000_000);
        ex.Complete();

        ex.Reset();

        // Run 2 spans t = 10s .. 10.5s → duration must be 0.5s, not 10.5s (first timestamp reset).
        ex.RecordGaze(Near, 10_000_000);
        ex.RecordGaze(Near, 10_500_000);

        Check.That(ex.Complete().DurationSeconds).IsEqualTo(0.5);
    }
}
