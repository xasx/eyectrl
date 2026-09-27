namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;

/// <summary>
/// The calibration overlay's completion copy, as a pure function. The window's own end is an
/// STA/Show() path — it creates an HWND and takes focus, the user's only input surface — so the part
/// that must never regress (a probe never reads "failed", never offers Retry) is pinned here, without
/// a window.
/// </summary>
public class CalibrationCompletionTests
{
    [Fact]
    public void Describe_MeasureOnly_IsNeutralForEveryOutcome()
    {
        foreach (bool goodResult in new[] { true, false })
        foreach (double quality in new[] { 0.0, 0.5, 9.0 })
        {
            var presentation = CalibrationCompletion.Describe(
                measureOnly: true, goodResult, errorPx: 12.3, quality, errorText: "Calibration failed");

            Check.That(presentation.Title).IsEqualTo(CalibrationCompletion.MeasureOnlyTitle);
            Check.That(presentation.Title).IsEqualTo("Gaze offset recorded");
            Check.That(presentation.Detail).IsEqualTo("");
            Check.That(presentation.RetryVisible).IsFalse();
            Check.That(presentation.HintVisible).IsFalse();
            Check.That(presentation.Tone).IsEqualTo(CompletionTone.Success);
            Check.That(presentation.AutoDismissMs).IsEqualTo(CalibrationCompletion.GoodAutoDismissMs);
            // Nothing is left to cancel once the probe has recorded (holistic review N1).
            Check.That(presentation.CancelHintVisible).IsFalse();
        }
    }

    [Theory]
    [InlineData(true, 5.0, 0.5, null, "Calibration complete", true, false, 3000)]
    [InlineData(true, 120.0, 7.5, null, "Calibration complete", false, true, 12000)]
    [InlineData(false, 0.0, 0.0, "Gaze never reached the target", "Calibration failed", false, false, 12000)]
    public void Describe_NormalSession_PicksThePanelAndVisibility(
        bool goodResult, double errorPx, double qualityPercent, string? errorText,
        string expectedTitle, bool expectedSuccessTone, bool retryVisible, int autoDismissMs)
    {
        var presentation = CalibrationCompletion.Describe(
            measureOnly: false, goodResult, errorPx, qualityPercent, errorText);

        Check.That(presentation.Title).IsEqualTo(expectedTitle);
        Check.That(presentation.Tone)
            .IsEqualTo(expectedSuccessTone ? CompletionTone.Success : CompletionTone.Warning);
        Check.That(presentation.RetryVisible).IsEqualTo(retryVisible);
        Check.That(presentation.AutoDismissMs).IsEqualTo(autoDismissMs);
        // Normal endings keep the cancel line: the probe's panel is the only one that drops it.
        Check.That(presentation.CancelHintVisible).IsTrue();
        if (goodResult && qualityPercent < CalibrationCompletion.GoodQualityPercent)
            Check.That(presentation.Detail).Contains("Mean error:").And.Contains("px");
        else if (!goodResult)
            Check.That(presentation.Detail).IsEqualTo(errorText);
        else
            Check.That(presentation.Detail).Contains("poor quality");
    }
}
