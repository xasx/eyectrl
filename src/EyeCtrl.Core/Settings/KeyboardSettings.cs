namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Keyboard layout and typing behavior settings.</summary>
public partial class KeyboardSettings : ObservableObject, IValidatable
{
    /// <summary>Active keyboard layout name (QWERTY, AZERTY, or QWERTZ).</summary>
    [ObservableProperty] private string _activeLayout = "QWERTY";
    /// <summary>Multiplier applied to key sizes for accessibility. Valid range: 0.5–2.0.</summary>
    [ObservableProperty] private double _keySizeMultiplier = 1.0;
    /// <summary>Whether word prediction suggestions are shown above the keyboard.</summary>
    [ObservableProperty] private bool _predictionEnabled = true;
    /// <summary>
    /// Maximum number of suggestions in the prediction bar. Valid range:
    /// 1–<see cref="MaxSuggestionsCeiling"/> — bind the constant, never a hand-copied literal.
    /// Fewer suggestions means fewer dwell targets to mis-hit; the bar renders one cell per
    /// suggestion, so a lower count simply leaves the tail empty. The renderer is dynamic — 5
    /// is today's hard-coded fill and stays the default.
    /// </summary>
    [ObservableProperty] private int _maxSuggestions = 5;

    /// <summary>
    /// The upper bound of <see cref="MaxSuggestions"/> as a single constant: the settings
    /// range-check, the load clamp, the Keyboard tab's slider and WordPredictor.GetSuggestions'
    /// optional-parameter default must all agree, and a hand-copied literal in any of them
    /// silently diverges the moment this widens (issue #159). Compile-time const so it can
    /// also serve as the predictor's C# default value.
    /// </summary>
    public const int MaxSuggestionsCeiling = 5;
    /// <summary>Language pack identifier for dictionary loading.</summary>
    [ObservableProperty] private string _languagePack = "";
    /// <summary>Edge of the screen where the keyboard is docked (Top or Bottom).</summary>
    [ObservableProperty] private string _screenEdge = "Bottom";
    /// <summary>Emoji skin tone index (0 = none/default, 1–5 = Fitzpatrick scale).</summary>
    [ObservableProperty] private int _emojiSkinTone = 0;
    /// <summary>Whether a click sound is played on each key activation.</summary>
    [ObservableProperty] private bool _clickSoundEnabled = true;
    /// <summary>Whether automatic capitalization is applied based on context.</summary>
    [ObservableProperty] private bool _smartCapitalizationEnabled = true;

    /// <summary>Whether next-word prediction after a completed word is enabled.</summary>
    [ObservableProperty] private bool _nextWordPredictionEnabled = true;
    /// <summary>Whether to show confidence scores next to predictions.</summary>
    [ObservableProperty] private bool _showPredictionConfidence = false;
    /// <summary>Whether to show prediction source labels (bigram, phrase, ML).</summary>
    [ObservableProperty] private bool _showPredictionTypes = false;

    /// <summary>Whether decay-based frequency weighting is enabled for learned words.</summary>
    [ObservableProperty] private bool _decayEnabled = true;
    /// <summary>Half-life in days for learned word frequency decay. Valid range: 1–365.</summary>
    [ObservableProperty] private int _decayHalfLifeDays = 30;

    /// <summary>Whether small language model (ML) suggestions are enabled.</summary>
    [ObservableProperty] private bool _mLSuggestionsEnabled = true;
    /// <summary>Small language model size identifier (e.g., "360M").</summary>
    [ObservableProperty] private string _mLModelSize = "360M";
    /// <summary>Whether to show a loading indicator while the ML model initializes.</summary>
    [ObservableProperty] private bool _showLMLoadingIndicator = true;
    /// <summary>Whether to prefer GPU inference for the ML model.</summary>
    [ObservableProperty] private bool _preferGPU = true;
    /// <summary>User-defined custom model entries for alternative ML models.</summary>
    [ObservableProperty] private List<CustomModelEntry> _customModels = [];

    private static readonly string[] ValidLayouts = { "QWERTY", "AZERTY", "QWERTZ" };
    private static readonly string[] ValidScreenEdges = { "Top", "Bottom" };

    private static readonly System.Text.RegularExpressions.Regex LanguagePackPattern =
        new("^[a-z]{2}(-[A-Za-z]{2})?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="value"/> is a safe language-pack code (two-letter ISO, optional
    /// region, or empty for auto-detect). The value is a PATH INGREDIENT — dictionary and trie
    /// reads and the user-dictionary write all interpolate it — so a hand-edited
    /// <c>..\..\evil</c> would read and write outside the app-data/install trees. Sanitized to
    /// empty (never a validation error: that would wipe the whole settings file for one string).
    /// </summary>
    public static bool IsValidLanguagePack(string? value) =>
        string.IsNullOrEmpty(value) || LanguagePackPattern.IsMatch(value);

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(ActiveLayout, ValidLayouts, nameof(ActiveLayout)),
            ValidationHelper.ValidateRange(KeySizeMultiplier, 0.5, 2.0, nameof(KeySizeMultiplier)),
            ValidationHelper.ValidateStringEnum(ScreenEdge, ValidScreenEdges, nameof(ScreenEdge)),
            ValidationHelper.ValidateRange(DecayHalfLifeDays, 1, 365, nameof(DecayHalfLifeDays)),
            ValidationHelper.ValidateRange(MaxSuggestions, 1, MaxSuggestionsCeiling, nameof(MaxSuggestions))
        );
    }
}

/// <summary>User-defined custom ML model entry for word prediction.</summary>
public class CustomModelEntry
{
    /// <summary>Unique identifier for this custom model.</summary>
    public string Id { get; set; } = "";
    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>URL from which to download the model file.</summary>
    public string Url { get; set; } = "";
    /// <summary>SHA-256 hash of the model file for integrity verification.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>Size of the model file in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>URL from which to download the tokenizer file.</summary>
    public string TokenizerUrl { get; set; } = "";
    /// <summary>SHA-256 hash of the tokenizer file for integrity verification.</summary>
    public string TokenizerSha256 { get; set; } = "";
}
