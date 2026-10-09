namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Exercise 4: saccade jumps. One target lights at a time; user snaps gaze to it.
/// Score: saccade latency (reaction time from target appearance to gaze acquisition).</summary>
public sealed class SaccadeExercise : ExerciseBase
{
    private readonly PhysicalPoint[] _targets;
    private readonly long _dwellPerTargetMicroseconds;
    private long? _currentTargetStartUs;
    private bool _acquiredThisTarget;

    public SaccadeExercise(PhysicalPoint[] targets, int dwellPerTargetMs, double onTargetRadiusPixels)
    {
        _targets = targets;
        _dwellPerTargetMicroseconds = dwellPerTargetMs * MathConstants.MicrosecondsPerMillisecond;
        OnTargetRadiusPixels = onTargetRadiusPixels;
    }

    public override string Name => StringTable.Current.T("eye-care.exercise.saccade.name");
    public override double OnTargetRadiusPixels { get; }
    protected override string EncouragementLine =>
        StringTable.Current.T("eye-care.exercise.saccade.encouragement");

    public override PhysicalPoint GetTargetAt(long timestampMicroseconds)
    {
        var index = (int)(timestampMicroseconds / _dwellPerTargetMicroseconds);
        return _targets[index % _targets.Length];
    }

    public override void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        // Track first + last timestamps for duration (base handles accuracy deltas)
        base.RecordGaze(gaze, timestampMicroseconds);

        var target = GetTargetAt(timestampMicroseconds);
        var targetStart = (timestampMicroseconds / _dwellPerTargetMicroseconds) * _dwellPerTargetMicroseconds;

        if (_currentTargetStartUs != targetStart)
        {
            _currentTargetStartUs = targetStart;
            _acquiredThisTarget = false;
        }

        // On-target acquisition: record latency once per target
        if (!_acquiredThisTarget)
        {
            var dx = gaze.X - target.X;
            var dy = gaze.Y - target.Y;
            if ((dx * dx + dy * dy) <= OnTargetRadiusPixels * OnTargetRadiusPixels)
            {
                var latency = timestampMicroseconds - targetStart;
                if (latency > 0)
                    RecordReactionTime(latency);
                _acquiredThisTarget = true;
            }
        }
    }

    public override ExerciseResult Complete() => BuildResult();

    public override void Reset()
    {
        base.Reset();
        _currentTargetStartUs = null;
        _acquiredThisTarget = false;
    }
}
