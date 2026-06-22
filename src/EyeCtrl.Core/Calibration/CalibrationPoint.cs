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
/// <param name="SampleCount">Number of valid gaze samples collected.</param>
public readonly record struct CalibrationPoint(
    PhysicalPoint Target,
    PhysicalPoint MeanGaze,
    double StdDevX,
    double StdDevY,
    int SampleCount)
{
    /// <summary>Computes the Euclidean distance in pixels between the target and the mean gaze position.</summary>
    /// <returns>The error distance in physical pixels.</returns>
    public double EuclideanError() =>
        Math.Sqrt(
            (MeanGaze.X - Target.X) * (MeanGaze.X - Target.X) +
            (MeanGaze.Y - Target.Y) * (MeanGaze.Y - Target.Y));
}
