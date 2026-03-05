namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class CursorSettings : ObservableObject
{
    [ObservableProperty] private GazeFilterMode _filterMode = GazeFilterMode.OneEuro;
    [ObservableProperty] private double _smoothingFactor = 0.5;
    [ObservableProperty] private double _oneEuroMinCutoff = 5.0;
    [ObservableProperty] private double _oneEuroBeta = 0.5;
    [ObservableProperty] private int _snapRadiusPixels = 30;
    [ObservableProperty] private int _maxGapInterpolationMs = 150;
    [ObservableProperty] private string _circleColor = "#00FF00";
    [ObservableProperty] private double _circleOpacity = 0.6;
    [ObservableProperty] private double _circleThickness = 2.0;
    [ObservableProperty] private double _circleSize = 250;
    [ObservableProperty] private bool _circleHighContrast = false;
}
