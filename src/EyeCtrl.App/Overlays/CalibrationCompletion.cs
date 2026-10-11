namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core.Localization;

/// <summary>Whether the completion panel is a success or a warning, as a tone the overlay maps to a brush.</summary>
internal enum CompletionTone
{
    Success,
    Warning,
}

/// <summary>
/// What the calibration overlay's completion panel shows: symbol, tone, title, detail line, whether
/// Retry is offered, whether the hint is shown, whether the "Press Esc to cancel" line is shown, and
/// how long the panel lingers before dismissing itself.
/// </summary>
internal readonly record struct CalibrationCompletionPresentation(
    string Symbol,
    CompletionTone Tone,
    string Title,
    string Detail,
    bool RetryVisible,
    bool HintVisible,
    int AutoDismissMs,
    bool CancelHintVisible);

/// <summary>
/// The completion panel's copy and visibility rules, as a pure function of the session's state.
/// <para>
/// Extracted from <c>CalibrationWindow.ShowCompletion</c> so the measure-only ending — which must
/// never say "failed" and must never offer a retry — has a headless test. The window itself calls
/// <c>Show()</c>, <c>Activate()</c> and <c>Focus()</c>, so an STA test of it would create an HWND and
/// seize the user's only input surface; the text choice is the part worth pinning, and it needs none
/// of that.
/// </para>
/// <para>
/// The copy comes from the calibration surface's keys; the numbers keep their <c>:F1</c> specifiers
/// and go through <c>T(key, args)</c>, so they format in the language of the sentence around them.
/// </para>
/// </summary>
internal static class CalibrationCompletion
{
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
            // No "Press Esc to cancel" either: the probe is over and there is nothing left to cancel \u2014
            // the line only told an observer something false for the panel's 3 s (holistic review N1).
            return new("\u25CF", CompletionTone.Success, T("calibration.window.title.recorded"), "",
                RetryVisible: false, HintVisible: false, AutoDismissMs: GoodAutoDismissMs,
                CancelHintVisible: false);
        }

        bool isGood = qualityPercent < GoodQualityPercent;
        string title = goodResult
            ? T("calibration.window.title.complete")
            : T("calibration.window.title.failed");

        if (goodResult && isGood)
        {
            return new("\u2713", CompletionTone.Success, title,
                T("calibration.completion.detail", errorPx, qualityPercent),
                RetryVisible: false, HintVisible: true, AutoDismissMs: GoodAutoDismissMs,
                CancelHintVisible: true);
        }

        return new("\u26A0", CompletionTone.Warning, title,
            goodResult
                ? T("calibration.completion.detail.poor", errorPx, qualityPercent)
                : errorText ?? "",
            // A failed session cannot be retried from here (the user must look at the dot again), so
            // only the poor-quality case offers Retry; both linger long enough to be read.
            RetryVisible: goodResult, HintVisible: false, AutoDismissMs: PoorAutoDismissMs,
            CancelHintVisible: true);
    }

    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);
}
