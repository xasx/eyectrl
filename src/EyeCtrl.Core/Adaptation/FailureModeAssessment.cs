namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// One failure mode's classified assessment for a controller tick (design §13.2 output). Built by the
/// <c>StruggleMonitor</c> (plan 1b-③b) from the per-signal <see cref="SignalScore"/>s and consumed by
/// the AdaptationController (plan 1b-④).
/// </summary>
/// <param name="Mode">The failure mode this entry describes.</param>
/// <param name="Score">The mode's struggle score in [0, 1].</param>
/// <param name="Confidence">The mode's evidence confidence in [0, 1].</param>
/// <param name="GatePassed">True when the mode's confidence gate is met and the controller may act.</param>
/// <param name="Context">The dominant interaction context for context-specific modes; null for global modes.</param>
public readonly record struct FailureModeAssessment(
    FailureMode Mode,
    double Score,
    double Confidence,
    bool GatePassed,
    DwellContext? Context);
