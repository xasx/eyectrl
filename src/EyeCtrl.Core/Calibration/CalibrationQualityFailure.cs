namespace EyeCtrl.Core.Calibration;

/// <summary>
/// The screen region a calibration target occupies, as one of nine whole-position codes.
/// <para>
/// A code rather than an assembled phrase ("the {vertical}-{horizontal} point"): German cannot be
/// assembled from English fragments, so every position gets its own translatable phrase on the
/// calibration surface.
/// </para>
/// </summary>
public enum CalibrationTargetRegion
{
    /// <summary>The screen size was unknown, so the target could not be placed.</summary>
    Unknown,

    /// <summary>Top third, left third.</summary>
    TopLeft,

    /// <summary>Top third, middle third.</summary>
    TopCenter,

    /// <summary>Top third, right third.</summary>
    TopRight,

    /// <summary>Middle third, left third.</summary>
    MiddleLeft,

    /// <summary>The screen centre.</summary>
    Center,

    /// <summary>Middle third, right third.</summary>
    MiddleRight,

    /// <summary>Bottom third, left third.</summary>
    BottomLeft,

    /// <summary>Bottom third, middle third.</summary>
    BottomCenter,

    /// <summary>Bottom third, right third.</summary>
    BottomRight,
}

/// <summary>Which per-target quality gate a calibration target failed.</summary>
public enum CalibrationQualityReason
{
    /// <summary>The transform reproduces the target's mean worse than the residual budget allows.</summary>
    ResidualTooLarge,

    /// <summary>The target's samples were too scattered to build a mean from (σ over the limit).</summary>
    ExcessScatter,

    /// <summary>Too little of the target's attempt was spent inside its band (coverage below the minimum).</summary>
    LowCoverage,
}

/// <summary>
/// One target that failed the calibration quality gate: which target, where it sat, which metric it
/// failed, and the two numbers the user's sentence quotes.
/// </summary>
/// <param name="TargetIndex">Zero-based index in the session's target order.</param>
/// <param name="Region">The screen region the target occupies, as a whole-position code.</param>
/// <param name="Reason">Which gate the target crossed.</param>
/// <param name="Value">
/// The measured value, in the unit the sentence shows: pixels for
/// <see cref="CalibrationQualityReason.ResidualTooLarge"/> and
/// <see cref="CalibrationQualityReason.ExcessScatter"/>, percent for
/// <see cref="CalibrationQualityReason.LowCoverage"/>.
/// </param>
/// <param name="Limit">The gate's threshold, in the same unit as <paramref name="Value"/>.</param>
public readonly record struct CalibrationQualityFailure(
    int TargetIndex,
    CalibrationTargetRegion Region,
    CalibrationQualityReason Reason,
    double Value,
    double Limit);
