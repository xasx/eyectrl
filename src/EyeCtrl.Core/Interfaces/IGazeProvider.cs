namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Contract for gaze data providers (Tobii Stream Engine, mouse simulation, etc.).
/// Produces a stream of <see cref="GazePoint"/> samples that feed into the gaze pipeline.
/// </summary>
public interface IGazeProvider : IDisposable
{
    /// <summary>Raised when a new gaze sample is available from the tracker.</summary>
    event Action<GazePoint>? GazePointReceived;
    /// <summary>Starts the gaze data stream.</summary>
    void Start();
    /// <summary>Stops the gaze data stream.</summary>
    void Stop();
    /// <summary><c>true</c> if the tracker is connected and providing data.</summary>
    bool IsConnected { get; }
}
