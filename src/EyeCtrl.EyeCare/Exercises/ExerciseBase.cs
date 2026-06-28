namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>Shared base: gaze-sample buffering against a moving target, accuracy/steadiness math.</summary>
public abstract class ExerciseBase : IExercise
{
    private readonly ExerciseMetrics _metrics = new();
    private long? _firstTimestampUs;
    private long? _lastTimestampUs;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract double OnTargetRadiusPixels { get; }

    /// <inheritdoc />
    public abstract PhysicalPoint GetTargetAt(long timestampMicroseconds);

    /// <inheritdoc />
    public virtual void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds)
    {
        _firstTimestampUs ??= timestampMicroseconds;
        _lastTimestampUs = timestampMicroseconds;
        _metrics.RecordDelta(gaze, GetTargetAt(timestampMicroseconds));
    }

    /// <summary>Records a reaction-time sample (saccade exercises only).</summary>
    protected void RecordReactionTime(long latencyMicroseconds) => _metrics.RecordReactionTime(latencyMicroseconds);

    /// <summary>Builds the result using the shared metrics + the exercise's own encouragement line.</summary>
    protected ExerciseResult BuildResult(double? reactionTimeOverride = null)
    {
        var durationSec = (_lastTimestampUs - _firstTimestampUs) / 1_000_000.0 ?? 0.0;
        var rt = reactionTimeOverride ?? _metrics.ComputeReactionTimeMs();
        return new ExerciseResult(
            ExerciseName: Name,
            AccuracyPercent: _metrics.ComputeAccuracyPercent(OnTargetRadiusPixels),
            Steadiness0To5: _metrics.ComputeSteadiness0To5(),
            ReactionTimeMs: rt,
            DurationSeconds: durationSec,
            EncouragementLine: EncouragementLine);
    }

    /// <summary>Short encouraging line for the summary card. Override per exercise.</summary>
    protected abstract string EncouragementLine { get; }

    /// <inheritdoc />
    public abstract ExerciseResult Complete();

    /// <inheritdoc />
    /// <remarks>Override to also clear subclass state, calling <c>base.Reset()</c>.</remarks>
    public virtual void Reset()
    {
        _metrics.Reset();
        _firstTimestampUs = null;
        _lastTimestampUs = null;
    }
}
