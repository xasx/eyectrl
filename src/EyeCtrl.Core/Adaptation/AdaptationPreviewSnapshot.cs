namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// One knob's display row for the adaptation preview panel: the net band-fill bar (total, with the
/// learned portion shaded inside it), the direction label for the dwell knob, and — for the two knobs
/// that have a single absolute value — the effective value the engine would run plus its baseline.
/// Pure data; every field is computed by <see cref="AdaptationPreviewSnapshot.Build"/> from the
/// provider's stored fills, so a row is a point-in-time projection of the composition layer, never
/// state the panel writes back.
/// </summary>
/// <param name="Knob">The knob this row describes (rows are ordered Dwell, Tolerance, Smoothing, Geometry).</param>
/// <param name="TotalFill">Clamped composed net band fill toward the row's direction, in [0, 1]:
/// clamp(learned + momentary, 0, 1) on the row's dominant side (spec §3.4, mirroring the provider's
/// fill model at the fraction level).</param>
/// <param name="LearnedFill">The learned portion shaded inside <paramref name="TotalFill"/>, in
/// [0, <paramref name="TotalFill"/>]: min(learned, TotalFill). The learned layer is forgiving-only.</param>
/// <param name="TighteningDirection">True only for the DwellTime knob, when the tightening (longer)
/// side strictly dominates the forgiving side; ties keep the forgiving ("shorter") label.</param>
/// <param name="AbsoluteValue">The effective absolute value for tolerance (fixation px) and smoothing
/// (the active filter's parameter); null for Dwell (multi-baseline per surface) and Geometry
/// (runtime-derived), and for Smoothing in Direct mode.</param>
/// <param name="BaselineValue">The absolute knob's user baseline; null exactly when
/// <paramref name="AbsoluteValue"/> is null.</param>
/// <param name="IsHzUnit">True only on the Smoothing row when the active filter is One-Euro (the
/// parameter is min-cutoff Hz); false for the EMA factor and every non-smoothing row.</param>
public sealed record AdaptationPreviewRow(
    AdaptationKnob Knob,
    double TotalFill,
    double LearnedFill,
    bool TighteningDirection,
    double? AbsoluteValue,
    double? BaselineValue,
    bool IsHzUnit);

