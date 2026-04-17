namespace EyeCtrl.Core.Prediction;

/// <summary>
/// N-gram language model for next-word prediction.
/// Stores bigram frequencies: P(word2 | word1).
/// </summary>
public class BigramModel
{
    private readonly Dictionary<string, Dictionary<string, int>> _bigrams = new();
    private const int MinFrequency = 2; // Filter out single occurrences to reduce noise

    /// <summary>
    /// Learns a bigram (word pair) and increments its frequency.
    /// </summary>
    /// <param name="word1">The first word (context).</param>
    /// <param name="word2">The second word (prediction target).</param>
    public void Learn(string word1, string word2)
    {
        if (string.IsNullOrWhiteSpace(word1) || string.IsNullOrWhiteSpace(word2))
            return;

        var key = word1.ToLowerInvariant();
        var value = word2.ToLowerInvariant();

        if (!_bigrams.ContainsKey(key))
            _bigrams[key] = new Dictionary<string, int>();

        var followers = _bigrams[key];
        followers[value] = followers.GetValueOrDefault(value) + 1;
    }

    /// <summary>
    /// Gets the most likely next words given a previous word.
    /// </summary>
    /// <param name="previousWord">The context word.</param>
    /// <param name="maxCount">Maximum number of suggestions to return.</param>
    /// <returns>List of (word, frequency) tuples, ordered by descending frequency.</returns>
    public List<(string word, int frequency)> GetNextWords(string? previousWord, int maxCount)
    {
        if (string.IsNullOrEmpty(previousWord))
            return new List<(string, int)>();

        if (!_bigrams.TryGetValue(previousWord.ToLowerInvariant(), out var followers))
            return new List<(string, int)>();

        return followers
            .Where(kv => kv.Value >= MinFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Gets all stored bigrams for serialization.
    /// </summary>
    /// <returns>Dictionary mapping context words to their followers with frequencies.</returns>
    public Dictionary<string, Dictionary<string, int>> GetAllBigrams()
    {
        return _bigrams;
    }

    /// <summary>
    /// Loads bigrams from a previously serialized state.
    /// Clears existing data before loading.
    /// </summary>
    /// <param name="data">The bigram data to load.</param>
    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data)
    {
        _bigrams.Clear();
        foreach (var (word1, followers) in data)
        {
            _bigrams[word1] = new Dictionary<string, int>(followers);
        }
    }
}
