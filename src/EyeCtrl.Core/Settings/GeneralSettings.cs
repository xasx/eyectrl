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
    /// <summary>Gaze provider type to use (Auto, StreamEngine, or Mouse).</summary>
    [ObservableProperty] private GazeProviderType _gazeProviderType = GazeProviderType.Auto;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        // GeneralSettings only has booleans and enums - no validation needed
        return new List<string>();
    }
}
