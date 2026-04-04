namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ZoomSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = false;
    [ObservableProperty] private int _circleRadiusPixels = 150;
    [ObservableProperty] private double _magnificationLevel = 2.0;
    [ObservableProperty] private int _zoomGracePeriodMs = 500;
    [ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;

    [ObservableProperty] private double _gazeIndicatorOuterDiameter = 30.0;
    [ObservableProperty] private double _gazeIndicatorCenterDotDiameter = 10.0;
    [ObservableProperty] private double _gazeIndicatorStrokeThickness = 2.0;
    [ObservableProperty] private double _gazeIndicatorOpacity = 0.7;
    [ObservableProperty] private int _gazeIndicatorResetAnimationMs = 100;

    partial void OnCircleRadiusPixelsChanging(int value)
    {
        if (value < 50 || value > 500)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(CircleRadiusPixels)} must be between 50 and 500");
    }

    partial void OnMagnificationLevelChanging(double value)
    {
        if (value < 1.5 || value > 5.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(MagnificationLevel)} must be between 1.5 and 5");
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(CircleRadiusPixels, 50, 500, nameof(CircleRadiusPixels)),
            ValidationHelper.ValidateRange(MagnificationLevel, 1.5, 5.0, nameof(MagnificationLevel))
        );
    }
}
