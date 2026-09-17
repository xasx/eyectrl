namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Tracks user's preferred casing for words and learns from corrections over time.
/// Helps adapt to personal preferences (e.g., user prefers "Haus" over "haus" in German).
/// </summary>
public class CasingPreferenceTracker
{
    // Maps lowercase word -> casing variant -> frequency count
    private readonly Dictionary<string, Dictionary<string, int>> _casingVariants = new(StringComparer.OrdinalIgnoreCase);

    // Cache for preferred casing to avoid O(n log n) sort on every call
    private readonly Dictionary<string, string> _preferredCasingCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a word with its specific casing, incrementing the frequency for that variant.
    /// </summary>
    /// <param name="word">The word with its user-preferred casing.</param>
    public void RecordCasing(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.ContainsKey(lowerKey))
            _casingVariants[lowerKey] = new Dictionary<string, int>();

        var variants = _casingVariants[lowerKey];
        var oldCount = variants.GetValueOrDefault(word);
        variants[word] = oldCount + 1;

        // Update cache: if this variant now has higher frequency, or is the first variant
        if (!_preferredCasingCache.TryGetValue(lowerKey, out var currentPreferred) ||
            variants[word] > variants.GetValueOrDefault(currentPreferred))
        {
            _preferredCasingCache[lowerKey] = word;
        }
    }

    /// <summary>
    /// Gets the user's preferred casing for a word based on historical usage.
    /// Returns the most frequently used casing variant.
    /// </summary>
    /// <param name="word">The word in any casing.</param>
    /// <returns>The most frequently used casing variant, or the original word if no preference exists.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(word))]
    public string? GetPreferredCasing(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return word;

        var lowerKey = word.ToLowerInvariant();

        // Use cached preferred casing for O(1) lookup
        if (_preferredCasingCache.TryGetValue(lowerKey, out var preferred))
            return preferred;

        return word;
    }

    /// <summary>
    /// Checks if a preferred casing exists for a word.
    /// </summary>
    /// <param name="word">The word to check.</param>
    /// <returns>True if a casing preference has been recorded for this word.</returns>
    public bool HasPreference(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var lowerKey = word.ToLowerInvariant();
        return _casingVariants.ContainsKey(lowerKey) && _casingVariants[lowerKey].Count > 0;
    }

    /// <summary>
    /// Gets all casing preferences for serialization.
    /// Returns a defensive copy to prevent external modification.
    /// </summary>
    /// <returns>Dictionary mapping lowercase words to their casing variants with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> GetAllPreferences()
    {
        var copy = new Dictionary<string, Dictionary<string, int>>(_casingVariants.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (lowerKey, variants) in _casingVariants)
        {
            copy[lowerKey] = new Dictionary<string, int>(variants);
        }
        return copy;
    }

    /// <summary>
    /// Applies a frequency delta to one (lowerword → variant) entry — the delta-merge
    /// counterpart of <see cref="GetAllPreferences"/> snapshots. Used by the background
    /// prediction-stack swap.
    /// </summary>
    internal void LearnEntry(string lowerWord, string variant, int countDelta)
    {
        if (countDelta <= 0 || string.IsNullOrWhiteSpace(lowerWord) || string.IsNullOrWhiteSpace(variant))
            return;
        if (!_casingVariants.ContainsKey(lowerWord))
            _casingVariants[lowerWord] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _casingVariants[lowerWord][variant] = _casingVariants[lowerWord].GetValueOrDefault(variant) + countDelta;
        _preferredCasingCache.Remove(lowerWord); // the merge may change the winning variant
    }

    /// <summary>
    /// Loads casing preferences from a previously serialized state.
    /// Clears existing data before loading.
    /// </summary>
    /// <param name="data">The casing preference data to load.</param>
    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _casingVariants.Clear();
        _preferredCasingCache.Clear();

        foreach (var (lowerKey, variants) in data)
        {
            // Skip null or empty variant entries to prevent NullReferenceException
            if (variants == null || variants.Count == 0)
                continue;

            _casingVariants[lowerKey] = new Dictionary<string, int>(variants);

            // Rebuild cache: find the variant with the highest frequency
            var maxVariant = variants.OrderByDescending(kv => kv.Value).First();
            _preferredCasingCache[lowerKey] = maxVariant.Key;
        }
    }
}
