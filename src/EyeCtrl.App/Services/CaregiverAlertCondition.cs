namespace EyeCtrl.App.Services;

/// <summary>The two conditions that sustain a caregiver alert (design §10, Phase 3). Drift is
/// deliberately absent — its report is the user-actionable in-app banner (§7.1, D7).</summary>
public enum CaregiverAlertCondition
{
    /// <summary>Aggregate struggle level at or above <see cref="CaregiverAlertService.StruggleLevelThreshold"/>, held continuously.</summary>
    SustainedStruggle,

    /// <summary>The <see cref="EyeCtrl.Core.Adaptation.FailureMode.PoorTracking"/> mode gated, held continuously (fires with zero interactions, §6.3).</summary>
    SustainedPoorTracking,
}
