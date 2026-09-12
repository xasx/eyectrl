namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>
/// Result of collecting gaze samples at a single calibration target.
/// All coordinates are in physical screen pixels.
/// </summary>
/// <param name="Target">The screen position of the calibration target in physical pixels.</param>
/// <param name="MeanGaze">The mean of the collected gaze samples in physical pixels.</param>
/// <param name="StdDevX">Standard deviation of gaze samples on the X axis in pixels.</param>
/// <param name="StdDevY">Standard deviation of gaze samples on the Y axis in pixels.</param>
/// <param name="SampleCount">
/// Number of in-band gaze samples the mean and σ were computed from. Since 2026-09-13 this counts
/// only samples that lay within <see cref="CalibrationGeometry.BandRadiusPx"/> of the target.
/// </param>
/// <param name="AttemptedSamples">
/// Every post-settle sample this target saw, in band or not — the denominator of
/// <see cref="Coverage"/>. Zero on calibrations written before coverage was recorded, which is why
/// <see cref="Coverage"/> returns null rather than 0 % in that case.
/// </param>
public readonly record struct CalibrationPoint(
    PhysicalPoint Target,
    PhysicalPoint MeanGaze,
    double StdDevX,
    double StdDevY,
    int SampleCount,
    int AttemptedSamples = 0)
{
    /// <summary>
    /// Fraction of this target's attempt that was spent on target, or <c>null</c> when unknown —
    /// which means either nothing was attempted or the calibration predates this field.
    /// <para>
    /// σ is computed over in-band samples only, so without coverage it cannot be read at all: two
    /// good samples out of two hundred give a beautifully tight σ describing nothing. Coverage is the
    /// number that stops a gated σ from flattering a session. Null must be treated as "unknown", never
    /// as "fine" — a legacy calibration genuinely has no evidence either way.
    /// </para>
    /// </summary>
    public double? Coverage =>
        AttemptedSamples <= 0 ? null : (double)SampleCount / AttemptedSamples;

    /// <summary>Computes the Euclidean distance in pixels between the target and the mean gaze position.</summary>
    /// <returns>The error distance in physical pixels.</returns>
    public double EuclideanError() =>
        Math.Sqrt(
            (MeanGaze.X - Target.X) * (MeanGaze.X - Target.X) +
            (MeanGaze.Y - Target.Y) * (MeanGaze.Y - Target.Y));
}
