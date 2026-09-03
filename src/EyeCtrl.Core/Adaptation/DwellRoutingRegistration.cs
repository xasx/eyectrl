namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Processing;

/// <summary>
/// One dwell surface's routing entry: the telemetry context, the surface's validated dwell range, and
/// the two appliers that push baseline→effective maps onto the surface's live targets — dwell time
/// (ms→ms) and hit geometry (extent→extent; converted to a margin inside the detector). The appliers
/// are delegates (not a raw <see cref="DwellDetector"/>) so an owner with its own threading contract —
/// ExternalDwellService serializes detector access under <c>_stateLock</c> on the gaze thread — can
/// wrap the calls in its lock.
/// </summary>
/// <param name="Context">Telemetry context (informational today; per-context fills are Phase 2).</param>
/// <param name="MinDwellMs">Surface-family validated floor for the effective dwell value.</param>
/// <param name="MaxDwellMs">Surface-family validated ceiling for the effective dwell value.</param>
/// <param name="Apply">Receives the dwell map (null = restore baselines); must apply it on the owner's thread.</param>
/// <param name="ApplyGeometry">Receives the geometry map (null = restore as-registered geometry);
/// must apply it on the owner's thread. Required, not optional — a silently-absent applier is the
/// mutation class the wiring guard tests exist to catch.</param>
public sealed record DwellRoutingRegistration(
    DwellContext Context,
    int MinDwellMs,
    int MaxDwellMs,
    Action<Func<int, int>?> Apply,
    Action<Func<double, double>?> ApplyGeometry)
{
    /// <summary>The default appliers for the common case: a detector owned and mutated on one thread
    /// (the UI thread) with no additional locking.</summary>
    public static DwellRoutingRegistration ForDetector(
        DwellDetector detector, DwellContext context, int minDwellMs, int maxDwellMs) =>
        new(context, minDwellMs, maxDwellMs, detector.SetEffectiveDwellTime, detector.SetEffectiveGeometry);
}
