namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Receives dwell outcomes (and, in later phases, gaze-quality summaries) from every interaction
/// surface. Implementations must be cheap to invoke on the gaze/UI thread and must not throw
/// (see design §11 — enforcement lands in the serialized App-layer sink wrapper).
/// A null sink means "not instrumented" and disables emission at the source.
/// </summary>
public interface IStruggleTelemetrySink
{
    /// <summary>Reports a completed dwell interaction (activation or abandonment).</summary>
    /// <param name="outcome">The outcome to record.</param>
    void Report(in DwellOutcome outcome);
}