/// <summary>
/// The adaptation preview panel's whole view model, built as a pure projection of the composition
/// layer: exactly four rows in display order plus the "anything active" flag (false ⇒ the panel shows
/// its "nothing active" line). Use <see cref="Build"/> to snapshot the current state — the builder is
/// pure (no clocks, no events, no WPF), so it is safe to call from any thread and cheap to call
/// repeatedly while a fill animates.
/// </summary>
/// <param name="Rows">Exactly four rows, in display order: DwellTime, FixationRadius (tolerance), Smoothing, TargetGeometry.</param>
/// <param name="AnythingActive">False when every row's fill is zero — the panel then shows the
/// "nothing active" line instead of the rows.</param>
public sealed record AdaptationPreviewSnapshot(
    IReadOnlyList<AdaptationPreviewRow> Rows,
    bool AnythingActive)
{
    /// <summary>
    /// Builds the preview snapshot from the composition layer's current state. The row fills mirror
    /// the provider's fill model (spec §3.4): <c>clamp(learned + momentary, 0, 1)</c> per direction,
    /// with the dominant side naming the dwell direction (ties ⇒ forgiving); the absolute columns are
    /// the exact values the engine would run — <see cref="EffectiveSettingsProvider.Effective"/> for
    /// tolerance and <see cref="EffectiveSmoothing.Compose"/> for smoothing (the same code path the
    /// engine's rebuild uses). When <paramref name="adaptation"/> is disabled, every fill reads zero
    /// (the locked §8.3 invariant: disabled ⇒ effective == baseline — the provider still holds any
    /// stored fills, and the absolute columns show the exact baselines).
    /// </summary>
    /// <param name="adaptation">The master adaptation settings — the enable gate (P14: the provider
    /// exposes no <c>Enabled</c>). Must be the same instance the provider composes under (the app
    /// wiring) so the gate and the composition agree.</param>
    /// <param name="provider">The effective-settings provider holding the momentary and learned fills.</param>
    /// <param name="dwell">The dwell settings, supplying the tolerance knob's baseline (<c>FixationRadiusPixels</c>).</param>
    /// <param name="cursor">The cursor settings, supplying the smoothing knob's filter mode and per-mode baseline.</param>
    /// <returns>The four-row snapshot; <see cref="AnythingActive"/> is true when any row carries a non-zero fill.</returns>
    public static AdaptationPreviewSnapshot Build(
        AdaptationSettings adaptation,
        EffectiveSettingsProvider provider,
        DwellSettings dwell,
        CursorSettings cursor)
    {
        // P14: the enable gate is read here because the provider exposes no Enabled. The controller
        // writes momentary fills under DwellContext.Other (the global momentary layer) and the learned
        // fills are knob-keyed, so those are the two seams read below.
        bool enabled = adaptation.Enabled;

        provider.TryGetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, out double dwellForgiving, out double dwellTightening);
        provider.TryGetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, out double toleranceForgiving, out _);
        provider.TryGetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, out double smoothingForgiving, out _);
        provider.TryGetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, out double geometryForgiving, out _);

        double learnedDwell = LearnedFill(provider, AdaptationKnob.DwellTime);
        double learnedTolerance = LearnedFill(provider, AdaptationKnob.FixationRadius);
        double learnedSmoothing = LearnedFill(provider, AdaptationKnob.Smoothing);
        double learnedGeometry = LearnedFill(provider, AdaptationKnob.TargetGeometry);

        if (!enabled)
        {
            // Disabled ⇒ all-zero rows (§8.3): the stored fills are not cleared (the provider keeps
            // them for a re-enable), they are just not reported. The absolute columns below still
            // resolve — the provider's own gate returns the exact baseline when disabled.
            dwellForgiving = 0.0;
            dwellTightening = 0.0;
            toleranceForgiving = 0.0;
            smoothingForgiving = 0.0;
            geometryForgiving = 0.0;
            learnedDwell = 0.0;
            learnedTolerance = 0.0;
            learnedSmoothing = 0.0;
            learnedGeometry = 0.0;
        }

        var rows = new List<AdaptationPreviewRow>(4)
        {
            DwellRow(learnedDwell, dwellForgiving, dwellTightening),
            ToleranceRow(provider, dwell, learnedTolerance, toleranceForgiving),
            SmoothingRow(provider, cursor, learnedSmoothing, smoothingForgiving),
            GeometryRow(learnedGeometry, geometryForgiving),
        };

        return new AdaptationPreviewSnapshot(rows, rows.Any(r => r.TotalFill > 0.0));
    }

    // The provider enumerates learned fills as (knob, fill) pairs of its non-zero entries; a linear
    // scan per knob (four knobs, at most four entries) beats building a dictionary per snapshot.
    private static double LearnedFill(EffectiveSettingsProvider provider, AdaptationKnob knob)
    {
        foreach (var (k, fill) in provider.EnumerateLearnedFills())
            if (k == knob)
                return fill;
        return 0.0;
    }

    // Dwell is the only knob with a tightening side: compose each direction's net fill, then the
    // STRICTLY larger side names the row's direction — ties keep forgiving ("shorter"), so accidental
    // activation never labels a row "longer" unless tightening actually dominates. The learned layer
    // is forgiving-only and shades inside whichever total won.
    private static AdaptationPreviewRow DwellRow(double learned, double momentaryForgiving, double momentaryTightening)
    {
        double forgiving = Math.Clamp(learned + momentaryForgiving, 0.0, 1.0);
        double tightening = Math.Clamp(momentaryTightening, 0.0, 1.0);
        bool tighteningDominates = tightening > forgiving;
        double total = tighteningDominates ? tightening : forgiving;
        return new AdaptationPreviewRow(
            AdaptationKnob.DwellTime, total, Math.Min(learned, total), tighteningDominates, null, null, false);
    }

    // Tolerance has a single absolute: the same Effective call GazeEngine's fixation rebuild makes,
    // resolved against the dwell settings' own baseline. Dwell baseline per surface and geometry's
    // runtime-derived extent have no single row-level absolute, hence the nulls elsewhere.
    private static AdaptationPreviewRow ToleranceRow(
        EffectiveSettingsProvider provider, DwellSettings dwell, double learned, double momentary)
    {
        double total = Math.Clamp(learned + momentary, 0.0, 1.0);
        return new AdaptationPreviewRow(
            AdaptationKnob.FixationRadius,
            total,
            Math.Min(learned, total),
            TighteningDirection: false,
            provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, dwell.FixationRadiusPixels),
            BaselineValue: dwell.FixationRadiusPixels,
            IsHzUnit: false);
    }

    // Smoothing's absolute IS EffectiveSmoothing.Compose — the exact code the engine's rebuild runs —
    // so the preview can never show a value the engine would not. The unit label follows the active
    // mode's baseline (Hz for One-Euro, factor for EMA); Direct composes null (no parameter), taking
    // both value columns with it.
    private static AdaptationPreviewRow SmoothingRow(
        EffectiveSettingsProvider provider, CursorSettings cursor, double learned, double momentary)
    {
        double total = Math.Clamp(learned + momentary, 0.0, 1.0);
        double? absolute = EffectiveSmoothing.Compose(provider, cursor);
        bool isHzUnit = cursor.FilterMode switch
        {
            GazeFilterMode.Direct => false,
            GazeFilterMode.Smoothed or GazeFilterMode.SmoothedWithSnapping => false,
            // OneEuro plus the default arm, mirroring EffectiveSmoothing.Compose's mode mapping so a
            // future enum member keeps the unit label consistent with the composed value.
            _ => true,
        };
        double? baseline = absolute is null
            ? null
            : isHzUnit ? cursor.OneEuroMinCutoff : cursor.SmoothingFactor;
        return new AdaptationPreviewRow(
            AdaptationKnob.Smoothing, total, Math.Min(learned, total), false, absolute, baseline, isHzUnit);
    }

    // Geometry is band-only: the knob's absolute extent is per-target (registered at runtime), so a
    // panel row has no single value to show — the fill bar alone carries the meaning.
    private static AdaptationPreviewRow GeometryRow(double learned, double momentary)
    {
        double total = Math.Clamp(learned + momentary, 0.0, 1.0);
        return new AdaptationPreviewRow(
            AdaptationKnob.TargetGeometry, total, Math.Min(learned, total), false, null, null, false);
    }
}
