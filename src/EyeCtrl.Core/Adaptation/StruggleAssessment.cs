namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The <c>StruggleMonitor</c>'s per-tick output (design §13.2): the classified per-failure-mode
/// assessments plus an aggregate struggle level, consumed by the AdaptationController (plan 1b-④).
/// Constructed by the monitor (plan 1b-③b); this slice (1b-③a) defines the contract plus the signal
/// scorers that feed it.
/// </summary>
public sealed class StruggleAssessment
{
    /// <summary>Initializes a new assessment.</summary>
    /// <param name="modes">The per-mode assessments for this tick (at most one entry per
    /// <see cref="FailureMode"/>). Stored by reference (no defensive copy): the monitor (1b-③b) passes a
    /// freshly-built list it neither retains nor mutates, and <see cref="FailureModeAssessment"/> is an
    /// immutable value type, so callers cannot observe or cause mutation.</param>
    /// <param name="aggregateLevel">Aggregate struggle level in [0, 1] (drives cue intensity / logging).</param>
    public StruggleAssessment(IReadOnlyList<FailureModeAssessment> modes, double aggregateLevel)
    {
        Modes = modes;
        AggregateLevel = aggregateLevel;
    }

    /// <summary>The per-failure-mode assessments for this tick (at most one entry per <see cref="FailureMode"/>).</summary>
    public IReadOnlyList<FailureModeAssessment> Modes { get; }

    /// <summary>Aggregate struggle level in [0, 1] across all modes.</summary>
    public double AggregateLevel { get; }

    /// <summary>An empty assessment (no modes, zero level) — the monitor's output when idle or before any evidence.</summary>
    public static StruggleAssessment None { get; } = new(Array.Empty<FailureModeAssessment>(), 0.0);

    /// <summary>Returns the assessment for <paramref name="mode"/>, or null when that mode has no entry this tick.</summary>
    /// <param name="mode">The failure mode to look up.</param>
    /// <returns>The matching <see cref="FailureModeAssessment"/>, or null.</returns>
    public FailureModeAssessment? ForMode(FailureMode mode)
    {
        foreach (var m in Modes)
            if (m.Mode == mode)
                return m;
        return null;
    }
}
