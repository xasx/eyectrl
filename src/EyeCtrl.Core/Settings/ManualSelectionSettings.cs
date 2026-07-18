namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for manual (zoom-to-point) selection mode.</summary>
public partial class ManualSelectionSettings : ObservableObject, IValidatable
{
    /// <summary>Whether manual selection mode is enabled by default.</summary>
    [ObservableProperty] private bool _enabled = false;
    /// <summary>Radius of the zoom circle in physical pixels. Valid range: 50–500.</summary>
    [ObservableProperty] private int _circleRadiusPixels = 150;
    /// <summary>Magnification level within the zoom circle. Valid range: 1.5–5.0.</summary>
    [ObservableProperty] private double _magnificationLevel = 2.0;
    /// <summary>Grace period in milliseconds after entering manual selection before dwell activates. Valid range: 100–2000.</summary>
    [ObservableProperty] private int _manualSelectionGracePeriodMs = 500;

    /// <summary>Outer diameter of the gaze indicator in logical pixels during manual selection.</summary>
    [ObservableProperty] private double _gazeIndicatorOuterDiameter = 60.0;
    /// <summary>Diameter of the center dot in the gaze indicator in logical pixels.</summary>
    [ObservableProperty] private double _gazeIndicatorCenterDotDiameter = 10.0;
    /// <summary>Stroke thickness of the gaze indicator in logical pixels.</summary>
    [ObservableProperty] private double _gazeIndicatorStrokeThickness = 2.0;
    /// <summary>Opacity of the gaze indicator. Valid range: 0–1.</summary>
    [ObservableProperty] private double _gazeIndicatorOpacity = 0.7;
    /// <summary>Duration in milliseconds of the reset animation when the gaze indicator returns to center.</summary>
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

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(CircleRadiusPixels, 50, 500, nameof(CircleRadiusPixels)),
            ValidationHelper.ValidateRange(MagnificationLevel, 1.5, 5.0, nameof(MagnificationLevel))
        );
    }
}
