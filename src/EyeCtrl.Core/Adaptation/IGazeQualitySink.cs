namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Receives periodic <see cref="GazeQualitySummary"/> snapshots. Separate from
/// <see cref="IStruggleTelemetrySink"/> so dwell-outcome sinks need not implement it; the same
/// concrete sink may implement both. Implementations must be cheap and must not throw (design §11).
/// </summary>
public interface IGazeQualitySink
{
    /// <summary>Reports one gaze-quality window summary.</summary>
    /// <param name="summary">The window summary.</param>
    void ReportGazeQuality(in GazeQualitySummary summary);
}
