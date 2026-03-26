namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class KeyboardSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private string _activeLayout = "QWERTY";
    [ObservableProperty] private double _keySizeMultiplier = 1.0;
    [ObservableProperty] private bool _predictionEnabled = true;
    [ObservableProperty] private string _languagePack = "";
    [ObservableProperty] private string _screenEdge = "Bottom";
    [ObservableProperty] private int _emojiSkinTone = 0;
    [ObservableProperty] private bool _clickSoundEnabled = true;

    private static readonly string[] ValidLayouts = { "QWERTY", "AZERTY", "QWERTZ" };
    private static readonly string[] ValidScreenEdges = { "Top", "Bottom" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(ActiveLayout, ValidLayouts, nameof(ActiveLayout)),
            ValidationHelper.ValidateRange(KeySizeMultiplier, 0.5, 2.0, nameof(KeySizeMultiplier)),
            ValidationHelper.ValidateStringEnum(ScreenEdge, ValidScreenEdges, nameof(ScreenEdge))
        );
    }
}
