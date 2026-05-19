namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;

public partial class BackdropSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private BackdropType _type = BackdropType.Acrylic;
    [ObservableProperty] private double _opacity = 0.3;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Opacity < 0.1 || Opacity > 0.9)
            errors.Add($"Backdrop opacity must be between 0.1 and 0.9 (was {Opacity}).");
        if (!Enum.IsDefined(typeof(BackdropType), Type))
            errors.Add($"Invalid backdrop type: {Type}.");
        return errors;
    }
}
