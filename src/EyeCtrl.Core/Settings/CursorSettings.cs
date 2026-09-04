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
    /// <summary>Minimum cutoff frequency in Hz for the 1€ filter. Lower values produce more smoothing. Valid range: 0.1–20.</summary>
    [ObservableProperty] private double _oneEuroMinCutoff = 5.0;
    /// <summary>Speed coefficient (beta) for the 1€ filter. Controls responsiveness to rapid movements. Valid range: 0–5.</summary>
    [ObservableProperty] private double _oneEuroBeta = 0.5;
    /// <summary>Maximum gap in milliseconds to interpolate between missing gaze samples. Valid range: 0–500.</summary>
    [ObservableProperty] private int _maxGapInterpolationMs = 150;
    /// <summary>Hex color of the gaze cursor circle (e.g., "#A5F3FC").</summary>
    [ObservableProperty] private string _circleColor = SettingsDefaults.Accent;
    /// <summary>
    /// Opacity of the gaze cursor circle. Valid range: 0–1.
    ///
    /// 1.0, not 0.7: the ring's outline band is opaque regardless (CursorRingStacks applies
    /// this value to the core band alone), so the backdrop-independent edge no longer needs
    /// the core dimmed to stay above the 7.0 floor — at full opacity that edge is
    /// Accent against StrokeHalo, 15.78:1. The earlier 0.7 was chosen when this value scaled
    /// the whole stack; it also composited the accent toward the backdrop, which is what put
    /// it at 1.17:1 over a white document. Lowering this dims the core band toward whatever is
    /// behind the ring, and on a dark backdrop — where the near-black outline has little
    /// contrast of its own — that weakens the ring rather than merely restyling it; no test
    /// covers this, since the contrast tests compare palette colours and do not model alpha
    /// compositing.
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

    partial void OnOneEuroBetaChanging(double value)
    {
        if (value < 0.0 || value > 5.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(OneEuroBeta)} must be between 0 and 5");
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
            ValidationHelper.ValidateRange(OneEuroBeta, 0.0, 5.0, nameof(OneEuroBeta)),
            ValidationHelper.ValidateRange(MaxGapInterpolationMs, 0, 500, nameof(MaxGapInterpolationMs)),
            ValidationHelper.ValidateHexColor(CircleColor, nameof(CircleColor)),
            ValidationHelper.ValidateRange(CircleOpacity, 0.0, 1.0, nameof(CircleOpacity)),
            ValidationHelper.ValidateRange(CircleThickness, 1.0, 10.0, nameof(CircleThickness)),
            ValidationHelper.ValidateRange(CircleSize, 10.0, 500.0, nameof(CircleSize)),
            ValidationHelper.ValidateEnumDefined(RingStyle, nameof(RingStyle))
        );
    }
}
