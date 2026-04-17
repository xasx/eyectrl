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
                // Build prefix and full phrase using index-based iteration to avoid allocations
                var prefix = BuildJoinedString(words, i, len - 1);
                var fullPhrase = BuildJoinedString(words, i, len);

                if (!_phrases.ContainsKey(prefix))
                    _phrases[prefix] = new Dictionary<string, int>();

                _phrases[prefix][fullPhrase] = _phrases[prefix].GetValueOrDefault(fullPhrase) + 1;
            }
        }
    }

    /// <summary>
    /// Builds a space-joined string from a segment of the words array without intermediate allocations.
    /// </summary>
    /// <param name="words">The source word array.</param>
    /// <param name="startIndex">The starting index in the array.</param>
    /// <param name="count">The number of words to join.</param>
    /// <returns>The joined string.</returns>
    private static string BuildJoinedString(string[] words, int startIndex, int count)
    {
        if (count == 0)
            return string.Empty;
        if (count == 1)
            return words[startIndex];

        // Calculate total length to pre-allocate the right size
        int totalLength = words[startIndex].Length;
        for (int i = 1; i < count; i++)
            totalLength += 1 + words[startIndex + i].Length; // +1 for space

        return string.Create(totalLength, (words, startIndex, count), (span, state) =>
        {
            var (w, start, cnt) = state;
            int pos = 0;

            for (int i = 0; i < cnt; i++)
            {
                if (i > 0)
                    span[pos++] = ' ';

                var word = w[start + i];
                word.AsSpan().CopyTo(span[pos..]);
                pos += word.Length;
            }
        });
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
