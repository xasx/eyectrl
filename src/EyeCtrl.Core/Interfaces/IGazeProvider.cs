namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Enums;
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

    /// <summary>
    /// Capabilities describing which optional data streams this provider can supply.
    /// Defaults to <see cref="GazeProviderCapabilities.None"/> — providers that support
    /// track-status data override this.
    /// </summary>
    GazeProviderCapabilities Capabilities => GazeProviderCapabilities.None;

    /// <summary>
    /// Raised when a new track-status sample (eye position, distance, presence) is available.
    /// Only raised by providers with the corresponding <see cref="Capabilities"/>.
    /// Default implementation is a no-op event so test fakes compile unchanged
    /// (mirrors the IInputSimulator DIM pattern at IInputSimulator.cs:53,58).
    /// </summary>
    /// <remarks>
    /// H4 note: this event is currently UNUSED — the sole data path is
    /// <see cref="TryGetLatestStatus"/>, polled by the window's DispatcherTimer.
    /// The event is retained as part of the interface for a future push-based
    /// model (e.g. a provider that fires samples on a non-poll thread without
    /// caching). The Tobii provider does NOT fire it. If YAGNI is preferred,
    /// remove this member entirely — the DIM default and TryGetLatestStatus
    /// are sufficient. Left in for now to avoid a second interface churn if
    /// the push model is needed later.
    /// </remarks>
    event Action<TrackStatusSample>? TrackStatusReceived { add { } remove { } }

    /// <summary>
    /// Thread-safe read of the latest cached track-status sample.
    /// Default returns false (no track-status data). The Tobii provider overrides this.
    /// This is the sole data path used by the Track Status box (the window's DispatcherTimer
    /// polls it at ~15 Hz). The <see cref="TrackStatusReceived"/> event above is currently unused.
    /// </summary>
    bool TryGetLatestStatus(out TrackStatusSample sample)
    {
        sample = default;
        return false;
    }
}
