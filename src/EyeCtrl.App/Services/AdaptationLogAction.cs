namespace EyeCtrl.App.Services;

/// <summary>What <see cref="AdaptationLog.OnAssessment"/> did for one assessment (for tests + diagnostics).</summary>
public enum AdaptationLogAction
{
    /// <summary>The gated-failure-mode set was unchanged since the last assessment; nothing was logged.</summary>
    None,
    /// <summary>The gated-mode set changed and a full structured entry was logged.</summary>
    Logged,
    /// <summary>The set changed in a sensitive context; only the coarse suppression marker was logged (§8.10/§10).</summary>
    SuppressedMarker,
}
