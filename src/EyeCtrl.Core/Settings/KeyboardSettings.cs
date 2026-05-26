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
    [ObservableProperty] private bool _smartCapitalizationEnabled = true;

    // Phase 4: Prediction UI & Configuration Settings
    [ObservableProperty] private bool _nextWordPredictionEnabled = true;
    [ObservableProperty] private bool _showPredictionConfidence = false;
    [ObservableProperty] private bool _showPredictionTypes = false;
    [ObservableProperty] private bool _emojiSuggestionsEnabled = false;

    // Phase 8: Decay Mechanism Settings
    [ObservableProperty] private bool _decayEnabled = true;
    [ObservableProperty] private int _decayHalfLifeDays = 30;

    // ML Model Settings
    [ObservableProperty] private bool _mLSuggestionsEnabled = true;
    [ObservableProperty] private string _mLModelSize = "360M";
    [ObservableProperty] private bool _showLMLoadingIndicator = true;
    [ObservableProperty] private bool _preferGPU = true;
    [ObservableProperty] private List<CustomModelEntry> _customModels = [];

    private static readonly string[] ValidLayouts = { "QWERTY", "AZERTY", "QWERTZ" };
    private static readonly string[] ValidScreenEdges = { "Top", "Bottom" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(ActiveLayout, ValidLayouts, nameof(ActiveLayout)),
            ValidationHelper.ValidateRange(KeySizeMultiplier, 0.5, 2.0, nameof(KeySizeMultiplier)),
            ValidationHelper.ValidateStringEnum(ScreenEdge, ValidScreenEdges, nameof(ScreenEdge)),
            ValidationHelper.ValidateRange(DecayHalfLifeDays, 1, 365, nameof(DecayHalfLifeDays))
        );
    }
}

public class CustomModelEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string TokenizerUrl { get; set; } = "";
    public string TokenizerSha256 { get; set; } = "";
}
