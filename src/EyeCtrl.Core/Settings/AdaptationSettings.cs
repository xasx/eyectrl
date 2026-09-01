namespace EyeCtrl.Core.Settings;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Master switch, posture, per-knob enables + safety-band bounds, and feedback toggles for
/// struggle-detection self-adaptation. Off by default (opt-in v1). Band bounds are fractions of the
/// knob's baseline and stay a *desired* band — the hard guarantee is the validated-range clamp applied
/// at composition time (design §5/§8.4). Posture scales eagerness only; its preset values live in the
/// PosturePresets table. The safety-band widths are fixed across postures (§13.1).
/// </summary>
public partial class AdaptationSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch. When false, adaptation is fully inert (effective settings == baseline).</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>How eagerly adaptation responds. Conservative by default.</summary>
    [ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;

    /// <summary>Whether dwell-duration adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptDwell = true;

    /// <summary>Whether targeting-tolerance adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptTolerance = true;

    /// <summary>Whether smoothing adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptSmoothing = true;

    /// <summary>Whether target-geometry adaptation is enabled.</summary>
    [ObservableProperty] private bool _adaptGeometry = true;

    /// <summary>Whether the on-screen adaptation cue is shown (still suppressed in sensitive contexts).</summary>
    [ObservableProperty] private bool _showCue = true;

    /// <summary>Whether sustained-struggle caregiver alerts are enabled (Phase 3; off by default).</summary>
    [ObservableProperty] private bool _caregiverAlerts;

    /// <summary>Whether feedback (cue/toast/verbose log) is suppressed in sensitive (password) contexts.</summary>
    [ObservableProperty] private bool _suppressInSensitiveContexts = true;

    /// <summary>Maximum forgiving (shorter) dwell deviation as a fraction of baseline (§13.1). Default 0.40.</summary>
    [ObservableProperty] private double _dwellShorterMaxFraction = 0.40;

    /// <summary>Maximum tightening (longer) dwell deviation for accidental activation, as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _dwellLongerMaxFraction = 0.50;

    /// <summary>Maximum looser (larger) fixation-radius deviation as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _toleranceLooserMaxFraction = 0.50;

    /// <summary>Maximum "more smoothing" shift of the active filter parameter as a fraction of baseline.
    /// Default 0.50 — CONFIRMED 2026-09-01 by the smoothing-routing slice (the knob's first consumer):
    /// fraction-of-baseline is kept, and the ceiling holds — One-Euro 5.0 → 2.5 Hz (fixation τ ≈ 32 →
    /// 64 ms), EMA 0.5 → 0.25 (τ ≈ 48 → 116 ms at every speed). Both DECREASED for more smoothing. The
    /// composed value is clamped to the ACTIVE filter's validated range by <c>GazeEngine</c>
    /// (One-Euro floor 0.1 Hz — the fraction model is range-safe only for EMA; design §13.3).</summary>
    [ObservableProperty] private double _smoothingMoreMaxFraction = 0.50;

    /// <summary>Maximum larger target-geometry deviation as a fraction of baseline (§13.1). Default 0.50.</summary>
    [ObservableProperty] private double _geometryBiggerMaxFraction = 0.50;

    partial void OnDwellShorterMaxFractionChanging(double value) => GuardFraction(value, nameof(DwellShorterMaxFraction));
    partial void OnDwellLongerMaxFractionChanging(double value) => GuardFraction(value, nameof(DwellLongerMaxFraction));
    partial void OnToleranceLooserMaxFractionChanging(double value) => GuardFraction(value, nameof(ToleranceLooserMaxFraction));
    partial void OnSmoothingMoreMaxFractionChanging(double value) => GuardFraction(value, nameof(SmoothingMoreMaxFraction));
    partial void OnGeometryBiggerMaxFractionChanging(double value) => GuardFraction(value, nameof(GeometryBiggerMaxFraction));

    private static void GuardFraction(double value, string name)
    {
        if (value < 0.0 || value > 1.0)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{name} must be between 0 and 1.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DwellShorterMaxFraction, 0.0, 1.0, nameof(DwellShorterMaxFraction)),
        ValidationHelper.ValidateRange(DwellLongerMaxFraction, 0.0, 1.0, nameof(DwellLongerMaxFraction)),
        ValidationHelper.ValidateRange(ToleranceLooserMaxFraction, 0.0, 1.0, nameof(ToleranceLooserMaxFraction)),
        ValidationHelper.ValidateRange(SmoothingMoreMaxFraction, 0.0, 1.0, nameof(SmoothingMoreMaxFraction)),
        ValidationHelper.ValidateRange(GeometryBiggerMaxFraction, 0.0, 1.0, nameof(GeometryBiggerMaxFraction)));
}
