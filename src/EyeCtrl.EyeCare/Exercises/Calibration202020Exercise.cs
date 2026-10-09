namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Exercise 3: 20-20-20. Hold steady gaze on a centered marker; relax at distance.</summary>
public sealed class Calibration202020Exercise : ExerciseBase
{
    private readonly PhysicalPoint _marker;

    public Calibration202020Exercise(PhysicalPoint marker, double onTargetRadiusPixels)
    {
        _marker = marker;
        OnTargetRadiusPixels = onTargetRadiusPixels;
    }

    public override string Name => StringTable.Current.T("eye-care.exercise.calibration202020.name");
    public override double OnTargetRadiusPixels { get; }
    protected override string EncouragementLine =>
        StringTable.Current.T("eye-care.exercise.calibration202020.encouragement");

    public override PhysicalPoint GetTargetAt(long timestampMicroseconds) => _marker;

    public override ExerciseResult Complete() => BuildResult();
}
