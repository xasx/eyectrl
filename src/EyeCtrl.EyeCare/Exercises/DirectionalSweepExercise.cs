namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Exercise 5: directional sweeps. A dot sweeps edge-to-edge across horizontal, vertical, diagonal.</summary>
public sealed class DirectionalSweepExercise : ExerciseBase
{
    private readonly PhysicalPoint _topLeft;
    private readonly PhysicalPoint _bottomRight;
    private readonly long _sweepMicroseconds;

    /// <param name="topLeft">Bounding-box top-left (physical px).</param>
    /// <param name="bottomRight">Bounding-box bottom-right (physical px).</param>
    /// <param name="sweepDurationMs">Duration of one sweep leg in ms.</param>
    /// <param name="onTargetRadiusPixels">On-target scoring radius.</param>
    public DirectionalSweepExercise(PhysicalPoint topLeft, PhysicalPoint bottomRight, int sweepDurationMs, double onTargetRadiusPixels)
    {
        _topLeft = topLeft;
        _bottomRight = bottomRight;
        _sweepMicroseconds = sweepDurationMs * MathConstants.MicrosecondsPerMillisecond;
        OnTargetRadiusPixels = onTargetRadiusPixels;
    }

    public override string Name => "Directional Sweeps";
    public override double OnTargetRadiusPixels { get; }
    protected override string EncouragementLine => "Full range of motion!";

    public override PhysicalPoint GetTargetAt(long timestampMicroseconds)
    {
        // Three legs: horizontal, vertical, diagonal. Each leg = one _sweepMicroseconds.
        var leg = (int)(timestampMicroseconds / _sweepMicroseconds);
        var frac = (double)(timestampMicroseconds % _sweepMicroseconds) / _sweepMicroseconds;
        // ping-pong within a leg for a smooth there-and-back
        if (frac > 0.5) frac = (1.0 - frac) * 2.0; else frac *= 2.0;

        var x = _topLeft.X + (_bottomRight.X - _topLeft.X) * frac;
        var y = _topLeft.Y + (_bottomRight.Y - _topLeft.Y) * frac;

        return (leg % 3) switch
        {
            0 => new PhysicalPoint(x, _topLeft.Y),                 // horizontal
            1 => new PhysicalPoint(_topLeft.X, y),                 // vertical
            _ => new PhysicalPoint(x, y),                          // diagonal
        };
    }

    public override ExerciseResult Complete() => BuildResult();
}
