namespace EyeCtrl.Core.Adaptation;

using System;

/// <summary>
/// Pure implementation of the effective-value model (design §5): composes a baseline with a
/// forgiving delta and a separately-clamped tightening delta, then clamps the result to the
/// setting's validated range. The two deltas are clamped in independent sub-bands so a bounded
/// tightening (e.g. longer dwell for accidental activation) is never swallowed by an accumulated
/// forgiving offset. A zero delta always returns the exact baseline — the Phase-1a exit invariant
/// (with adaptation disabled the controller yields zero deltas, so the pipeline reduces to baseline).
/// </summary>
public static class EffectiveValue
{
    /// <summary>Computes the effective value for one knob.</summary>
    /// <param name="baseline">The user's saved baseline value for the knob.</param>
    /// <param name="forgivingDelta">Combined learned + momentary forgiving delta (signed toward "easier").</param>
    /// <param name="tighteningDelta">Momentary tightening delta (e.g. longer dwell); zero for forgiving-only knobs.</param>
    /// <param name="forgivingBand">Inclusive (Min, Max) the forgiving delta is clamped to.</param>
    /// <param name="tighteningBand">Inclusive (Min, Max) the tightening delta is clamped to.</param>
    /// <param name="validMin">Lower bound of the setting's validated range.</param>
    /// <param name="validMax">Upper bound of the setting's validated range.</param>
    /// <returns>The exact <paramref name="baseline"/> when the clamped delta is zero; otherwise
    /// <c>clamp(baseline + clampedForgiving + clampedTightening, [validMin, validMax])</c>.</returns>
    public static double Compute(
        double baseline,
        double forgivingDelta,
        double tighteningDelta,
        (double Min, double Max) forgivingBand,
        (double Min, double Max) tighteningBand,
        double validMin,
        double validMax)
    {
        double forgiving = Math.Clamp(forgivingDelta, forgivingBand.Min, forgivingBand.Max);
        double tightening = Math.Clamp(tighteningDelta, tighteningBand.Min, tighteningBand.Max);
        double delta = forgiving + tightening;
        // §5: the validated-range clamp engages only when delta != 0, so a zero delta always returns
        // the exact baseline — even a legacy/hand-edited baseline sitting outside the range.
        if (delta == 0) return baseline;
        return Math.Clamp(baseline + delta, validMin, validMax);
    }
}
