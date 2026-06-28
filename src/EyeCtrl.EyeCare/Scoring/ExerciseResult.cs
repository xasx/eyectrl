namespace EyeCtrl.EyeCare.Scoring;

/// <summary>Result of one completed exercise, shown on the summary card (two metrics + a line).</summary>
/// <param name="ExerciseName">Display name of the exercise.</param>
/// <param name="AccuracyPercent">0–100 mean on-target percentage during the exercise.</param>
/// <param name="Steadiness0To5">Tracking steadiness / focus-hold quality on a 0–5 scale (0 if N/A).</param>
/// <param name="ReactionTimeMs">Mean saccade latency for saccade exercises; null otherwise.</param>
/// <param name="DurationSeconds">Actual exercise duration.</param>
/// <param name="EncouragementLine">A short encouraging string for the summary card.</param>
public record ExerciseResult(
    string ExerciseName,
    double AccuracyPercent,
    double Steadiness0To5,
    double? ReactionTimeMs,
    double DurationSeconds,
    string EncouragementLine);
