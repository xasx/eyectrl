namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ZoomSettings : ObservableObject
{
    [ObservableProperty] private bool _enabled = false;
    [ObservableProperty] private int _circleRadiusPixels = 150;
    [ObservableProperty] private double _magnificationLevel = 2.0;
    [ObservableProperty] private int _zoomGracePeriodMs = 500;
    [ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;

    [ObservableProperty] private double _gazeIndicatorOuterDiameter = 30.0;
    [ObservableProperty] private double _gazeIndicatorCenterDotDiameter = 10.0;
    [ObservableProperty] private double _gazeIndicatorStrokeThickness = 2.0;
    [ObservableProperty] private double _gazeIndicatorOpacity = 0.7;
    [ObservableProperty] private int _gazeIndicatorResetAnimationMs = 100;
}
