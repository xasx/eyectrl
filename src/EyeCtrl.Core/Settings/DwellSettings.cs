namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Dwell timing settings for gaze-based click activation.</summary>
public partial class DwellSettings : ObservableObject, IValidatable
{
    /// <summary>Time in milliseconds to detect a fixation before dwell begins. Valid range: 100–1000.</summary>
    [ObservableProperty] private int _fixationDetectionMs = 300;
    /// <summary>Dwell time in milliseconds for activation buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _activationButtonDwellMs = 700;
    /// <summary>Dwell time in milliseconds for action menu buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _actionMenuDwellMs = 600;
    /// <summary>Dwell time in milliseconds for manual selection action menu buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _ManualSelectionActionMenuDwellMs = 600;
    /// <summary>Dwell time in milliseconds for toolbar buttons. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _toolbarButtonDwellMs = 700;
    /// <summary>Dwell time in milliseconds for keyboard keys. Valid range: 100–1500.</summary>
    [ObservableProperty] private int _keyboardKeyDwellMs = 450;
    /// <summary>Dwell time in milliseconds for prediction suggestion buttons. Valid range: 100–1500.</summary>
    [ObservableProperty] private int _keyboardPredictionDwellMs = 500;
    /// <summary>Radius in pixels within which gaze samples are considered a fixation. Valid range: 10–200.</summary>
    [ObservableProperty] private double _fixationRadiusPixels = 50;
    /// <summary>Velocity threshold in pixels per second below which gaze is considered fixating.</summary>
    [ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
    /// <summary>Cooldown period in milliseconds after a dwell activation before another can trigger. Valid range: 100–5000.</summary>
    [ObservableProperty] private int _dwellCooldownMs = 400;
    /// <summary>Dwell time in milliseconds for the drag release button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _dragReleaseButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for the drag right-edge button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _dragRightEdgeButtonDwellMs = 600;
    /// <summary>Dwell time in milliseconds for the scroll stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _scrollStopButtonDwellMs = 600;

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

    partial void OnDwellCooldownMsChanging(int value)
    {
        if (value < 100 || value > 5000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DwellCooldownMs)} must be between 100 and 5000");
    }

    partial void OnDragReleaseButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DragReleaseButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnDragRightEdgeButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DragRightEdgeButtonDwellMs)} must be between 200 and 2000");
    }

    partial void OnScrollStopButtonDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(ScrollStopButtonDwellMs)} must be between 200 and 2000");
    }

    /// <inheritdoc />
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
            ValidationHelper.ValidateRange(FixationRadiusPixels, 10.0, 200.0, nameof(FixationRadiusPixels)),
            VelocityThresholdPxPerSec > 0
                ? null
                : $"{nameof(VelocityThresholdPxPerSec)} must be greater than 0, but was {VelocityThresholdPxPerSec}",
            ValidationHelper.ValidateRange(DwellCooldownMs, 100, 5000, nameof(DwellCooldownMs)),
            ValidationHelper.ValidateRange(DragReleaseButtonDwellMs, 200, 2000, nameof(DragReleaseButtonDwellMs)),
            ValidationHelper.ValidateRange(DragRightEdgeButtonDwellMs, 200, 2000, nameof(DragRightEdgeButtonDwellMs)),
            ValidationHelper.ValidateRange(ScrollStopButtonDwellMs, 200, 2000, nameof(ScrollStopButtonDwellMs))
        );
    }
}
