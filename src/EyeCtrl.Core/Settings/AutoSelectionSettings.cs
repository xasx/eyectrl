namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class AutoSelectionSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private string _highlightColor = "#0078D4";
    [ObservableProperty] private double _highlightThickness = 3.0;
    [ObservableProperty] private double _highlightOpacity = 0.7;
    [ObservableProperty] private bool _fallbackToPixelOnNoControl = true;

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateHexColor(HighlightColor, nameof(HighlightColor)),
            ValidationHelper.ValidateRange(HighlightThickness, 1.0, 10.0, nameof(HighlightThickness)),
            ValidationHelper.ValidateRange(HighlightOpacity, 0.1, 1.0, nameof(HighlightOpacity))
        );
    }
}
