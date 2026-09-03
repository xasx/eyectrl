namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Per-surface validated dwell-time ranges for DwellTime routing (design §13.1: per-surface sub-ranges
/// are re-applied at dwell routing; the provider clamps to the union [100, 3000] — the twelve dwell
/// guards, incl. ExternalDwellSettings.DefaultDwellMs). A range is the
/// <see cref="DwellSettings"/> guard FAMILY the surface's baselines come from, so an effective value
/// never leaves the span the surface's own settings would allow — and an identity restore returns the
/// exact baseline. Drift-guarded by the setter-as-oracle test (the FixationRadius precedent).
/// </summary>
public static class AdaptationDwellRanges
{
    /// <summary>The nine non-keyboard DwellSettings guards (Toolbar, ActionMenu, ActivationButton, …): 200–2000 ms.</summary>
    public const int StandardMinDwellMs = 200;
    /// <summary>See <see cref="StandardMinDwellMs"/>.</summary>
    public const int StandardMaxDwellMs = 2000;
    /// <summary>KeyboardKeyDwellMs / KeyboardPredictionDwellMs (also the emoji grid's category tabs, which read KeyboardKeyDwellMs): 100–1500 ms.</summary>
    public const int KeyboardMinDwellMs = 100;
    /// <summary>See <see cref="KeyboardMinDwellMs"/>.</summary>
    public const int KeyboardMaxDwellMs = 1500;
    /// <summary>ExternalDwellSettings.DefaultDwellMs: 200–3000 ms — NOT Standard; a legal 2500 ms baseline restored through [200, 2000] would be truncated.</summary>
    public const int ExternalMinDwellMs = 200;
    /// <summary>See <see cref="ExternalMinDwellMs"/>.</summary>
    public const int ExternalMaxDwellMs = 3000;
}
