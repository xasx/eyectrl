namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The §13.3 smoothing composition helper: the per-filter-mode composition+clamp of the adaptation
/// Smoothing knob, extracted VERBATIM from <c>GazeEngine.EffectiveSmoothingParameter</c>
/// (adaptation-preview Task 2) so a later preview builder composes the identical value from the same
/// code. The engine method is now a one-line delegate to <see cref="Compose"/>; the engine-level oracle
/// (<c>GazeEngineSmoothingRoutingTests</c>, including the assign-back guard test that pins the clamp
/// literals against the <see cref="CursorSettings"/> setters) still pins this behaviour end-to-end.
/// </summary>
public static class EffectiveSmoothing
{
    // §13.3 item (1): the provider→filter VALUE path. The active filter's ONE adaptation-sensitive
    // parameter, composed against the CURRENT mode's baseline — the fill is a fraction OF the baseline, so
    // the baseline must be the active parameter's (OneEuroMinCutoff vs SmoothingFactor), resolved afresh on
    // every read. null = the knob no-ops: Direct has no smoothing parameter (mode is never auto-switched,
    // §5), and no provider means no adaptation at all.
    //
    // §13.3 SAFETY CLAMP: the provider's Smoothing policy carries the union superset [0, 20], but the
    // ACTIVE filter's own validated range is re-applied here — [0.1, 20] for One-Euro, [0, 1] for EMA.
    // Without it, a 0.1 Hz One-Euro baseline at the default fraction composes to 0.05 (and to exactly 0.0
    // at fraction 1.0), which OneEuroFilter's constructor rejects — a throw on the gaze thread, inside the
    // unguarded rebuild. The literals deliberately duplicate the CursorSettings guards (the FixationRadius
    // precedent); the assign-back oracle test fails the build if they drift. Only the FLOOR can engage:
    // smoothing is forgiving-only (parameter down, no tightening), so the composed value never exceeds the
    // baseline, which the setters keep inside the range.

    /// <summary>
    /// Composes the effective smoothing parameter for the active <see cref="CursorSettings.FilterMode"/>:
    /// the provider's Smoothing fill resolved against the mode's own baseline (OneEuroMinCutoff for
    /// One-Euro, SmoothingFactor for EMA), then clamped to that filter's validated range. Returns the
    /// exact baseline when adaptation is disabled or no fill is stored, and <c>null</c> when the knob
    /// no-ops (no provider, or Direct mode).
    /// </summary>
    /// <param name="provider">The effective-settings provider (the adaptation composition layer); <c>null</c> when the engine was built without one.</param>
    /// <param name="cursor">The cursor settings supplying the filter mode and the mode's baseline.</param>
    /// <returns>The composed, clamped effective parameter for the live filter, or <c>null</c>.</returns>
    public static double? Compose(EffectiveSettingsProvider? provider, CursorSettings cursor)
    {
        if (provider is null)
            return null;
        return cursor.FilterMode switch
        {
            GazeFilterMode.Direct => null,
            GazeFilterMode.Smoothed or GazeFilterMode.SmoothedWithSnapping => Math.Clamp(
                provider.Effective(AdaptationKnob.Smoothing, DwellContext.Other, cursor.SmoothingFactor),
                0.0, 1.0),
            // OneEuro, plus the default arm mirroring GazeFilterFactory's default (OneEuroFilter) so a
            // future enum member can never compose an EMA baseline onto a One-Euro filter.
            GazeFilterMode.OneEuro => Math.Clamp(
                provider.Effective(AdaptationKnob.Smoothing, DwellContext.Other, cursor.OneEuroMinCutoff),
                0.1, 20.0),
            _ => Math.Clamp(
                provider.Effective(AdaptationKnob.Smoothing, DwellContext.Other, cursor.OneEuroMinCutoff),
                0.1, 20.0),
        };
    }
}
