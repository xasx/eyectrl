namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Exercise 1: alternate gaze between a large near dot and a small far dot, dwelling each.</summary>
public sealed class NearFarFocusExercise : ExerciseBase
{
    private readonly PhysicalPoint _near;
    private readonly PhysicalPoint _far;
    private readonly long _dwellMicroseconds;

    /// <summary>Creates the exercise.</summary>
    /// <param name="near">Near dot position (physical px).</param>
    /// <param name="far">Far dot position (physical px).</param>
    /// <param name="dwellMs">Dwell time per target in ms.</param>
    /// <param name="onTargetRadiusPixels">On-target scoring radius.</param>
    public NearFarFocusExercise(PhysicalPoint near, PhysicalPoint far, int dwellMs, double onTargetRadiusPixels)
    {
        _near = near;
        _far = far;
        _dwellMicroseconds = dwellMs * MathConstants.MicrosecondsPerMillisecond;
        OnTargetRadiusPixels = onTargetRadiusPixels;
    }

    public override string Name => StringTable.Current.T("eye-care.exercise.nearFarFocus.name");
    public override double OnTargetRadiusPixels { get; }
    protected override string EncouragementLine =>
        StringTable.Current.T("eye-care.exercise.nearFarFocus.encouragement");

    public override PhysicalPoint GetTargetAt(long timestampMicroseconds)
    {
        // Phase index alternates near/far every dwell interval. Even phase = near.
        var phase = timestampMicroseconds / _dwellMicroseconds;
        return (phase % 2 == 0) ? _near : _far;
    }

    public override ExerciseResult Complete() => BuildResult();
}
