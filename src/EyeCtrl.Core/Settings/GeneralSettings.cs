namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Enums;

/// <summary>General application settings (startup behavior and gaze provider selection).</summary>
public partial class GeneralSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the application starts automatically on Windows boot.</summary>
    [ObservableProperty] private bool _startOnBoot = false;
    /// <summary>Whether gaze tracking starts in a paused state when the application launches.</summary>
    [ObservableProperty] private bool _startPaused = false;
    /// <summary>Whether the settings window opens in its simple (curated) mode. A UI preference in
    /// the company of <see cref="UiLanguage"/>: nothing outside SettingsWindow reads it, and bulk
    /// resets keep it for the same reason they keep the language (spec §5.1).</summary>
    [ObservableProperty] private bool _simpleSettingsUi = false;
    /// <summary>Gaze provider type to use (Auto, StreamEngine, or Mouse).</summary>
    [ObservableProperty] private GazeProviderType _gazeProviderType = GazeProviderType.Auto;
    /// <summary>Language for EyeCtrl's own UI; <see cref="UiLanguage.System"/> follows the Windows display language.</summary>
    [ObservableProperty] private UiLanguage _uiLanguage = UiLanguage.System;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateEnumDefined(UiLanguage, nameof(UiLanguage)));
    }
}
