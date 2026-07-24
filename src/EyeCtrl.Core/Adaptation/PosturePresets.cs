namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Momentary-controller tuning for one posture (design §7.2/§13.1): how eagerly adaptation relaxes
/// toward the transform target and recovers back toward baseline. Posture scales eagerness only — the
/// per-knob safety envelope (band bounds in <see cref="AdaptationSettings"/>) is fixed across postures.
/// </summary>
/// <param name="RelaxThreshold">Struggle score above which a knob relaxes (toward easier).</param>
/// <param name="SustainSeconds">Seconds the score must stay above <paramref name="RelaxThreshold"/> before relaxing.</param>
/// <param name="RelaxStepFraction">Fraction of the band moved toward the target per tick while relaxing.</param>
/// <param name="RecoverThreshold">Struggle score below which a knob recovers (toward baseline).</param>
/// <param name="RecoverStepFraction">Fraction of the band moved toward baseline per tick while recovering (posture-independent).</param>
public readonly record struct PostureTuning(
    double RelaxThreshold,
    int SustainSeconds,
    double RelaxStepFraction,
    double RecoverThreshold,
    double RecoverStepFraction);

/// <summary>
/// The fixed <see cref="PostureTuning"/> presets per <see cref="AdaptationPosture"/> (design §13.1).
/// Consumed by the Phase-1b AdaptationController; recovery is uniformly gentle across postures.
/// </summary>
public static class PosturePresets
{
    /// <summary>Returns the controller tuning for the given posture.</summary>
    /// <param name="posture">The adaptation posture.</param>
    /// <returns>The posture's <see cref="PostureTuning"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="posture"/> has no preset —
    /// surfaces enum drift loudly rather than silently mis-tuning. The drift-guard test catches an
    /// unhandled member at test time; at runtime the controller's §11 catch degrades to no-adaptation.</exception>
    public static PostureTuning For(AdaptationPosture posture) => posture switch
    {
        AdaptationPosture.Conservative => new PostureTuning(0.50, 3, 0.10, 0.25, 0.05),
        AdaptationPosture.Balanced => new PostureTuning(0.40, 2, 0.15, 0.20, 0.05),
        AdaptationPosture.Responsive => new PostureTuning(0.30, 1, 0.20, 0.15, 0.05),
        _ => throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unhandled AdaptationPosture — add a PosturePresets arm."),
    };
}
