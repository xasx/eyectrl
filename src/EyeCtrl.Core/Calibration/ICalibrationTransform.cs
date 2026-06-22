namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>
/// Contract for coordinate transforms that map raw eye tracker gaze to screen coordinates.
/// Implementations are applied to every gaze sample in the pipeline.
/// </summary>
public interface ICalibrationTransform
{
    /// <summary>Applies the transform to a raw gaze point and returns the screen-space coordinate.</summary>
    /// <param name="rawGaze">Raw gaze position in the tracker's native coordinate space.</param>
    /// <returns>Transformed position in physical screen pixels.</returns>
    PhysicalPoint Apply(PhysicalPoint rawGaze);
    /// <summary>Returns the transform parameters as an array suitable for persistence.</summary>
    double[] GetParameters();
    /// <summary>Restores the transform from persisted parameters.</summary>
    /// <param name="parameters">The parameter array previously returned by <see cref="GetParameters"/>.</param>
    void SetParameters(double[] parameters);
}
