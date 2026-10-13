namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Which palette the overlay UI uses. See docs/design/overlay-unification-spec.md
/// §4.1 (Midnight), §10.2 (Ember), §10.7 (Contrast) and §10.8 (Daylight).
///
/// Replaces three flags — Mode ("System"/"Light"/"Dark"), OverlayOpacity and
/// HighContrast — that were persisted and surfaced in the settings UI but read by
/// nothing. Mode's light/dark axis was never buildable anyway: §2 originally ruled
/// out a light theme outright, because eye trackers work poorly against bright
/// surfaces. Daylight (§10.8) reopens that axis deliberately and records the
/// ruling change there.
/// Legacy keys are simply ignored on load (unknown JSON members), so there is no
/// migration code.
/// </summary>
public partial class ThemeSettings : ObservableObject, IValidatable
{
    /// <summary>
    /// Names accepted by <see cref="Name"/>, in picker order.
    ///
    /// Restated here because EyeCtrl.Core cannot reference EyeCtrl.Controls, where
    /// ThemeRegistry lives; ThemeSettingsNamesTests in EyeCtrl.App.Tests keeps the
    /// two in step, the same way SettingsDefaults is kept in step with Tokens.
    /// </summary>
    public static readonly string[] ValidNames = ["Midnight", "Ember", "Daylight", "Contrast"];

    /// <summary>
    /// The active palette's name. Applied at startup; changing it takes effect on
    /// the next launch, because a theme change recreates windows rather than
    /// re-resolving live (spec §10.6).
    /// </summary>
    [ObservableProperty] private string _name = "Midnight";

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(Name, ValidNames, nameof(Name))
        );
    }
}
