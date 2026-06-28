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
}
