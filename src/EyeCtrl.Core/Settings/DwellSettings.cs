namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DwellSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _fixationDetectionMs = 300;
    [ObservableProperty] private int _activationButtonDwellMs = 700;
    [ObservableProperty] private int _actionMenuDwellMs = 600;
    [ObservableProperty] private int _ManualSelectionActionMenuDwellMs = 600;
    [ObservableProperty] private int _toolbarButtonDwellMs = 700;
    [ObservableProperty] private int _keyboardKeyDwellMs = 450;
    [ObservableProperty] private int _keyboardPredictionDwellMs = 500;
    [ObservableProperty] private double _fixationRadiusPixels = 50;
    [ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
    [ObservableProperty] private int _dwellCooldownMs = 400;

    partial void OnFixationDetectionMsChanging(int value)
    {
        if (value < 100 || value > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(FixationDetectionMs)} must be between 100 and 1000");
    }

    partial void OnActivationButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ActivationButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnActionMenuDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ActionMenuDwellMs)} must be between 200 and 2000");
    }

    partial void OnManualSelectionActionMenuDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ManualSelectionActionMenuDwellMs)} must be between 200 and 2000");
    }

    partial void OnToolbarButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(ToolbarButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnKeyboardKeyDwellMsChanging(int value)
    {
        if (value < 100 || value > 1500)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(KeyboardKeyDwellMs)} must be between 100 and 1500");
    }

    partial void OnKeyboardPredictionDwellMsChanging(int value)
    {
        if (value < 100 || value > 1500)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(KeyboardPredictionDwellMs)} must be between 100 and 1500");
    }

    partial void OnFixationRadiusPixelsChanging(double value)
    {
        if (value < 10.0 || value > 200.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(FixationRadiusPixels)} must be between 10 and 200");
    }

    partial void OnVelocityThresholdPxPerSecChanging(double value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(VelocityThresholdPxPerSec)} must be greater than 0");
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(FixationDetectionMs, 100, 1000, nameof(FixationDetectionMs)),
            ValidationHelper.ValidateRange(ActivationButtonDwellMs, 200, 2000, nameof(ActivationButtonDwellMs)),
            ValidationHelper.ValidateRange(ActionMenuDwellMs, 200, 2000, nameof(ActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ManualSelectionActionMenuDwellMs, 200, 2000, nameof(ManualSelectionActionMenuDwellMs)),
            ValidationHelper.ValidateRange(ToolbarButtonDwellMs, 200, 2000, nameof(ToolbarButtonDwellMs)),
            ValidationHelper.ValidateRange(KeyboardKeyDwellMs, 100, 1500, nameof(KeyboardKeyDwellMs)),
            ValidationHelper.ValidateRange(KeyboardPredictionDwellMs, 100, 1500, nameof(KeyboardPredictionDwellMs)),
            ValidationHelper.ValidateRange(FixationRadiusPixels, 10.0, 200.0, nameof(FixationRadiusPixels))
        );
    }
}
