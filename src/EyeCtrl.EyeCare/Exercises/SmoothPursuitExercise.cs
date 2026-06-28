namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Exercise 2: smooth pursuit. Round 1 = circle orbit; round 2 = figure-8 (lemniscate).</summary>
public sealed class SmoothPursuitExercise : ExerciseBase
{
    private readonly PhysicalPoint _center;
    private readonly double _radius;
    private readonly long _periodMicroseconds;

    /// <summary>Creates the exercise.</summary>
    /// <param name="center">Orbit center (physical px).</param>
    /// <param name="radius">Orbit radius (px).</param>
    /// <param name="periodMs">Milliseconds per full revolution.</param>
    /// <param name="onTargetRadiusPixels">On-target scoring radius.</param>
    public SmoothPursuitExercise(PhysicalPoint center, double radius, int periodMs, double onTargetRadiusPixels)
    {
        _center = center;
        _radius = radius;
        _periodMicroseconds = periodMs * MathConstants.MicrosecondsPerMillisecond;
        OnTargetRadiusPixels = onTargetRadiusPixels;
    }

    public override string Name => "Smooth Pursuit";
    public override double OnTargetRadiusPixels { get; }
    protected override string EncouragementLine => "Smooth tracking!";

    public override PhysicalPoint GetTargetAt(long timestampMicroseconds)
    {
        var period = _periodMicroseconds;
        var revolutions = (double)timestampMicroseconds / period;
        var theta = 2.0 * Math.PI * revolutions;

        // First half of the exercise: circle. Second half: figure-8 (lemniscate of Gerono).
        bool isFigure8 = revolutions >= 1.0;
        if (!isFigure8)
        {
            return new PhysicalPoint(
                _center.X + _radius * Math.Cos(theta),
                _center.Y + _radius * Math.Sin(theta));
        }
        else
        {
            // Lemniscate: x = a*cos(t), y = a*sin(t)*cos(t)
            var tt = theta - 2.0 * Math.PI; // continue phase from where circle ended
            return new PhysicalPoint(
                _center.X + _radius * Math.Cos(tt),
                _center.Y + (_radius * 0.5) * Math.Sin(2.0 * tt));
        }
    }

    public override ExerciseResult Complete() => BuildResult();
}
