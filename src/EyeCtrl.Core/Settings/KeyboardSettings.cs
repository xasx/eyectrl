namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class KeyboardSettings : ObservableObject
{
    [ObservableProperty] private string _activeLayout = "QWERTY";
    [ObservableProperty] private double _keySizeMultiplier = 1.0;
    [ObservableProperty] private bool _predictionEnabled = true;
    [ObservableProperty] private string _languagePack = "en";
    [ObservableProperty] private string _screenEdge = "Bottom";
    [ObservableProperty] private int _emojiSkinTone = 0;
}
