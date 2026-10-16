namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;

/// <summary>Settings for the overlay window backdrop material and opacity.</summary>
public partial class BackdropSettings : ObservableObject, IValidatable
{
    /// <summary>
    /// The opacity range, as ONE pair the guard, <see cref="Validate"/> and the Settings slider all
    /// read — three consumers a hand-copied literal would let drift apart. 0.0 is the owner's "no
    /// darkening" (Glass shows the pure glass; Solid darken shows nothing, accepted) and 0.95 is
    /// "almost black" (about 5 % of the surface still shows through).
    /// </summary>
    public const double OpacityMin = 0.0;

    /// <inheritdoc cref="OpacityMin"/>
    public const double OpacityMax = 0.95;

    /// <summary>Whether the backdrop material effect is enabled.</summary>
    [ObservableProperty] private bool _enabled = true;
    /// <summary>Backdrop material type (Glass or SolidDarken).</summary>
    [ObservableProperty] private BackdropType _type = BackdropType.Glass;
    /// <summary>Opacity of the backdrop effect. Valid range: <see cref="OpacityMin"/>–<see cref="OpacityMax"/>.</summary>
    [ObservableProperty] private double _opacity = 0.3;

    partial void OnOpacityChanging(double value)
    {
        if (value < OpacityMin || value > OpacityMax)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Opacity must be between {OpacityMin} and {OpacityMax}.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(Opacity, OpacityMin, OpacityMax, nameof(Opacity)),
            ValidationHelper.ValidateEnumDefined(Type, nameof(Type))
        );
    }
}
