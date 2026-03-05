namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DwellSettings : ObservableObject
{
    [ObservableProperty] private int _fixationDetectionMs = 300;
    [ObservableProperty] private int _activationButtonDwellMs = 700;
    [ObservableProperty] private int _actionMenuDwellMs = 600;
    [ObservableProperty] private int _zoomActionMenuDwellMs = 600;
    [ObservableProperty] private int _toolbarButtonDwellMs = 700;
    [ObservableProperty] private int _keyboardKeyDwellMs = 450;
    [ObservableProperty] private int _keyboardPredictionDwellMs = 500;
    [ObservableProperty] private double _fixationRadiusPixels = 50;
    [ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
}
