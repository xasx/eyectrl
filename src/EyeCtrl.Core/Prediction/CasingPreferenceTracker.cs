namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Tracks user's preferred casing for words and learns from corrections over time.
/// Helps adapt to personal preferences (e.g., user prefers "Haus" over "haus" in German).
/// </summary>
public class CasingPreferenceTracker
{
    // Maps lowercase word -> casing variant -> frequency count
    private readonly Dictionary<string, Dictionary<string, int>> _casingVariants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a word with its specific casing, incrementing the frequency for that variant.
    /// </summary>
    /// <param name="word">The word with its user-preferred casing.</param>
    public void RecordCasing(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.ContainsKey(lowerKey))
            _casingVariants[lowerKey] = new Dictionary<string, int>();

        var variants = _casingVariants[lowerKey];
        variants[word] = variants.GetValueOrDefault(word) + 1;
    }

    /// <summary>
    /// Gets the user's preferred casing for a word based on historical usage.
    /// Returns the most frequently used casing variant.
    /// </summary>
    /// <param name="word">The word in any casing.</param>
    /// <returns>The most frequently used casing variant, or the original word if no preference exists.</returns>
    public string GetPreferredCasing(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return word;

        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.TryGetValue(lowerKey, out var variants) || variants.Count == 0)
            return word;

        // Return the variant with the highest frequency
        return variants.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// Checks if a preferred casing exists for a word.
    /// </summary>
    /// <param name="word">The word to check.</param>
    /// <returns>True if a casing preference has been recorded for this word.</returns>
    public bool HasPreference(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        var lowerKey = word.ToLowerInvariant();
        return _casingVariants.ContainsKey(lowerKey) && _casingVariants[lowerKey].Count > 0;
    }

    /// <summary>
    /// Gets all casing variants and their frequencies for a word.
    /// </summary>
    /// <param name="word">The word to get variants for.</param>
    /// <returns>Dictionary mapping casing variants to their frequencies.</returns>
    public Dictionary<string, int> GetVariants(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return new Dictionary<string, int>();

        var lowerKey = word.ToLowerInvariant();

        if (!_casingVariants.TryGetValue(lowerKey, out var variants))
            return new Dictionary<string, int>();

        return new Dictionary<string, int>(variants);
    }

    /// <summary>
    /// Gets all casing preferences for serialization.
    /// </summary>
    /// <returns>Dictionary mapping lowercase words to their casing variants with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> GetAllPreferences()
    {
        return _casingVariants;
    }

    /// <summary>
    /// Loads casing preferences from a previously serialized state.
    /// Clears existing data before loading.
    /// </summary>
    /// <param name="data">The casing preference data to load.</param>
    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _casingVariants.Clear();
        foreach (var (lowerKey, variants) in data)
        {
            _casingVariants[lowerKey] = new Dictionary<string, int>(variants);
        }
    }
}
