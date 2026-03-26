namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DwellSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _fixationDetectionMs = 300;
    [ObservableProperty] private int _activationButtonDwellMs = 700;
    [ObservableProperty] private int _actionMenuDwellMs = 600;
    [ObservableProperty] private int _zoomActionMenuDwellMs = 600;
    [ObservableProperty] private int _toolbarButtonDwellMs = 700;
    [ObservableProperty] private int _keyboardKeyDwellMs = 450;
    [ObservableProperty] private int _keyboardPredictionDwellMs = 500;
    [ObservableProperty] private double _fixationRadiusPixels = 50;
    [ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
    [ObservableProperty] private int _dwellCooldownMs = 400;

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(FixationDetectionMs, 100, 1000, nameof(FixationDetectionMs)),
            ValidationHelper.ValidateRange(ActivationButtonDwellMs, 200, 2000, nameof(ActivationButtonDwellMs)),
            ValidationHelper.ValidateRange(ActionMenuDwellMs, 200, 2000, nameof(ActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ZoomActionMenuDwellMs, 200, 2000, nameof(ZoomActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ToolbarButtonDwellMs, 200, 2000, nameof(ToolbarButtonDwellMs)),
            ValidationHelper.ValidateRange(KeyboardKeyDwellMs, 100, 1500, nameof(KeyboardKeyDwellMs)),
            ValidationHelper.ValidateRange(KeyboardPredictionDwellMs, 100, 1500, nameof(KeyboardPredictionDwellMs)),
            ValidationHelper.ValidateRange(FixationRadiusPixels, 10.0, 200.0, nameof(FixationRadiusPixels))
        );
    }
}
