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
