namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Interface for gaze filtering algorithms.
/// Returns filtered positions in physical screen pixels.
/// </summary>
public interface IGazeFilter
{
    /// <summary>
    /// Applies the filter to a raw gaze sample and returns a smoothed position.
    /// </summary>
    /// <param name="rawGaze">Raw gaze point in the tracker's native coordinate space.</param>
    /// <returns>Filtered position in physical screen pixels.</returns>
    PhysicalPoint Filter(GazePoint rawGaze);
    /// <summary>Resets the filter's internal state (e.g., when tracking is paused and resumed).</summary>
    void Reset();
}
