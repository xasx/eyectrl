namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>UI-agnostic hooks the EyeCare service calls into to surface overlays.
/// Uses only Core types (no EyeCare types) to avoid a Core→EyeCare reference.
/// The host (App) implements this; a future plugin supplies the logic.</summary>
public interface IEyeCareHooks
{
    /// <summary>Shows the top-edge dwellable confirmation banner.</summary>
    /// <param name="message">Banner text (e.g. "Time for an eye break").</param>
    /// <returns>Acceptance: true if the user dwelled Start, false if snoozed/auto-dismissed.</returns>
    Task<bool> ShowBannerAsync(string message);

    /// <summary>Runs an exercise: full-screen moving target + live gaze cursor. The host owns the
    /// UI/animation; the EyeCare service owns the motion + scoring and supplies them via callbacks
    /// (this keeps <c>IExercise</c>/<c>ExerciseResult</c> out of Core — only Core types cross the seam).</summary>
    /// <param name="exerciseName">Display name (HUD label).</param>
    /// <param name="durationMicroseconds">Exercise length.</param>
    /// <param name="getTargetAt">Target position at an <b>elapsed</b> time (µs since exercise start).</param>
    /// <param name="recordGaze">Records a gaze sample (point + elapsed µs) for the service to score.</param>
    Task ShowExerciseAsync(
        string exerciseName,
        long durationMicroseconds,
        Func<long, PhysicalPoint> getTargetAt,
        Action<PhysicalPoint, long> recordGaze);

    /// <summary>Shows the 3-second summary card with the two computed metrics.</summary>
    Task ShowSummaryAsync(EyeCareExerciseOutcome outcome);

    /// <summary>Immediately dismisses any visible banner/exercise/summary overlay (e.g. when the
    /// user toggles eye-care off mid-session). Safe to call when nothing is showing.</summary>
    void CancelActive();
}

/// <summary>Primitive outcome of an exercise, for the summary card.
/// A Core-side mirror of EyeCare's <c>ExerciseResult</c>; <c>EyeCareService</c> maps between them.</summary>
public record EyeCareExerciseOutcome(
    string ExerciseName,
    double AccuracyPercent,
    double Steadiness0To5,
    double? ReactionTimeMs,
    double DurationSeconds,
    string EncouragementLine);
