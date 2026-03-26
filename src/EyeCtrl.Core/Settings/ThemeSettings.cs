namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ThemeSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private string _mode = "System";
    [ObservableProperty] private double _overlayOpacity = 0.9;
    [ObservableProperty] private bool _highContrast = false;

    private static readonly string[] ValidModes = { "System", "Light", "Dark" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(Mode, ValidModes, nameof(Mode)),
            ValidationHelper.ValidateRange(OverlayOpacity, 0.1, 1.0, nameof(OverlayOpacity))
        );
    }
}
