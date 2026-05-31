namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class TargetHighlightOverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private PhysicalRect _boundingRect;

    [ObservableProperty]
    private string _highlightColor = "#0078D4";

    [ObservableProperty]
    private double _highlightThickness = 3.0;

    [ObservableProperty]
    private double _highlightOpacity = 0.7;

    [ObservableProperty]
    private bool _isVisible;
}
