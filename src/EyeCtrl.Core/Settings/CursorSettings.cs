namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>Cursor filtering and visual appearance settings.</summary>
public partial class CursorSettings : ObservableObject, IValidatable
{
    /// <summary>Gaze filtering strategy applied to raw gaze samples.</summary>
    [ObservableProperty] private GazeFilterMode _filterMode = GazeFilterMode.OneEuro;
    /// <summary>Smoothing factor for EMA filter. 0 = no filtering, 1 = maximum smoothing. Valid range: 0–1.</summary>
    [ObservableProperty] private double _smoothingFactor = 0.5;
    /// <summary>Minimum cutoff frequency in Hz for the 1€ filter. Lower values produce more smoothing. Valid range: 0.1–20.</summary>
    [ObservableProperty] private double _oneEuroMinCutoff = 5.0;
    /// <summary>Speed coefficient (beta) for the 1€ filter. Controls responsiveness to rapid movements. Valid range: 0–5.</summary>
    [ObservableProperty] private double _oneEuroBeta = 0.5;
    /// <summary>Snap radius in physical pixels for control snapping. Points within this distance snap to the nearest control. Valid range: 0–100.</summary>
    [ObservableProperty] private int _snapRadiusPixels = 30;
    /// <summary>Maximum gap in milliseconds to interpolate between missing gaze samples. Valid range: 0–500.</summary>
    [ObservableProperty] private int _maxGapInterpolationMs = 150;
    /// <summary>Hex color of the gaze cursor circle (e.g., "#00FF00").</summary>
    [ObservableProperty] private string _circleColor = "#00FF00";
    /// <summary>Opacity of the gaze cursor circle. Valid range: 0–1.</summary>
    [ObservableProperty] private double _circleOpacity = 0.6;
    /// <summary>Thickness of the gaze cursor circle stroke in logical pixels. Valid range: 1–10.</summary>
    [ObservableProperty] private double _circleThickness = 2.0;
    /// <summary>Diameter of the gaze cursor circle in logical pixels. Valid range: 10–500.</summary>
    [ObservableProperty] private double _circleSize = 250;
    /// <summary>Whether the cursor circle uses a high-contrast color scheme.</summary>
    [ObservableProperty] private bool _circleHighContrast = false;
    /// <summary>Visual style of the position indicator shown at the gaze point.</summary>
    [ObservableProperty] private PositionIndicatorStyle _positionIndicatorStyle = PositionIndicatorStyle.Crosshair;

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

    partial void OnSnapRadiusPixelsChanging(int value)
    {
        if (value < 0 || value > 100)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(SnapRadiusPixels)} must be between 0 and 100");
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
            ValidationHelper.ValidateRange(SnapRadiusPixels, 0, 100, nameof(SnapRadiusPixels)),
            ValidationHelper.ValidateHexColor(CircleColor, nameof(CircleColor)),
            ValidationHelper.ValidateRange(CircleOpacity, 0.0, 1.0, nameof(CircleOpacity)),
            ValidationHelper.ValidateRange(CircleThickness, 1.0, 10.0, nameof(CircleThickness)),
            ValidationHelper.ValidateRange(CircleSize, 10.0, 500.0, nameof(CircleSize))
        );
    }
}
