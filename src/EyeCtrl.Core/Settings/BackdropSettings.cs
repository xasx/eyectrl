namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;

public partial class BackdropSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private BackdropType _type = BackdropType.Acrylic;
    [ObservableProperty] private double _opacity = 0.3;

    partial void OnOpacityChanging(double value)
    {
        if (value < 0.1 || value > 0.9)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Opacity must be between 0.1 and 0.9.");
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(Opacity, 0.1, 0.9, nameof(Opacity)),
            ValidationHelper.ValidateEnumDefined(Type, nameof(Type))
        );
    }
}
