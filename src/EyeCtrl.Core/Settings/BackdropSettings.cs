namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;

/// <summary>Settings for the overlay window backdrop material and opacity.</summary>
public partial class BackdropSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the backdrop material effect is enabled.</summary>
    [ObservableProperty] private bool _enabled = true;
    /// <summary>Backdrop material type (Acrylic, Mica, or SolidDarken).</summary>
    [ObservableProperty] private BackdropType _type = BackdropType.Acrylic;
    /// <summary>Opacity of the backdrop effect. Valid range: 0.1–0.9.</summary>
    [ObservableProperty] private double _opacity = 0.3;

    partial void OnOpacityChanging(double value)
    {
        if (value < 0.1 || value > 0.9)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Opacity must be between 0.1 and 0.9.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(Opacity, 0.1, 0.9, nameof(Opacity)),
            ValidationHelper.ValidateEnumDefined(Type, nameof(Type))
        );
    }
}
