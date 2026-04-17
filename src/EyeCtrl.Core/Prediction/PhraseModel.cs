namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Tracks multi-word phrase patterns (2-7 words).
/// Used for suggesting common phrases like "looking forward to".
/// </summary>
public class PhraseModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _phrases = new();
    private const int MinPhraseLength = 2;
    private const int MaxPhraseLength = 7;
    private const int MinFrequency = 2;

    /// <summary>
    /// Learns a phrase by extracting all sub-sequences of length 2-7.
    /// </summary>
    /// <param name="words">Array of words forming the phrase.</param>
    public void LearnPhrase(string[] words)
    {
        if (words.Length < MinPhraseLength)
            return;

        // Extract all sub-phrases of length 2 to MaxPhraseLength
        for (int len = MinPhraseLength; len <= Math.Min(MaxPhraseLength, words.Length); len++)
        {
            for (int i = 0; i <= words.Length - len; i++)
            {
                var phraseWords = words.Skip(i).Take(len).ToArray();

                // Prefix is all words except the last
                var prefix = string.Join(" ", phraseWords.Take(len - 1));

                // Full phrase includes all words
                var fullPhrase = string.Join(" ", phraseWords);

                if (!_phrases.ContainsKey(prefix))
                    _phrases[prefix] = new Dictionary<string, int>();

                _phrases[prefix][fullPhrase] = _phrases[prefix].GetValueOrDefault(fullPhrase) + 1;
            }
        }
    }

    /// <summary>
    /// Gets phrases that start with the given prefix.
    /// </summary>
    /// <param name="prefix">The prefix to match (can be one or more words).</param>
    /// <param name="maxCount">Maximum number of suggestions.</param>
    /// <returns>List of (phrase, frequency) tuples, ordered by descending frequency.</returns>
    public List<(string phrase, int frequency)> GetPhrases(string prefix, int maxCount)
    {
        if (string.IsNullOrEmpty(prefix))
            return new List<(string, int)>();

        if (!_phrases.TryGetValue(prefix, out var matches))
            return new List<(string, int)>();

        return matches
            .Where(kv => kv.Value >= MinFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Gets all stored phrases for serialization.
    /// </summary>
    /// <returns>Dictionary mapping prefixes to their continuations with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> GetAllPhrases()
    {
        return _phrases;
    }

    /// <summary>
    /// Loads phrases from a previously serialized state.
    /// Clears existing data before loading.
    /// </summary>
    /// <param name="data">The phrase data to load.</param>
    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _phrases.Clear();
        foreach (var (prefix, continuations) in data)
        {
            _phrases[prefix] = new Dictionary<string, int>(continuations);
        }
    }
}
