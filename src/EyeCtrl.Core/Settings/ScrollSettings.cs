namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for gaze-joystick scroll mode (sensitivity).</summary>
public partial class ScrollSettings : ObservableObject, IValidatable
{
    private const double SensitivityMin = 0.5;
    private const double SensitivityMax = 2.0;
    private const double SensitivityDefault = 1.0;

    /// <summary>Scroll sensitivity multiplier (0.5–2.0).</summary>
    [ObservableProperty] private double _sensitivity = SensitivityDefault;

    partial void OnSensitivityChanging(double value)
    {
        if (value < SensitivityMin || value > SensitivityMax)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Sensitivity must be between {SensitivityMin} and {SensitivityMax}");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(Sensitivity, SensitivityMin, SensitivityMax, nameof(Sensitivity))
        );
    }
}
