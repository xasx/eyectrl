namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Theme and overlay visual style settings.</summary>
public partial class ThemeSettings : ObservableObject, IValidatable
{
    /// <summary>Color theme mode (System, Light, or Dark).</summary>
    [ObservableProperty] private string _mode = "System";
    /// <summary>Opacity of the overlay window. Valid range: 0.1–1.0.</summary>
    [ObservableProperty] private double _overlayOpacity = 0.9;
    /// <summary>Whether high contrast mode is enabled for accessibility.</summary>
    [ObservableProperty] private bool _highContrast = false;

    private static readonly string[] ValidModes = { "System", "Light", "Dark" };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(Mode, ValidModes, nameof(Mode)),
            ValidationHelper.ValidateRange(OverlayOpacity, 0.1, 1.0, nameof(OverlayOpacity))
        );
    }
}
