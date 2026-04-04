namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class CursorSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private GazeFilterMode _filterMode = GazeFilterMode.OneEuro;
    [ObservableProperty] private double _smoothingFactor = 0.5;
    [ObservableProperty] private double _oneEuroMinCutoff = 5.0;
    [ObservableProperty] private double _oneEuroBeta = 0.5;
    [ObservableProperty] private int _snapRadiusPixels = 30;
    [ObservableProperty] private int _maxGapInterpolationMs = 150;
    [ObservableProperty] private string _circleColor = "#00FF00";
    [ObservableProperty] private double _circleOpacity = 0.6;
    [ObservableProperty] private double _circleThickness = 2.0;
    [ObservableProperty] private double _circleSize = 250;
    [ObservableProperty] private bool _circleHighContrast = false;
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
