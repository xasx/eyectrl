namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Stateful composition layer over the pure <see cref="EffectiveValue"/> calculator. Holds a momentary
/// per-<c>(knob, context)</c> <b>fill fraction</b> (a dimensionless band position in <c>[0, 1]</c>) the
/// Phase-1b <c>AdaptationController</c> (plan 1b-④b) pushes in, applies the
/// <see cref="AdaptationSettings.Enabled"/> gate, and exposes the effective value each consumer reads.
/// The absolute delta is resolved <b>at read time</b> as <c>fill × sign × bandFraction × baseline</c>
/// (band fractions sourced live from <see cref="AdaptationSettings"/>, so the multi-baseline dwell knob
/// and the runtime-derived geometry knob each compose against the caller's own baseline). When adaptation
/// is disabled or no non-zero fill is stored, <see cref="Effective"/> returns the exact baseline — the
/// locked "disabled ⇒ effective == baseline" invariant (design §8.3).
/// Not thread-safe for concurrent writes: fills are set on the serialized controller/dispatcher thread.
/// The sole consumer (<c>GazeEngine</c>, plan 1b-①) defers its <c>Changed</c>-driven fixation rebuild onto
/// the gaze thread, so its reads of <see cref="Effective"/> arrive there; the other three knobs are
/// composable but have no consumer yet (design §9).
/// </summary>
public sealed class EffectiveSettingsProvider
{
    // §13.1 fixed safety envelope. A knob's forgiving/tightening MAGNITUDE (a fraction of baseline) is
    // sourced live from AdaptationSettings (the band-source migration, 1b-④a); the SIGN encodes the knob's
    // safe direction (§5 typed transforms); ValidMin/ValidMax stay provider-owned constants (NOT migrated).
    //
    // ⚠️ FixationRadius's ValidMin/ValidMax DUPLICATE DwellSettings.FixationRadiusPixels's guard (10–200);
    // the Task-2 drift-guard test uses that setter as the oracle so a divergence fails the build. The three
    // unrouted knobs get SUPERSET validated ranges (dwell = union of the 11 dwell-time guards [100, 2000];
    // smoothing = union of both filter ranges [0, 20]; geometry = unbounded above, since geometry has NO
    // pre-existing validated range — §8.4 for geometry stays deferred). The per-consumer/mode/runtime range
    // is re-applied by each knob's routing slice; here the fraction clamp is the operative bound and the
    // superset is only a safety net. A knob reaches this table only when it has a stored fill (see Effective).
    private readonly record struct KnobPolicy(
        double ForgivingSign,
        Func<AdaptationSettings, double> ForgivingFraction,
        double TighteningSign,
        Func<AdaptationSettings, double>? TighteningFraction,
        double ValidMin,
        double ValidMax);

    private static readonly IReadOnlyDictionary<AdaptationKnob, KnobPolicy> Policies =
        new Dictionary<AdaptationKnob, KnobPolicy>
        {
            // Tolerance: forgiving = looser = larger (+); no tightening. Range 10–200 (DwellSettings guard).
            [AdaptationKnob.FixationRadius] = new(
                +1.0, static a => a.ToleranceLooserMaxFraction, 0.0, null, 10.0, 200.0),
            // Dwell: forgiving = shorter (−); the one quarantined tightening = longer (+). Range = union of
            // the 11 dwell-time guards [100, 2000] (per-surface ranges re-applied at dwell routing).
            [AdaptationKnob.DwellTime] = new(
                -1.0, static a => a.DwellShorterMaxFraction, +1.0, static a => a.DwellLongerMaxFraction, 100.0, 2000.0),
            // Smoothing: forgiving = more = parameter DOWN (−); no tightening. Range = union of both filter
            // ranges [0, 20] (per-mode range re-applied at smoothing routing). Discharges 1b-②'s flagged
            // "revisit fraction-vs-absolute" question: ④ ADOPTS fraction-of-baseline — the uniform fill
            // model applies cleanly (OneEuro baseline 5 → 2.5; EMA 0.5 → 0.25, both in-range) and needs no
            // per-filter special-casing here; a mode-aware absolute per-filter delta, if ever needed, is a
            // smoothing-routing concern (this knob is unrouted, so the model choice is not yet load-bearing).
            [AdaptationKnob.Smoothing] = new(
                -1.0, static a => a.SmoothingMoreMaxFraction, 0.0, null, 0.0, 20.0),
            // Geometry: forgiving = bigger (+); no tightening. No pre-existing validated range (runtime-
            // derived) → unbounded above; the +fraction band is the operative bound (§8.4 deferred, per 1b-①).
            // The absolute geometry bound §13.1 expects "Phase 1b" to introduce lands with the GEOMETRY-ROUTING
            // slice, which supplies both the runtime-derived baseline and the first consumer — it cannot be
            // validated before then. This knob is unrouted here, so the fraction bounds the (no-op) delta.
            [AdaptationKnob.TargetGeometry] = new(
                +1.0, static a => a.GeometryBiggerMaxFraction, 0.0, null, 0.0, double.MaxValue),
        };

