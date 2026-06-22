namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for external dwell (hardware-button-triggered) activation.</summary>
public partial class ExternalDwellSettings : ObservableObject, IValidatable
{
    /// <summary>Whether external dwell activation is enabled.</summary>
    [ObservableProperty] private bool _enabled = true;
    /// <summary>Default dwell duration in milliseconds for external activation. Valid range: 200–3000.</summary>
    [ObservableProperty] private int _defaultDwellMs = 800;
    /// <summary>Cooldown period in milliseconds between external dwell activations. Valid range: 0–5000.</summary>
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

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DefaultDwellMs, 200, 3000, nameof(DefaultDwellMs)),
        ValidationHelper.ValidateRange(CooldownMs, 0, 5000, nameof(CooldownMs)));
}
