namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class GeneralSettings : ObservableObject
{
    [ObservableProperty] private bool _startOnBoot = false;
    [ObservableProperty] private bool _startPaused = false;
}
