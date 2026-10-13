namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>Cursor filtering and visual appearance settings.</summary>
public partial class CursorSettings : ObservableObject, IValidatable
{
    /// <summary>Gaze filtering strategy applied to raw gaze samples.</summary>
    [ObservableProperty] private GazeFilterMode _filterMode = GazeFilterMode.OneEuro;
    /// <summary>Smoothing factor for the EMA filter. 0 = maximum smoothing, 1 = no filtering (higher = more responsive, less smoothing). Valid range: 0–1.</summary>
    [ObservableProperty] private double _smoothingFactor = 0.5;
    /// <summary>
    /// Minimum cutoff frequency in Hz for the 1€ filter — the cutoff at rest. Lower values produce more
    /// smoothing. Valid range: 0.1–20.
    ///
    /// Default 1.5, lowered from 5.0 in the Stage 3 filter retune. At the PCEye 5's ~33 Hz a one-pole
    /// filter attenuates white noise by ~43 % at 5 Hz and ~65 % at 1.5 Hz, for ~32 ms and ~106 ms of lag
    /// at rest respectively. Spending that lag is affordable because it is only paid AT REST: a saccade
    /// takes the jump-detector path, which is unaffected by this value.
    /// </summary>
    [ObservableProperty] private double _oneEuroMinCutoff = 1.5;
    /// <summary>
    /// Speed coefficient (beta) for the 1€ filter: Hz of additional cutoff per
    /// <see cref="Processing.OneEuroFilter.SpeedNormalizationPixels"/> pixels per second of filtered
    /// speed. Valid range: 0–60.
    ///
    /// Renamed and rescaled in the Stage 3 retune. It was previously <c>OneEuroBeta</c> in Hz per PIXEL
    /// per second, where the right answer was ~0.01 and the shipped default was 0.5. The rename is
    /// load-bearing: the persisted JSON key is the migration's only durable marker for "this profile
    /// still holds a pixel-unit value", and any code still reading the old name fails to compile rather
    /// than silently reading a number that is now 2000x off.
    /// </summary>
    [ObservableProperty] private double _oneEuroBetaPerDiagonal = 20.0;
    /// <summary>Maximum gap in milliseconds to interpolate between missing gaze samples. Valid range: 0–500.</summary>
    [ObservableProperty] private int _maxGapInterpolationMs = 150;
    /// <summary>
    /// Radius in pixels of the cursor stabiliser's hysteretic deadband: gaze movement smaller than this
    /// leaves the published point exactly where it is. 0 disables it.
    ///
    /// Clamped at READ time (<c>SettingsService.Load</c>) to 0–15, and deliberately absent from
    /// <see cref="Validate"/>: a <c>Validate()</c> range on a NEW setting discards the user's ENTIRE
    /// settings file the first time any profile carries an out-of-range value, which punishes exactly
    /// the hand-editors the range was meant to protect.
    ///
    /// The stabiliser that consumes this is not implemented yet (Stage 3 ticket 4); the setting ships
    /// with the migration so that the profile does not need a second one later.
    /// </summary>
    [ObservableProperty] private double _deadbandRadiusPx = 4.5;
    /// <summary>
    /// Consecutive samples outside the deadband required before the stabiliser releases, for excursions
    /// short of an obvious saccade (which releases immediately). 2 discards isolated single-sample
    /// outliers — the excursion class that destroys accumulated dwell progress — for 1 sample of delay
    /// on a marginal release only.
    ///
    /// Clamped at read time to 1–5; see <see cref="DeadbandRadiusPx"/> for why not in <see cref="Validate"/>.
    /// </summary>
    [ObservableProperty] private int _deadbandConfirmSamples = 2;
    /// <summary>Hex color of the gaze cursor circle (e.g., "#A5F3FC").</summary>
    [ObservableProperty] private string _circleColor = SettingsDefaults.Accent;
    /// <summary>
    /// Opacity of the gaze cursor circle. Valid range: 0–1.
    ///
    /// Default 1.0. The core band is painted OVER the outer band, which stays opaque
    /// regardless of this value (CursorRingStacks applies opacity to the core alone), so the
    /// visible core is core*opacity + outer*(1-opacity) — or, for BiTonalSegments' side-by-side
    /// bands, core*opacity + backdrop*(1-opacity) — converging on whatever is behind it as
    /// opacity falls. The outer band's opacity is what makes the edge strong at 1.0; it does
    /// not protect the edge as this value drops. For Midnight's Accent core the edge is
    /// 15.78:1 at 1.0, 7.92:1 at 0.7, and 6.03:1 at 0.6 — under the 7.0 floor: the original 0.7
    /// reasoning was correct, and an earlier revision of this comment was wrong to call it
    /// superseded. CursorRingStyleContrastTests'
    /// CircleOpacity_WorstCaseAcrossEveryStyleThemeAndPreset_IsPinned models this across every
    /// style/theme/preset; the worst case needs 0.9 (Midnight's Amber, tied by Daylight's
    /// ring green and warning).
    /// </summary>
    [ObservableProperty] private double _circleOpacity = 1.0;
    /// <summary>
    /// Thickness of the gaze cursor circle stroke in logical pixels. Valid range: 1–10.
    ///
    /// 1.0, the bottom of the legal range: live testing found the ring too heavy at 3.0. This
    /// is safe because the contrast guarantee lives in the internal edge between the halo and
    /// core bands (the halo is always <c>thickness + 4</c>), which holds at any thickness — so
    /// thickness is a taste knob, not a safety one. The ring style preset no longer overwrites
    /// this value (see CursorPageSource's <c>c_ring_style</c> setter), so this default is what
    /// a fresh install actually sees.
    /// </summary>
    [ObservableProperty] private double _circleThickness = 1.0;
    /// <summary>Diameter of the gaze cursor circle in logical pixels. Valid range: 10–500.</summary>
    [ObservableProperty] private double _circleSize = 250;
    /// <summary>Visual style of the position indicator shown at the gaze point.</summary>
    [ObservableProperty] private PositionIndicatorStyle _positionIndicatorStyle = PositionIndicatorStyle.Crosshair;
    /// <summary>How the cursor ring's stroke stack is built. See CursorRingStacks.</summary>
    [ObservableProperty] private CursorRingStyle _ringStyle = CursorRingStyle.WeightedSandwich;

