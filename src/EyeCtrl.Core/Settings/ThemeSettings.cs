namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ThemeSettings : ObservableObject
{
    [ObservableProperty] private string _mode = "System";
    [ObservableProperty] private double _overlayOpacity = 0.9;
    [ObservableProperty] private bool _highContrast = false;
}
