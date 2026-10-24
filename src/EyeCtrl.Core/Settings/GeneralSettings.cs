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
    /// <summary>Whether the one-time gaze safety setup (crash recovery, start at sign-in, dwell sound,
    /// track status, settings view) has been answered to the end. False (the default, so an existing
    /// <c>settings.json</c> without the key asks once) until the last answer is given; closing the
    /// chain early leaves it false. Like <see cref="UiLanguage"/> and <see cref="SimpleSettingsUi"/>
    /// it is not a preference to reset: bulk resets keep it, and a "Run safety setup again" row
    /// re-asks on demand.</summary>
    [ObservableProperty] private bool _safetySetupDone = false;
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