    partial void OnSmoothingFactorChanging(double value)
    {
        if (value < 0.0 || value > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(SmoothingFactor)} must be between 0 and 1");
    }

    partial void OnOneEuroMinCutoffChanging(double value)
    {
        if (value < 0.1 || value > 20.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(OneEuroMinCutoff)} must be between 0.1 and 20");
    }

    partial void OnOneEuroBetaPerDiagonalChanging(double value)
    {
        if (value < 0.0 || value > 60.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(OneEuroBetaPerDiagonal)} must be between 0 and 60");
    }

    partial void OnMaxGapInterpolationMsChanging(int value)
    {
        if (value < 0 || value > 500)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(MaxGapInterpolationMs)} must be between 0 and 500");
    }

    partial void OnCircleColorChanging(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException(
                $"{nameof(CircleColor)} must be a valid hex color (e.g., #00FF00)",
                nameof(value));
        }
    }

    partial void OnCircleOpacityChanging(double value)
    {
        if (value < 0.0 || value > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(CircleOpacity)} must be between 0 and 1");
    }

    partial void OnCircleThicknessChanging(double value)
    {
        if (value < 1.0 || value > 10.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(CircleThickness)} must be between 1 and 10");
    }

    partial void OnCircleSizeChanging(double value)
    {
        if (value < 10.0 || value > 500.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(CircleSize)} must be between 10 and 500");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(SmoothingFactor, 0.0, 1.0, nameof(SmoothingFactor)),
            ValidationHelper.ValidateRange(OneEuroMinCutoff, 0.1, 20.0, nameof(OneEuroMinCutoff)),
            ValidationHelper.ValidateRange(OneEuroBetaPerDiagonal, 0.0, 60.0, nameof(OneEuroBetaPerDiagonal)),
            // DeadbandRadiusPx / DeadbandConfirmSamples are deliberately NOT here — read-time clamps
            // only. See their XML docs: a Validate() range on a new setting is a whole-file wipe.
            ValidationHelper.ValidateRange(MaxGapInterpolationMs, 0, 500, nameof(MaxGapInterpolationMs)),
            ValidationHelper.ValidateHexColor(CircleColor, nameof(CircleColor)),
            ValidationHelper.ValidateRange(CircleOpacity, 0.0, 1.0, nameof(CircleOpacity)),
            ValidationHelper.ValidateRange(CircleThickness, 1.0, 10.0, nameof(CircleThickness)),
            ValidationHelper.ValidateRange(CircleSize, 10.0, 500.0, nameof(CircleSize)),
            ValidationHelper.ValidateEnumDefined(RingStyle, nameof(RingStyle)),
            ValidationHelper.ValidateEnumDefined(PositionIndicatorStyle, nameof(PositionIndicatorStyle))
        );
    }
}
