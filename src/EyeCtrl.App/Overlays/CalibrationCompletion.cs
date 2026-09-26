namespace EyeCtrl.App.Overlays;

/// <summary>Whether the completion panel is a success or a warning, as a tone the overlay maps to a brush.</summary>
internal enum CompletionTone
{
    Success,
    Warning,
}

/// <summary>
/// What the calibration overlay's completion panel shows: symbol, tone, title, detail line, whether
/// Retry is offered, whether the hint is shown, and how long the panel lingers before dismissing
/// itself.
/// </summary>
internal readonly record struct CalibrationCompletionPresentation(
    string Symbol,
    CompletionTone Tone,
    string Title,
    string Detail,
    bool RetryVisible,
    bool HintVisible,
    int AutoDismissMs);

/// <summary>
/// The completion panel's copy and visibility rules, as a pure function of the session's state.
/// <para>
/// Extracted from <c>CalibrationWindow.ShowCompletion</c> so the measure-only ending — which must
/// never say "failed" and must never offer a retry — has a headless test. The window itself calls
/// <c>Show()</c>, <c>Activate()</c> and <c>Focus()</c>, so an STA test of it would create an HWND and
/// seize the user's only input surface; the text choice is the part worth pinning, and it needs none
/// of that.
/// </para>
/// </summary>
internal static class CalibrationCompletion
{
    /// <summary>The neutral title a measure-only probe ends with — never "failed", never "recalibrate".</summary>
    public const string MeasureOnlyTitle = "Gaze offset recorded";

    /// <summary>Shipped quality bar: below this (2 % of the screen diagonal) a successful fit is "good".</summary>
    public const double GoodQualityPercent = 2.0;

    /// <summary>How long a clean ending (or a probe's neutral one) lingers before it dismisses itself.</summary>
    public const int GoodAutoDismissMs = 3_000;

    /// <summary>How long a poor or failed ending lingers; long enough to read, not long enough to trap.</summary>
    public const int PoorAutoDismissMs = 12_000;

    public static CalibrationCompletionPresentation Describe(
        bool measureOnly, bool goodResult, double errorPx, double qualityPercent, string? errorText)
    {
        if (measureOnly)
        {
            // Neutral by contract: no quality arithmetic (a probe fits nothing, so quality is 0) and
            // no retry. Auto-dismiss is the only way out for a user who cannot actuate Accept.
            return new("\u25CF", CompletionTone.Success, MeasureOnlyTitle, "",
                RetryVisible: false, HintVisible: false, AutoDismissMs: GoodAutoDismissMs);
        }

        bool isGood = qualityPercent < GoodQualityPercent;
        if (goodResult && isGood)
        {
            return new("\u2713", CompletionTone.Success, "Calibration complete",
                $"Mean error: {errorPx:F1} px ({qualityPercent:F1}%)",
                RetryVisible: false, HintVisible: true, AutoDismissMs: GoodAutoDismissMs);
        }

        return new("\u26A0", CompletionTone.Warning,
            goodResult ? "Calibration complete" : "Calibration failed",
            goodResult
                ? $"Mean error: {errorPx:F1} px ({qualityPercent:F1}%) \u2014 poor quality"
                : errorText ?? "",
            // A failed session cannot be retried from here (the user must look at the dot again), so
            // only the poor-quality case offers Retry; both linger long enough to be read.
            RetryVisible: goodResult, HintVisible: false, AutoDismissMs: PoorAutoDismissMs);
    }
}
