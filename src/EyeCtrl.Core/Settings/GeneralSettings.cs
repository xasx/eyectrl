namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Enums;

public partial class GeneralSettings : ObservableObject
{
    [ObservableProperty] private bool _startOnBoot = false;
    [ObservableProperty] private bool _startPaused = false;
    [ObservableProperty] private GazeProviderType _gazeProviderType = GazeProviderType.Auto;
}
