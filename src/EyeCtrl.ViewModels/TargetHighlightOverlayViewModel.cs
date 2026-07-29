namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class TargetHighlightOverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private PhysicalRect _boundingRect;

    // Default mirrors Tokens.Accent / SettingsDefaults.Accent (#A5F3FC); configured from
    // settings.HighlightColor at runtime, but the initializer must not carry the legacy
    // #0078D4 this migration removed.
    [ObservableProperty]
    private string _highlightColor = "#A5F3FC";

    [ObservableProperty]
    private double _highlightThickness = 3.0;

    [ObservableProperty]
    private double _highlightOpacity = 0.7;

    [ObservableProperty]
    private bool _isVisible;
}
