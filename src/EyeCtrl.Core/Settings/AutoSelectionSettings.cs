namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for automatic control selection (snap-to-control via UIA/Win32).</summary>
public partial class AutoSelectionSettings : ObservableObject, IValidatable
{
    /// <summary>Hex color of the highlight border around the selected control (e.g., "#A5F3FC").</summary>
    [ObservableProperty] private string _highlightColor = SettingsDefaults.Accent;
    /// <summary>Thickness of the highlight border in logical pixels. Valid range: 1–10.</summary>
    [ObservableProperty] private double _highlightThickness = 3.0;
    /// <summary>Opacity of the highlight overlay. Valid range: 0.1–1.0.</summary>
    [ObservableProperty] private double _highlightOpacity = 0.7;
    /// <summary>If <c>true</c>, falls back to the raw pixel position when no control is found nearby.</summary>
    [ObservableProperty] private bool _fallbackToPixelOnNoControl = true;
    /// <summary>
    /// How far, in physical pixels, the snap-to-control search probes outward from the gaze point
    /// when no interactive control sits directly under it. Valid range: 16–96, in steps of 16 —
    /// the search walks square rings one <c>SearchStepPixels</c> apart, so only multiples of 16
    /// add a ring, and each added ring costs up to 8 more cross-process UIA probes against the
    /// caller's 500&#160;ms resolution budget.
    /// </summary>
    [ObservableProperty] private int _snapSearchRadiusPixels = 48;

    partial void OnHighlightThicknessChanging(double value)
    {
        if (value < 1 || value > 10)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(HighlightThickness)} must be between 1 and 10");
    }

    partial void OnHighlightOpacityChanging(double value)
    {
        if (value < 0.1 || value > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(HighlightOpacity)} must be between 0.1 and 1.0");
    }

    partial void OnSnapSearchRadiusPixelsChanging(int value)
    {
        if (value < 16 || value > 96)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{nameof(SnapSearchRadiusPixels)} must be between 16 and 96");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateHexColor(HighlightColor, nameof(HighlightColor)),
            ValidationHelper.ValidateRange(HighlightThickness, 1.0, 10.0, nameof(HighlightThickness)),
            ValidationHelper.ValidateRange(HighlightOpacity, 0.1, 1.0, nameof(HighlightOpacity)),
            ValidationHelper.ValidateRange(SnapSearchRadiusPixels, 16, 96, nameof(SnapSearchRadiusPixels))
        );
    }
}
