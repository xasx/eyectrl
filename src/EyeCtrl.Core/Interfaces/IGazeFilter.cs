namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Interface for gaze filtering algorithms.
/// Returns filtered positions in physical screen pixels.
/// </summary>
public interface IGazeFilter
{
    /// <summary>
    /// Filters raw gaze data.
    /// </summary>
    /// <param name="rawGaze">Raw gaze point</param>
    /// <returns>Filtered position in physical screen pixels</returns>
    PhysicalPoint Filter(GazePoint rawGaze);
    void Reset();
}
