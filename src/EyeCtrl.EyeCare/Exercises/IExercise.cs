namespace EyeCtrl.EyeCare.Exercises;

using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Scoring;

/// <summary>One gaze-tracked exercise: a target path the user follows, scored on completion.</summary>
public interface IExercise
{
    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>Target position in physical pixels at the given timestamp (µs).</summary>
    PhysicalPoint GetTargetAt(long timestampMicroseconds);

    /// <summary>Radius (px) within which a gaze sample counts as on-target, for accuracy scoring.</summary>
    double OnTargetRadiusPixels { get; }

    /// <summary>Records one gaze sample against the current target.</summary>
    void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds);

    /// <summary>Computes the final result from recorded samples. Called once at exercise end.</summary>
    ExerciseResult Complete();

    /// <summary>Clears all per-run state so the instance can be reused for a fresh break.
    /// Exercises are reused across breaks (singleton pool); without this, scores would
    /// accumulate across runs and the duration clock would never restart.</summary>
    void Reset();
}
