namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Identifies an adaptable setting ("knob") the <see cref="EffectiveSettingsProvider"/> composes a
/// bounded momentary fill onto. Each knob has its own safe direction, units, and safety band
/// (design §5/§13.1). All four §5 knob families are declared; only <see cref="FixationRadius"/> is
/// routed to a Core consumer today (via <c>GazeEngine</c>, plan 1b-①) — the others are composable but
/// unrouted until their routing slices land.
/// </summary>
public enum AdaptationKnob
{
    /// <summary>Fixation-detection hit radius in physical pixels — the tolerance knob (forgiving = looser = larger). Routed via <c>GazeEngine</c>.</summary>
    FixationRadius,

    /// <summary>Dwell activation time in milliseconds (forgiving = shorter; the one quarantined tightening = longer, for accidental activation). Unrouted until the dwell-routing slice.</summary>
    DwellTime,

    /// <summary>The active gaze filter's smoothing parameter (forgiving = more smoothing = parameter DOWN for both filters). Unrouted until the smoothing-routing slice.</summary>
    Smoothing,

    /// <summary>Target ring/hit radius and its coupled visible size in physical pixels (forgiving = bigger). Unrouted until the geometry-routing slice.</summary>
    TargetGeometry,
}
