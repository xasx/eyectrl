namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Identifies an adaptable setting ("knob") the <see cref="EffectiveSettingsProvider"/> composes a
/// bounded momentary fill onto. Each knob has its own safe direction, units, and safety band
/// (design §5/§13.1). All four §5 knob families are declared; <see cref="FixationRadius"/> and
/// <see cref="Smoothing"/> are routed to Core consumers (both via <c>GazeEngine</c> — plans 1b-① and
/// the smoothing-routing slice) — dwell and geometry are composable but unrouted until their routing
/// slices land.
/// </summary>
public enum AdaptationKnob
{
    /// <summary>Fixation-detection hit radius in physical pixels — the tolerance knob (forgiving = looser = larger). Routed via <c>GazeEngine</c>.</summary>
    FixationRadius,

    /// <summary>Dwell activation time in milliseconds (forgiving = shorter; the one quarantined tightening = longer, for accidental activation). Unrouted until the dwell-routing slice.</summary>
    DwellTime,

    /// <summary>The active gaze filter's smoothing parameter (forgiving = more smoothing = parameter DOWN for both filters). Routed via <c>GazeEngine</c>'s adaptive interpolator rebuild, clamped to the active filter's validated range (smoothing-routing slice).</summary>
    Smoothing,

    /// <summary>Target ring/hit extent and its coupled visible size in physical pixels (forgiving = bigger).
    /// Composed as an absolute extent, but <c>DwellDetector.UpdateTargetGeometry</c> applies a signed
    /// <em>margin</em> relative to a target's as-registered extents (and grows rect bounds, not just the
    /// radius), so the routing slice converts between the two. Unrouted until the geometry-routing slice.</summary>
    TargetGeometry,
}
