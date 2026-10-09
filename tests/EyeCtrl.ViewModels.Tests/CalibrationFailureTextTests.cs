namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Calibration;

/// <summary>
/// The calibration surface's failure copy, as a pure function of the typed failure. Every code the
/// window can render is pinned to its exact English, so the words cannot drift while Core carries
/// only a code and its arguments.
/// </summary>
public class CalibrationFailureTextTests
{
    [Fact]
    public void EachSimpleCode_RendersItsWholeSentence()
    {
        Check.That(CalibrationFailureText.Describe(new CalibrationFailure(CalibrationFailureCode.GazeVariation)))
             .IsEqualTo("Gaze barely changed across calibration targets — the eye tracker may not be tracking. Please recalibrate.");
        Check.That(CalibrationFailureText.Describe(new CalibrationFailure(CalibrationFailureCode.UnusableTransform)))
             .IsEqualTo("Calibration produced an unusable transform — gaze would map off-screen. Please recalibrate.");
        Check.That(CalibrationFailureText.Describe(new CalibrationFailure(CalibrationFailureCode.DistortedTransform)))
             .IsEqualTo("Calibration produced a distorted transform (gaze would be flipped or amplified). Please recalibrate.");
        Check.That(CalibrationFailureText.Describe(new CalibrationFailure(CalibrationFailureCode.TargetNeverReached)))
             .IsEqualTo("Gaze never reached the target.");
        Check.That(CalibrationFailureText.Describe(CalibrationFailure.Unexpected))
             .IsEqualTo("Something went wrong while calibrating. Please try again.");
    }

    [Fact]
    public void TargetNeverHeld_QuotesTheBandRadiusAndTheAttempt()
    {
        var failure = new CalibrationFailure(CalibrationFailureCode.TargetNeverHeld, 240.0, 12);

        Check.That(CalibrationFailureText.Describe(failure))
             .IsEqualTo("Gaze reached the target but never stayed within 240px of it long enough "
                      + "to collect a sample (12 samples seen, all out of band).");
    }

    /// <summary>
    /// T08's plural: the sentence's count is the FIRST argument, so the table's visible-digits
    /// rule reads it and a single attempt renders "1 sample seen", not "1 samples". The band
    /// radius keeps its {1:0} specifier and is still quoted.
    /// </summary>
    [Fact]
    public void TargetNeverHeld_OneAttempt_ReadsTheSingular()
    {
        var failure = new CalibrationFailure(CalibrationFailureCode.TargetNeverHeld, 150.0, 1);

        Check.That(CalibrationFailureText.Describe(failure))
             .IsEqualTo("Gaze reached the target but never stayed within 150px of it long enough "
                      + "to collect a sample (1 sample seen, all out of band).");
    }

    [Theory]
    [InlineData(CalibrationTargetRegion.Unknown, "a point")]
    [InlineData(CalibrationTargetRegion.TopLeft, "the top-left point")]
    [InlineData(CalibrationTargetRegion.TopCenter, "the top point")]
    [InlineData(CalibrationTargetRegion.TopRight, "the top-right point")]
    [InlineData(CalibrationTargetRegion.MiddleLeft, "the left point")]
    [InlineData(CalibrationTargetRegion.Center, "the center point")]
    [InlineData(CalibrationTargetRegion.MiddleRight, "the right point")]
    [InlineData(CalibrationTargetRegion.BottomLeft, "the bottom-left point")]
    [InlineData(CalibrationTargetRegion.BottomCenter, "the bottom point")]
    [InlineData(CalibrationTargetRegion.BottomRight, "the bottom-right point")]
    public void EveryRegion_RendersItsWholePositionPhrase(CalibrationTargetRegion region, string phrase)
    {
        // Nine positions plus the unknown-screen fallback, each its own whole phrase: German cannot be
        // assembled from an English vertical + horizontal pair, which is why the region is a code.
        var failure = new CalibrationFailure(CalibrationFailureCode.Quality,
            new CalibrationQualityFailure[] { new(0, region, CalibrationQualityReason.ResidualTooLarge, 400, 226) });

        Check.That(CalibrationFailureText.Describe(failure)).Contains($"{phrase} — ");
    }

    [Fact]
    public void QualityFailures_JoinEveryTarget_AndEndInWhatToDo()
    {
        var failures = new CalibrationQualityFailure[]
        {
            new(0, CalibrationTargetRegion.TopLeft, CalibrationQualityReason.ResidualTooLarge, 400, 226),
            new(1, CalibrationTargetRegion.Center, CalibrationQualityReason.ExcessScatter, 250, 100),
            new(2, CalibrationTargetRegion.BottomRight, CalibrationQualityReason.LowCoverage, 1, 50),
        };

        Check.That(CalibrationFailureText.Describe(
                new CalibrationFailure(CalibrationFailureCode.Quality, failures)))
             .IsEqualTo(
                "Calibration not accepted. "
                + "the top-left point — the fit misses it by 400px (limit 226px); "
                + "the center point — gaze was too scattered on it (spread 250px, limit 100px); "
                + "the bottom-right point — gaze was on it only 1% of the time (needs 50%). "
                + "Try again and rest on each point until its ring fills — looking away resets nothing, "
                + "so short looks add up.");
    }

    [Fact]
    public void AnEmptyQualityFailureList_RendersNothing()
    {
        Check.That(CalibrationFailureText.Describe(new CalibrationFailure(
                CalibrationFailureCode.Quality, Array.Empty<CalibrationQualityFailure>())))
             .IsEqualTo(string.Empty);
    }

    [Fact]
    public void Describe_Throws_OnNull()
    {
        CalibrationFailure? failure = null;
        ArgumentNullException? ex = null;
        try { CalibrationFailureText.Describe(failure!); }
        catch (ArgumentNullException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("failure");
    }
}
