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

            // T08: the title text lives in strings.json under calibration.window.title.recorded.
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
    [InlineData(true, 5.0, 0.5, null, "Calibration complete", true, false, 3000,
        "Mean error: 5.0 px (0.5%)")]
    [InlineData(true, 120.0, 7.5, null, "Calibration complete", false, true, 12000,
        "Mean error: 120.0 px (7.5%) — poor quality")]
    [InlineData(false, 0.0, 0.0, "Gaze never reached the target", "Calibration failed", false, false, 12000,
        "Gaze never reached the target")]
    public void Describe_NormalSession_PicksThePanelAndVisibility(
        bool goodResult, double errorPx, double qualityPercent, string? errorText,
        string expectedTitle, bool expectedSuccessTone, bool retryVisible, int autoDismissMs,
        string expectedDetail)
    {
        var presentation = CalibrationCompletion.Describe(
            measureOnly: false, goodResult, errorPx, qualityPercent, errorText);

        Check.That(presentation.Title).IsEqualTo(expectedTitle);
        Check.That(presentation.Tone)
            .IsEqualTo(expectedSuccessTone ? CompletionTone.Success : CompletionTone.Warning);
        Check.That(presentation.RetryVisible).IsEqualTo(retryVisible);
        Check.That(presentation.AutoDismissMs).IsEqualTo(autoDismissMs);
        // The detail line is pinned as its whole English (T08 fixup R1): mutant M15 rendered the
        // good case's detail as "— poor quality" with every existing assertion green.
        Check.That(presentation.Detail).IsEqualTo(expectedDetail);
        // Normal endings keep the cancel line: the probe's panel is the only one that drops it.
        Check.That(presentation.CancelHintVisible).IsTrue();
    }
}
