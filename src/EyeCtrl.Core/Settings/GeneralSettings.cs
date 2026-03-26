namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Enums;

public partial class GeneralSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _startOnBoot = false;
    [ObservableProperty] private bool _startPaused = false;
    [ObservableProperty] private GazeProviderType _gazeProviderType = GazeProviderType.Auto;

    public IReadOnlyList<string> Validate()
    {
        // GeneralSettings only has booleans and enums - no validation needed
        return new List<string>();
    }
}
