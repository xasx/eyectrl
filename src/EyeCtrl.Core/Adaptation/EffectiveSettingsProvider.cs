namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;

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
/// <para>
/// Phase 2 (§7.4) adds a per-knob <b>learned</b> band fill — forgiving-only, clamped to
/// <see cref="AdaptationSettings.LearnedMaxFill"/> — composed with the momentary forgiving fill
/// before the single band clamp (design §5: forgiving = clamp(learned + momentary, band)).
/// <see cref="Changed"/> therefore also fires on learned set/clear and on composition-relevant
/// settings edits (band fractions, the geometry margin cap), not only on momentary fill writes.
/// </para>
/// Not thread-safe for concurrent writes: fills are set on the serialized controller/dispatcher thread.
/// The sole consumer (<c>GazeEngine</c>, plan 1b-①) defers its <c>Changed</c>-driven fixation rebuild onto
/// the gaze thread, so its reads of <see cref="Effective"/> arrive there; the dwell knob is routed by
/// the App-side `DwellTimeRouter` (dwell-routing slice, 2026-09-02), the geometry knob by
/// `GeometryRouter` (geometry-routing slice, 2026-09-03), which clamps the growth margin to
/// `GeometryMaxMarginPx`; the smoothing knob is routed by <c>GazeEngine</c>'s adaptive interpolator
/// rebuild (smoothing-routing slice, 2026-09-01), which clamps the composed value to the active
/// filter's validated range.
/// </summary>
public sealed class EffectiveSettingsProvider
{
    // §13.1 fixed safety envelope. A knob's forgiving/tightening MAGNITUDE (a fraction of baseline) is
    // sourced live from AdaptationSettings (the band-source migration, 1b-④a); the SIGN encodes the knob's
    // safe direction (§5 typed transforms); ValidMin/ValidMax stay provider-owned constants (NOT migrated).
    //
    // ⚠️ FixationRadius's ValidMin/ValidMax DUPLICATE DwellSettings.FixationRadiusPixels's guard (10–200);
    // the Task-2 drift-guard test uses that setter as the oracle so a divergence fails the build. Dwell and
    // geometry carry SUPERSET validated ranges (dwell = union of the 12 dwell-time guards [100, 3000] —
    // per-surface ranges re-applied at dwell routing; geometry = unbounded above — §8.4 for geometry is
    // discharged at routing, per the geometry-routing slice); smoothing's [0, 20] union range is the
    // pre-routing superset, now superseded by the per-mode clamp GazeEngine.EffectiveSmoothingParameter
    // applies at read time. The per-consumer/mode/runtime range is re-applied by each knob's routing
    // slice; here the fraction clamp is the operative bound and the superset is only a safety net. A knob
    // reaches this table only when it has a stored fill (see Effective).
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
            // the 12 dwell-time guards [100, 3000]: the 11 DwellSettings guards plus the twelfth,
            // ExternalDwellSettings.DefaultDwellMs (200–3000), so the quarantined tightening on a legal
            // 2500 ms External baseline composes toward LONGER (clamped to 3000), never truncated to the
            // Standard 2000 (a direction inversion to shorter = easier accidental re-activation).
            // Per-surface ranges re-applied at dwell routing.
            [AdaptationKnob.DwellTime] = new(
                -1.0, static a => a.DwellShorterMaxFraction, +1.0, static a => a.DwellLongerMaxFraction, 100.0, 3000.0),
            // Smoothing: forgiving = more = parameter DOWN (−); no tightening. Range = union of both filter
            // ranges [0, 20] (per-mode range re-applied by GazeEngine.EffectiveSmoothingParameter (smoothing
            // routing, 2026-09-01)). Discharges 1b-②'s flagged "revisit fraction-vs-absolute" question: ④
            // ADOPTS fraction-of-baseline — the uniform fill model applies cleanly (OneEuro baseline 5 → 2.5;
            // EMA 0.5 → 0.25, both in-range) and needs no per-filter special-casing here; a mode-aware
            // absolute per-filter delta, if ever needed, remains a routing-layer concern (the knob is routed
            // by GazeEngine.EffectiveSmoothingParameter, which clamps to the active filter's validated
            // range, so the fraction model is load-bearing).
            [AdaptationKnob.Smoothing] = new(
                -1.0, static a => a.SmoothingMoreMaxFraction, 0.0, null, 0.0, 20.0),
            // Geometry: forgiving = bigger (+); no tightening. No pre-existing validated range (runtime-
            // derived) → unbounded above; the +fraction band is the operative proportional bound. The §13.1
            // absolute bound landed with the geometry-routing slice (2026-09-03) as
            // AdaptationSettings.GeometryMaxMarginPx, applied at routing (consumer-side clamp, the
            // smoothing/dwell precedent) — §8.4 for geometry is discharged at the routing layer.
            //
            // ⚠️ UNIT MISMATCH for the routing slice to resolve: Effective() returns an ABSOLUTE extent
            // (baseline + fill × fraction × baseline), but DwellDetector.UpdateTargetGeometry consumes a
            // signed MARGIN relative to the target's as-registered extents. So routing must convert —
            // margin = Effective(TargetGeometry, ctx, baseExtent) − baseExtent — and must NOT pass a
            // baseline of 0, which would make every delta 0 (the band is a fraction OF the baseline).
            // Rect targets have no single "radius", so the slice also has to choose a reference extent
            // (DwellTarget captures both base half-extents, so a per-target baseline is available).
            [AdaptationKnob.TargetGeometry] = new(
                +1.0, static a => a.GeometryBiggerMaxFraction, 0.0, null, 0.0, double.MaxValue),
        };

    private readonly AdaptationSettings _adaptation;
    // ConcurrentDictionary resolves §13.3 item (4): fills are written on the dispatcher thread (controller
    // tick) and read on the gaze thread (GazeEngine's deferred reconfiguration reads Effective there), and
    // a first-key insert on the writer can resize a plain Dictionary while the reader is inside TryGetValue.
    // Single-WRITER semantics are unchanged (the controller is serialized on the dispatcher); only the
    // cross-thread READ becomes safe. SetMomentaryFill/ClearMomentaryFills still raise Changed on the
    // writer thread, so consumer-side coalescing is unaffected.
    private readonly ConcurrentDictionary<(AdaptationKnob Knob, DwellContext Context), (double ForgivingFill, double TighteningFill)> _fills = new();

    // Phase-2 learned fills (§7.4), keyed by knob only — v1 is global per knob (the momentary layer it
    // learns from is global; per-context learned arrives with per-context momentary fills, if ever).
    // Forgiving-only by construction: one non-negative fill per knob, no tightening component exists.
    // ConcurrentDictionary for the same reason as _fills: Effective reads it on the gaze thread.
    private readonly ConcurrentDictionary<AdaptationKnob, double> _learnedFills = new();

    /// <summary>Raised whenever a momentary fill is set or cleared, so consumers can re-read effective values.</summary>
    public event Action? Changed;

    /// <summary>Initializes the provider with the master adaptation settings (the enable gate + band-fraction source).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition and whose band fractions bound each knob.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation)
    {
        _adaptation = adaptation;
        // Composition-relevant settings edits re-raise Changed so consumers re-read: without this, a band
        // fraction or margin-cap edit while a fill is stored refreshed the applied value only at the next
        // fill write (the §13.3 settings-edit staleness leftover, fixed here for every consumer at once).
        // Enabled and Posture are excluded: the enable edge is owned by the coordinator/activation (which
        // also clear momentary fills), and posture affects only step rates, not composition. No unsubscribe:
        // provider and settings are co-extensive container singletons.
        _adaptation.PropertyChanged += OnAdaptationSettingsChanged;
    }

    private static readonly HashSet<string> CompositionRelevantSettings = new()
    {
        nameof(AdaptationSettings.DwellShorterMaxFraction),
        nameof(AdaptationSettings.DwellLongerMaxFraction),
        nameof(AdaptationSettings.ToleranceLooserMaxFraction),
        nameof(AdaptationSettings.SmoothingMoreMaxFraction),
        nameof(AdaptationSettings.GeometryBiggerMaxFraction),
        nameof(AdaptationSettings.GeometryMaxMarginPx),
    };

    private void OnAdaptationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && CompositionRelevantSettings.Contains(e.PropertyName))
            Changed?.Invoke();
    }

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
        bool hasMomentary = _fills.TryGetValue((knob, context), out var f)
            && (f.ForgivingFill != 0.0 || f.TighteningFill != 0.0);
        double learned = _learnedFills.TryGetValue(knob, out double l) ? l : 0.0;
        if (!hasMomentary && learned == 0.0)
            return baseline;
        if (!Policies.TryGetValue(knob, out var p))
            return baseline; // defensive (design §11): a fill for a knob without a policy falls back to baseline

        // Signed band edges resolved against the caller's baseline. fill ∈ [0, 1] keeps the delta within the
        // band; EffectiveValue.Compute re-clamps (the C1 safety net + any fill > 1) then applies the range.
        double forgivingEdge = p.ForgivingSign * p.ForgivingFraction(_adaptation) * baseline;
        double tighteningEdge = p.TighteningFraction is null ? 0.0 : p.TighteningSign * p.TighteningFraction(_adaptation) * baseline;

        return EffectiveValue.Compute(
            baseline,
            (f.ForgivingFill + learned) * forgivingEdge,
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

    /// <summary>
    /// Sets the Phase-2 learned band-fill fraction for a knob (§7.4) and raises <see cref="Changed"/> on
    /// value change. The value is clamped to <c>[0, <see cref="AdaptationSettings.LearnedMaxFill"/>]</c>:
    /// the store cannot hold a tightening or out-of-band learned offset, whatever the caller passes.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="fill">Learned band-fill fraction toward "easier" (forgiving-only, ≥ 0).</param>
    public void SetLearnedFill(AdaptationKnob knob, double fill)
    {
        double clamped = Math.Clamp(fill, 0.0, AdaptationSettings.LearnedMaxFill);
        double old = _learnedFills.GetOrAdd(knob, 0.0);
        if (clamped == old)
            return;
        _learnedFills[knob] = clamped;
        Changed?.Invoke();
    }

    /// <summary>Clears every learned fill (manual reset / load) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearLearnedFills()
    {
        if (_learnedFills.Count == 0)
            return;
        _learnedFills.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reads the stored momentary fills for a knob/context — the Phase-2 learner's observation
    /// seam (the provider previously had no read API at all).</summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context.</param>
    /// <param name="forgivingFill">Receives the stored forgiving band fill (0 when absent).</param>
    /// <param name="tighteningFill">Receives the stored tightening band fill (0 when absent).</param>
    /// <returns>True when a fill entry is stored for the knob/context.</returns>
    public bool TryGetMomentaryFill(AdaptationKnob knob, DwellContext context, out double forgivingFill, out double tighteningFill)
    {
        if (_fills.TryGetValue((knob, context), out var f))
        {
            forgivingFill = f.ForgivingFill;
            tighteningFill = f.TighteningFill;
            return true;
        }
        forgivingFill = 0.0;
        tighteningFill = 0.0;
        return false;
    }

    /// <summary>The non-zero learned fills, for persistence, logging, and status.</summary>
    public IReadOnlyList<(AdaptationKnob Knob, double Fill)> EnumerateLearnedFills()
    {
        var list = new List<(AdaptationKnob, double)>();
        foreach (var (knob, fill) in _learnedFills)
            if (fill != 0.0)
                list.Add((knob, fill));
        return list;
    }
}
