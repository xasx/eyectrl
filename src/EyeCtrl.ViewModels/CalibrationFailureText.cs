namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Calibration;

/// <summary>
/// Renders a <see cref="CalibrationFailure"/> as the sentence the calibration window shows.
/// <para>
/// The one place the calibration surface's failure text lives. Core raises codes plus arguments
/// (see <see cref="CalibrationFailure"/>), and this renderer turns them into today's English; the
/// localization work replaces these literals with <c>calibration</c> keys, which is why they sit here
/// rather than in a Core producer the Settings page also serves.
/// </para>
/// </summary>
public static class CalibrationFailureText
{
    /// <summary>Renders the failure for the user.</summary>
    /// <param name="failure">The typed failure to describe.</param>
    /// <returns>The English sentence the calibration window shows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failure"/> is null.</exception>
    public static string Describe(CalibrationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Code switch
        {
            CalibrationFailureCode.GazeVariation =>
                "Gaze barely changed across calibration targets — the eye tracker may not be tracking. Please recalibrate.",
            CalibrationFailureCode.UnusableTransform =>
                "Calibration produced an unusable transform — gaze would map off-screen. Please recalibrate.",
            CalibrationFailureCode.DistortedTransform =>
                "Calibration produced a distorted transform (gaze would be flipped or amplified). Please recalibrate.",
            CalibrationFailureCode.Quality => DescribeQuality(failure.Arguments),
            CalibrationFailureCode.TargetNeverReached => "Gaze never reached the target.",
            CalibrationFailureCode.TargetNeverHeld => DescribeTargetNeverHeld(failure.Arguments),
            CalibrationFailureCode.Unexpected => "Something went wrong while calibrating. Please try again.",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Code, "Unknown calibration failure code."),
        };
    }

    private static string DescribeQuality(IReadOnlyList<object?> arguments)
    {
        var failures = (IReadOnlyList<CalibrationQualityFailure>)arguments[0]!;
        if (failures.Count == 0) return string.Empty;

        var detail = string.Join("; ", failures.Select(f => $"{DescribeRegion(f.Region)} — {DescribeReason(f)}"));

        // The advice is the same in every case because the cause almost always is: the user moved
        // during collection. Naming the points is what makes it actionable — they are told WHICH ones
        // to spend longer on, rather than being sent back to repeat all of them blind.
        return $"Calibration not accepted. {detail}. "
             + "Try again and rest on each point until its ring fills — looking away resets nothing, "
             + "so short looks add up.";
    }

    private static string DescribeTargetNeverHeld(IReadOnlyList<object?> arguments)
    {
        var bandRadiusPx = (double)arguments[0]!;
        var attemptedSamples = (int)arguments[1]!;
        return $"Gaze reached the target but never stayed within {bandRadiusPx:0}px of it long enough "
             + $"to collect a sample ({attemptedSamples} samples seen, all out of band).";
    }

    private static string DescribeRegion(CalibrationTargetRegion region) => region switch
    {
        CalibrationTargetRegion.Unknown => "a point",
        CalibrationTargetRegion.TopLeft => "the top-left point",
        CalibrationTargetRegion.TopCenter => "the top point",
        CalibrationTargetRegion.TopRight => "the top-right point",
        CalibrationTargetRegion.MiddleLeft => "the left point",
        CalibrationTargetRegion.Center => "the center point",
        CalibrationTargetRegion.MiddleRight => "the right point",
        CalibrationTargetRegion.BottomLeft => "the bottom-left point",
        CalibrationTargetRegion.BottomCenter => "the bottom point",
        CalibrationTargetRegion.BottomRight => "the bottom-right point",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown calibration target region."),
    };

    private static string DescribeReason(CalibrationQualityFailure failure) => failure.Reason switch
    {
        CalibrationQualityReason.ResidualTooLarge =>
            $"the fit misses it by {failure.Value:F0}px (limit {failure.Limit:F0}px)",
        CalibrationQualityReason.ExcessScatter =>
            $"gaze was too scattered on it (spread {failure.Value:F0}px, limit {failure.Limit:F0}px)",
        CalibrationQualityReason.LowCoverage =>
            $"gaze was on it only {failure.Value:F0}% of the time (needs {failure.Limit:F0}%)",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Reason, "Unknown calibration quality reason."),
    };
}
