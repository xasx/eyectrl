namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ZoomSettings : ObservableObject
{
    [ObservableProperty] private bool _enabled = false;
    [ObservableProperty] private int _circleRadiusPixels = 150;
    [ObservableProperty] private double _magnificationLevel = 2.0;
    [ObservableProperty] private int _zoomGracePeriodMs = 500;
}
