namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for manual (zoom-to-point) selection mode.</summary>
public partial class ManualSelectionSettings : ObservableObject, IValidatable
{
    /// <summary>Whether manual selection mode is enabled by default.</summary>
    [ObservableProperty] private bool _enabled = false;
    /// <summary>Radius of the zoom circle in DIP (multiplied by the display scale for capture). Valid range: 50–500.</summary>
    [ObservableProperty] private int _circleRadiusPixels = 100;
    /// <summary>Magnification level within the zoom circle. Valid range: 1.5–5.0.</summary>
    [ObservableProperty] private double _magnificationLevel = 4.0;
    /// <summary>Grace period in milliseconds after entering manual selection before dwell activates. Not range-guarded: neither OnManualSelectionGracePeriodMsChanging nor Validate restricts it, and the settings UI offers 0–2000.</summary>
    [ObservableProperty] private int _manualSelectionGracePeriodMs = 500;

    /// <summary>Outer diameter of the gaze indicator in logical pixels during manual selection.</summary>
    [ObservableProperty] private double _gazeIndicatorOuterDiameter = 60.0;
    /// <summary>Diameter of the center dot in the gaze indicator in logical pixels.</summary>
    [ObservableProperty] private double _gazeIndicatorCenterDotDiameter = 10.0;
    /// <summary>Stroke thickness of the gaze indicator in logical pixels.</summary>
    [ObservableProperty] private double _gazeIndicatorStrokeThickness = 2.0;
    /// <summary>Opacity of the gaze indicator. Valid range: 0.1–1.0.</summary>
    [ObservableProperty] private double _gazeIndicatorOpacity = 0.7;
    /// <summary>Duration in milliseconds of the reset animation when the gaze indicator returns to center.</summary>
    [ObservableProperty] private int _gazeIndicatorResetAnimationMs = 100;

    /// <summary>
    /// Hold tolerance in DIP: how far the gaze may sit from the seated dwell anchor before the
    /// dwell is abandoned. This is the strictness knob — raise it toward the visible circle's
    /// logical radius for "always clicks, sometimes off-target", lower it for "clicks where you
    /// aimed, sometimes refuses". Magnification is deliberately NOT applied: hold tolerance is a
    /// property of the eye and lives in screen space, so it does not scale with the magnifier.
    /// Default 72 (the action-menu ring buttons are 70 DIP at the same 600 ms dwell, so this is
    /// a lower bound on what the user can hold, not a measured sigma).
    /// </summary>
    [ObservableProperty] private double _holdRadiusLogical = 72.0;

    /// <summary>Stability samples (K) required inside the onset window before the dwell anchor
    /// seats. Conservative by design: sigma is unmeasured, so it governs K. Valid range: 2–30.</summary>
    [ObservableProperty] private int _stabilitySampleCount = 5;

    /// <summary>Size of the sample window (M) scanned for stability onset. Values below
    /// <see cref="StabilitySampleCount"/> are lifted to it. Valid range: 2–60.</summary>
    [ObservableProperty] private int _stabilityWindowSize = 7;

    /// <summary>Consecutive out-of-hold samples tolerated (skip-N) before the anchor is dropped
    /// and the dwell restarts. Absorbs isolated gaze outliers; DwellDetector is timestamp-based,
    /// so elapsed dwell time survives the skipped samples. Valid range: 0–30.</summary>
    [ObservableProperty] private int _holdOutlierSkipLimit = 3;

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
            ValidationHelper.ValidateRange(MagnificationLevel, 1.5, 5.0, nameof(MagnificationLevel)),
            ValidationHelper.ValidateRange(GazeIndicatorOuterDiameter, 20, 200, nameof(GazeIndicatorOuterDiameter)),
            ValidationHelper.ValidateRange(GazeIndicatorCenterDotDiameter, 2, 40, nameof(GazeIndicatorCenterDotDiameter)),
            ValidationHelper.ValidateRange(GazeIndicatorStrokeThickness, 0.5, 10, nameof(GazeIndicatorStrokeThickness)),
            ValidationHelper.ValidateRange(GazeIndicatorOpacity, 0.1, 1.0, nameof(GazeIndicatorOpacity)),
            ValidationHelper.ValidateRange(GazeIndicatorResetAnimationMs, 0, 1000, nameof(GazeIndicatorResetAnimationMs)),
            ValidationHelper.ValidateRange(HoldRadiusLogical, 20, 300, nameof(HoldRadiusLogical)),
            ValidationHelper.ValidateRange(StabilitySampleCount, 2, 30, nameof(StabilitySampleCount)),
            ValidationHelper.ValidateRange(StabilityWindowSize, 2, 60, nameof(StabilityWindowSize)),
            ValidationHelper.ValidateRange(HoldOutlierSkipLimit, 0, 30, nameof(HoldOutlierSkipLimit)),
            StabilityWindowSize >= StabilitySampleCount
                ? null
                : $"{nameof(StabilityWindowSize)} must be at least {nameof(StabilitySampleCount)} " +
                  $"({StabilityWindowSize} < {StabilitySampleCount})");
    }
}
