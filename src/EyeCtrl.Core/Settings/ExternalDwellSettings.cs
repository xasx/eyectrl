namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ExternalDwellSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _defaultDwellMs = 800;
    [ObservableProperty] private int _cooldownMs = 400;

    partial void OnDefaultDwellMsChanging(int value)
    {
        if (value < 200 || value > 3000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(DefaultDwellMs)} must be between 200 and 3000");
    }

    partial void OnCooldownMsChanging(int value)
    {
        if (value < 0 || value > 5000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(CooldownMs)} must be between 0 and 5000");
    }

    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DefaultDwellMs, 200, 3000, nameof(DefaultDwellMs)),
        ValidationHelper.ValidateRange(CooldownMs, 0, 5000, nameof(CooldownMs)));
}
