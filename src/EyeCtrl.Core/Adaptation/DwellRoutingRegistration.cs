namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Processing;

/// <summary>
/// One dwell surface's routing entry: the telemetry context, the surface's validated dwell range, and
/// an applier that pushes a baseline→effective-ms map onto the surface's live targets. The applier is a
/// delegate (not a raw <see cref="DwellDetector"/>) so an owner with its own threading contract —
/// ExternalDwellService serializes detector access under <c>_stateLock</c> on the gaze thread — can wrap
/// the call in its lock. The geometry-routing slice reuses this discovery mechanism.
/// </summary>
/// <param name="Context">Telemetry context (informational today; per-context fills are Phase 2).</param>
/// <param name="MinDwellMs">Surface-family validated floor for the effective value.</param>
/// <param name="MaxDwellMs">Surface-family validated ceiling for the effective value.</param>
/// <param name="Apply">Receives the map (null = restore baselines); must apply it on the owner's thread.</param>
public sealed record DwellRoutingRegistration(
    DwellContext Context,
    int MinDwellMs,
    int MaxDwellMs,
    Action<Func<int, int>?> Apply)
{
    /// <summary>The default applier for the common case: a detector owned and mutated on one thread
    /// (the UI thread) with no additional locking.</summary>
    public static DwellRoutingRegistration ForDetector(
        DwellDetector detector, DwellContext context, int minDwellMs, int maxDwellMs) =>
        new(context, minDwellMs, maxDwellMs, detector.SetEffectiveDwellTime);
}
