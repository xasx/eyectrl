namespace EyeCtrl.Core.Adaptation;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Settings;

/// <summary>
/// Stateful composition layer over the pure <see cref="EffectiveValue"/> calculator. Holds the
/// momentary per-<c>(knob, context)</c> delta the Phase-1b <c>AdaptationController</c> pushes in,
/// applies the <see cref="AdaptationSettings.Enabled"/> gate, and exposes the effective value each
/// consumer reads. When adaptation is disabled or no delta is stored, <see cref="Effective"/> returns
/// the exact baseline — the locked "disabled ⇒ effective == baseline" invariant (design §8.3).
/// Not thread-safe for concurrent writes: deltas are set on the serialized controller/dispatcher
/// thread. In this slice the only reader (<c>GazeEngine.RebuildFixationDetector</c>) runs on that same
/// thread; cross-thread reads via <see cref="Effective"/> from consumer (ViewModel) threads arrive with
/// the later routing slices (design §9).
/// </summary>
public sealed class EffectiveSettingsProvider
{
    // §13.1 fixed safety envelope, as fractions of baseline + the setting's validated range. These are
    // internal constants in this slice; Phase 1b-② migrates them into user-configurable AdaptationSettings
    // (which validates the bands inside the base range, removing the duplication).
    // ⚠️ ValidMin/ValidMax here DUPLICATE DwellSettings.FixationRadiusPixels's guard (10–200); the Task-1
    // drift-guard test uses that setter as the oracle so a divergence fails the build. A knob reaches this
    // table only when it has a stored delta (see Effective), which falls back to baseline for any knob
    // without a policy entry.
    private readonly record struct KnobPolicy(
        double ForgivingFracMin, double ForgivingFracMax,
        double TighteningFracMin, double TighteningFracMax,
        double ValidMin, double ValidMax);

    private static readonly IReadOnlyDictionary<AdaptationKnob, KnobPolicy> Policies =
        new Dictionary<AdaptationKnob, KnobPolicy>
        {
            // Fixation radius: forgiving = looser = up to +50 % larger; no tightening. Validated range
            // 10–200 px (DwellSettings.FixationRadiusPixels guard).
            [AdaptationKnob.FixationRadius] = new(0.0, 0.5, 0.0, 0.0, 10.0, 200.0),
        };

    private readonly AdaptationSettings _adaptation;
    private readonly Dictionary<(AdaptationKnob Knob, DwellContext Context), (double Forgiving, double Tightening)> _deltas = new();

    /// <summary>Raised whenever a momentary delta is set or cleared, so consumers can re-read effective values.</summary>
    public event Action? Changed;

    /// <summary>Initializes the provider with the master adaptation settings (the enable gate).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation) => _adaptation = adaptation;

    /// <summary>
    /// Returns the effective value for <paramref name="knob"/> in <paramref name="context"/>, composing
    /// the stored momentary delta onto <paramref name="baseline"/> via <see cref="EffectiveValue.Compute"/>.
    /// Returns the exact <paramref name="baseline"/> when adaptation is disabled or no non-zero delta is stored.
    /// </summary>
    /// <param name="knob">The knob being read.</param>
    /// <param name="context">The interaction context; use <see cref="DwellContext.Other"/> for global knobs such as tolerance.</param>
    /// <param name="baseline">The user's saved baseline value for the knob.</param>
    /// <returns>The composed, clamped effective value, or the baseline.</returns>
    public double Effective(AdaptationKnob knob, DwellContext context, double baseline)
    {
        if (!_adaptation.Enabled)
            return baseline;
        if (!_deltas.TryGetValue((knob, context), out var d) || (d.Forgiving == 0 && d.Tightening == 0))
            return baseline;

        if (!Policies.TryGetValue(knob, out var p))
            return baseline; // defensive (design §11): a delta for a knob without a policy falls back to baseline
        return EffectiveValue.Compute(
            baseline, d.Forgiving, d.Tightening,
            (p.ForgivingFracMin * baseline, p.ForgivingFracMax * baseline),
            (p.TighteningFracMin * baseline, p.TighteningFracMax * baseline),
            p.ValidMin, p.ValidMax);
    }

    /// <summary>
    /// Sets the momentary delta components for a knob/context and raises <see cref="Changed"/>. Called by
    /// the Phase-1b controller (and tests); the components are clamped to the knob's sub-bands inside
    /// <see cref="EffectiveValue.Compute"/> when read.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context.</param>
    /// <param name="forgiving">Forgiving delta (signed toward "easier"; clamped to the knob's forgiving band).</param>
    /// <param name="tightening">Tightening delta (clamped to the knob's tightening band; zero for forgiving-only knobs).</param>
    public void SetMomentaryDelta(AdaptationKnob knob, DwellContext context, double forgiving, double tightening)
    {
        _deltas[(knob, context)] = (forgiving, tightening);
        Changed?.Invoke();
    }

    /// <summary>Clears every momentary delta (toolbar reset / session boundary) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearMomentaryDeltas()
    {
        if (_deltas.Count == 0)
            return;
        _deltas.Clear();
        Changed?.Invoke();
    }
}
