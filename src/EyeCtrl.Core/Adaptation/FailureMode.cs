namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The struggle failure modes the monitor classifies signals into; each selects a distinct safe
/// response (design §7.1/§13.2). At most one assessment per mode is produced per controller tick.
/// </summary>
public enum FailureMode
{
    /// <summary>High-progress abandonment and/or jitter (plus retries): shorter dwell, looser tolerance, more smoothing.</summary>
    SustainFailure,

    /// <summary>Activation followed by a reversal (re-approach or keyboard Backspace): the one quarantined tightening — longer dwell, bounded and instantly escapable.</summary>
    AccidentalActivation,

    /// <summary>Large near-miss on abandons: enlarge target geometry (and its coupled visible size, atomically).</summary>
    Aiming,

    /// <summary>Cross-context coherent near-miss bias: looser tolerance and report only — calibration is never auto-touched.</summary>
    Drift,

    /// <summary>High invalid-sample fraction: more smoothing, enlarge geometry, caregiver alert if sustained; never tighten.</summary>
    PoorTracking,
}
