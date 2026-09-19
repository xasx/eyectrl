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
    /// <summary>
    /// KeyboardKeyDwellMs / KeyboardPredictionDwellMs (also the emoji grid's category tabs, which
    /// read KeyboardKeyDwellMs): 300–1500 ms.
    /// <para>
    /// The floor exists because both keyboard paths credit a silence of up to 150 ms as held gaze
    /// (<c>KeyboardViewModel.MaxContinuousSampleGapUs</c> for the key grid, which credits exactly
    /// 150 ms, and <c>DwellDetector.MaxCreditedSampleGapUs</c> for the prediction bar, emoji tabs
    /// and the other shared surfaces, which restarts at 150 ms). At a 150 ms dwell one short blink
    /// completes a key. ANY floor above 150 closes that SINGLE-stall case; 300 is chosen because it
    /// is the value already published to the user (ticket 7's close-out) and recorded beside
    /// <c>KeyboardSpecialActions.AppEscapingPolicyFloorMs</c>, so the code enforces the number the
    /// user was told. Its supporting property: at 300 the largest single credited stall supplies at
    /// most half of the dwell, so the user must contribute at least as much real gaze as the silence
    /// could fake. The clamp is PER GAP, not cumulative — see
    /// <c>DwellDetector.MaxCreditedSampleGapUs</c> for the multi-stall qualifier.
    /// </para>
    /// <para>
    /// Every keyboard-family path is floored here: <c>DwellSettings</c>' guards and the Dwell
    /// sliders stop a low baseline being written or configured, and this constant re-clamps the
    /// ADAPTIVE effective value — the forgiving knob shortens toward 0.6×baseline, so with a 300
    /// baseline it would otherwise reach 180. The floor therefore has to sit above the baseline
    /// guard, not merely equal it.
    /// </para>
    /// </summary>
    public const int KeyboardMinDwellMs = 300;
    /// <summary>See <see cref="KeyboardMinDwellMs"/>.</summary>
    public const int KeyboardMaxDwellMs = 1500;
    /// <summary>ExternalDwellSettings.DefaultDwellMs: 200–3000 ms — NOT Standard; a legal 2500 ms baseline restored through [200, 2000] would be truncated.</summary>
    public const int ExternalMinDwellMs = 200;
    /// <summary>See <see cref="ExternalMinDwellMs"/>.</summary>
    public const int ExternalMaxDwellMs = 3000;
}
