namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;

/// <summary>
/// Renders a <see cref="CalibrationFailure"/> as the sentence the calibration window shows.
/// <para>
/// The one place the calibration surface's failure text lives. Core raises codes plus arguments
/// (see <see cref="CalibrationFailure"/>), and this renderer turns them into the calibration
/// surface's keys. Every key is written literally at its call site; the templates keep the
/// numbers' <c>:F0</c> specifiers, and the target-never-held sentence is a <c>{one, other}</c>
/// plural whose count is the FIRST argument so the table's visible-digits rule reads it
/// (see <c>StringTable</c>).
/// </para>
/// </summary>
public static class CalibrationFailureText
{
    /// <summary>Renders the failure for the user.</summary>
    /// <param name="failure">The typed failure to describe.</param>
    /// <returns>The sentence the calibration window shows.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failure"/> is null.</exception>
    public static string Describe(CalibrationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Code switch
        {
            CalibrationFailureCode.GazeVariation => T("calibration.failure.gazeVariation"),
            CalibrationFailureCode.UnusableTransform => T("calibration.failure.unusableTransform"),
            CalibrationFailureCode.DistortedTransform => T("calibration.failure.distortedTransform"),
            CalibrationFailureCode.Quality => DescribeQuality(failure.Arguments),
            CalibrationFailureCode.TargetNeverReached => T("calibration.failure.targetNeverReached"),
            CalibrationFailureCode.TargetNeverHeld => DescribeTargetNeverHeld(failure.Arguments),
            CalibrationFailureCode.Unexpected => T("calibration.failure.unexpected"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Code, "Unknown calibration failure code."),
        };
    }

    private static string DescribeQuality(IReadOnlyList<object?> arguments)
    {
        var failures = (IReadOnlyList<CalibrationQualityFailure>)arguments[0]!;
        if (failures.Count == 0) return string.Empty;

        var detail = string.Join("; ", failures.Select(f => $"{DescribeRegion(f.Region)} — {DescribeReason(f)}"));

        return T("calibration.failure.quality", detail);
    }

    private static string DescribeTargetNeverHeld(IReadOnlyList<object?> arguments)
    {
        var bandRadiusPx = (double)arguments[0]!;
        var attemptedSamples = (int)arguments[1]!;
        // The sample count comes FIRST so the plural probe reads it; the band radius keeps its
        // {1:0} specifier. "1 samples" reads as its "one" form: "1 sample".
        return T("calibration.failure.targetNeverHeld", attemptedSamples, bandRadiusPx);
    }

    private static string DescribeRegion(CalibrationTargetRegion region) => region switch
    {
        CalibrationTargetRegion.Unknown => T("calibration.failure.region.unknown"),
        CalibrationTargetRegion.TopLeft => T("calibration.failure.region.topLeft"),
        CalibrationTargetRegion.TopCenter => T("calibration.failure.region.topCenter"),
        CalibrationTargetRegion.TopRight => T("calibration.failure.region.topRight"),
        CalibrationTargetRegion.MiddleLeft => T("calibration.failure.region.middleLeft"),
        CalibrationTargetRegion.Center => T("calibration.failure.region.center"),
        CalibrationTargetRegion.MiddleRight => T("calibration.failure.region.middleRight"),
        CalibrationTargetRegion.BottomLeft => T("calibration.failure.region.bottomLeft"),
        CalibrationTargetRegion.BottomCenter => T("calibration.failure.region.bottomCenter"),
        CalibrationTargetRegion.BottomRight => T("calibration.failure.region.bottomRight"),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown calibration target region."),
    };

    private static string DescribeReason(CalibrationQualityFailure failure) => failure.Reason switch
    {
        CalibrationQualityReason.ResidualTooLarge =>
            T("calibration.failure.reason.residualTooLarge", failure.Value, failure.Limit),
        CalibrationQualityReason.ExcessScatter =>
            T("calibration.failure.reason.excessScatter", failure.Value, failure.Limit),
        CalibrationQualityReason.LowCoverage =>
            T("calibration.failure.reason.lowCoverage", failure.Value, failure.Limit),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Reason, "Unknown calibration quality reason."),
    };

    /// <summary>Resolves a calibration key; keys stay literal at their call sites.</summary>
    private static string T(string key, params object?[] args) => StringTable.Current.T(key, args);
}
