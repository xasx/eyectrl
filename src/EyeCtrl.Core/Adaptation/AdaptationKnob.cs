namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Identifies an adaptable setting ("knob") the <see cref="EffectiveSettingsProvider"/> composes a
/// bounded momentary delta onto. Each knob has its own safe direction, units, and safety band
/// (design §5/§13.1). Further knobs (dwell time, smoothing, target geometry) are added as their
/// routing lands in later Phase-1b slices.
/// </summary>
public enum AdaptationKnob
{
    /// <summary>Fixation-detection hit radius in physical pixels — the tolerance knob (looser = larger).</summary>
    FixationRadius,
}
