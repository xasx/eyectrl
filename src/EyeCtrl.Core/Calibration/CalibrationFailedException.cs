namespace EyeCtrl.Core.Calibration;

/// <summary>
/// A calibration session ended in a failure the user must be told about, carrying the typed
/// <see cref="Failure"/> rather than a user-facing sentence.
/// <para>
/// <see cref="Exception.Message"/> is deliberately technical (the code, not the sentence) and exists
/// for logs: the calibration window renders the user's words from the failure, which is what makes
/// them translatable later. Derives from <see cref="InvalidOperationException"/> so callers that
/// treated every calibration fault that way keep working.
/// </para>
/// </summary>
public sealed class CalibrationFailedException : InvalidOperationException
{
    /// <summary>
    /// Initializes the exception from the failure the renderer will describe.
    /// </summary>
    /// <param name="failure">The typed failure that ended the session.</param>
    /// <param name="innerException">The technical fault behind an <see cref="CalibrationFailureCode.Unexpected"/> failure, kept for logs.</param>
    public CalibrationFailedException(CalibrationFailure failure, Exception? innerException = null)
        : base(TechnicalMessage(failure), innerException)
    {
        Failure = failure;
    }

    /// <summary>The typed failure the calibration window renders.</summary>
    public CalibrationFailure Failure { get; }

    private static string TechnicalMessage(CalibrationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // For the quality gate the per-target numbers ARE the diagnostics this user has: the old
        // user-facing message carried them, and "Calibration failed: Quality." alone would drop the
        // region, the metric and the two values when the log is read after a failed session.
        if (failure.Code == CalibrationFailureCode.Quality
            && failure.Arguments.Count == 1
            && failure.Arguments[0] is IReadOnlyList<CalibrationQualityFailure> targets
            && targets.Count > 0)
        {
            return $"Calibration failed: {failure.Code}. "
                 + string.Join("; ", targets.Select(t =>
                     $"target {t.TargetIndex} {t.Region}: {t.Reason} (value {t.Value:F0}, limit {t.Limit:F0})"));
        }

        return $"Calibration failed: {failure.Code}.";
    }
}