    private readonly AdaptationSettings _adaptation;
    private readonly Dictionary<(AdaptationKnob Knob, DwellContext Context), (double ForgivingFill, double TighteningFill)> _fills = new();

    /// <summary>Raised whenever a momentary fill is set or cleared, so consumers can re-read effective values.</summary>
    public event Action? Changed;

    /// <summary>Initializes the provider with the master adaptation settings (the enable gate + band-fraction source).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition and whose band fractions bound each knob.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation) => _adaptation = adaptation;

    /// <summary>
    /// Returns the effective value for <paramref name="knob"/> in <paramref name="context"/>, resolving the
    /// stored fill fraction to an absolute delta (<c>fill × sign × bandFraction × baseline</c>) and composing
    /// it onto <paramref name="baseline"/> via <see cref="EffectiveValue.Compute"/>. Returns the exact
    /// <paramref name="baseline"/> when adaptation is disabled or no non-zero fill is stored.
    /// </summary>
    /// <param name="knob">The knob being read.</param>
    /// <param name="context">The interaction context; use <see cref="DwellContext.Other"/> for the global momentary layer.</param>
    /// <param name="baseline">The consumer's baseline value for the knob (its own, so the fill resolves per-consumer).</param>
    /// <returns>The composed, clamped effective value, or the baseline.</returns>
    public double Effective(AdaptationKnob knob, DwellContext context, double baseline)
    {
        if (!_adaptation.Enabled)
            return baseline;
        if (!_fills.TryGetValue((knob, context), out var f) || (f.ForgivingFill == 0.0 && f.TighteningFill == 0.0))
            return baseline;
        if (!Policies.TryGetValue(knob, out var p))
            return baseline; // defensive (design §11): a fill for a knob without a policy falls back to baseline

        // Signed band edges resolved against the caller's baseline. fill ∈ [0, 1] keeps the delta within the
        // band; EffectiveValue.Compute re-clamps (the C1 safety net + any fill > 1) then applies the range.
        double forgivingEdge = p.ForgivingSign * p.ForgivingFraction(_adaptation) * baseline;
        double tighteningEdge = p.TighteningFraction is null ? 0.0 : p.TighteningSign * p.TighteningFraction(_adaptation) * baseline;

        return EffectiveValue.Compute(
            baseline,
            f.ForgivingFill * forgivingEdge,
            f.TighteningFill * tighteningEdge,
            (Math.Min(0.0, forgivingEdge), Math.Max(0.0, forgivingEdge)),
            (Math.Min(0.0, tighteningEdge), Math.Max(0.0, tighteningEdge)),
            p.ValidMin, p.ValidMax);
    }

    /// <summary>
    /// Sets the momentary band-fill fractions for a knob/context and raises <see cref="Changed"/>. Called by
    /// the Phase-1b controller (and tests). Fills are the dimensionless band position in <c>[0, 1]</c>; the
    /// absolute delta and its sub-band clamp are resolved in <see cref="Effective"/> against the consumer's
    /// baseline.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context (<see cref="DwellContext.Other"/> for the global momentary layer).</param>
    /// <param name="forgivingFill">Forgiving band-fill fraction toward "easier" (expected in <c>[0, 1]</c>; over-range values are clamped at read time).</param>
    /// <param name="tighteningFill">Tightening band-fill fraction (non-zero only for the dwell knob under accidental activation; zero for forgiving-only knobs).</param>
    public void SetMomentaryFill(AdaptationKnob knob, DwellContext context, double forgivingFill, double tighteningFill)
    {
        _fills[(knob, context)] = (forgivingFill, tighteningFill);
        Changed?.Invoke();
    }

    /// <summary>Clears every momentary fill (toolbar reset / session boundary) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearMomentaryFills()
    {
        if (_fills.Count == 0)
            return;
        _fills.Clear();
        Changed?.Invoke();
    }
}
