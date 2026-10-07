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
        return $"Calibration failed: {failure.Code}.";
    }
}
