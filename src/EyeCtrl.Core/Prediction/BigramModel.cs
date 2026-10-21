namespace EyeCtrl.Core.Prediction;

/// <summary>
/// N-gram language model for next-word prediction.
/// Stores bigram frequencies: P(word2 | word1).
/// Phase 8: Supports timestamp tracking for decay mechanism.
/// </summary>
public class BigramModel
{
    // Core bigram data: word1 -> word2 -> frequency
    private readonly Dictionary<string, Dictionary<string, int>> _bigrams = new();

    // Phase 8: Timestamp tracking for decay: word1 -> word2 -> last use timestamp
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _timestamps = new();

    private const int MinFrequency = 2; // Filter out single occurrences to reduce noise

    /// <summary>
    /// Normalizes a word for bigram keys: lowercase, leading/trailing punctuation trimmed, inner
    /// apostrophes kept ("geht's", "don't"). Returns null when nothing word-like remains.
    /// Learning, lookup, the rebuild delta carry and legacy loads all go through this, so a pair
    /// learned from typing and one learned by <c>LearnSequence</c> land on the same key.
    /// </summary>
    public static string? NormalizeWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;

        var start = 0;
        while (start < word.Length && !char.IsLetterOrDigit(word[start])) start++;
        var end = word.Length;
        while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
        if (end <= start) return null;

        return word[start..end].ToLowerInvariant();
    }

    /// <summary>
    /// Learns a bigram (word pair) and increments its frequency.
    /// Phase 8: Updates timestamp to current time.
    /// </summary>
    /// <param name="word1">The first word (context).</param>
    /// <param name="word2">The second word (prediction target).</param>
    public void Learn(string word1, string word2)
    {
        LearnWithTimestamp(word1, word2, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Phase 8: Learns a bigram with a specific timestamp (for testing and migration).
    /// </summary>
    /// <param name="word1">The first word (context).</param>
    /// <param name="word2">The second word (prediction target).</param>
    /// <param name="timestamp">The timestamp to record for this entry.</param>
    internal void LearnWithTimestamp(string word1, string word2, DateTimeOffset timestamp)
    {
        var first = NormalizeWord(word1);
        var second = NormalizeWord(word2);
        if (first is null || second is null)
            return;

        // Update frequency
        if (!_bigrams.ContainsKey(first))
            _bigrams[first] = new Dictionary<string, int>();

        var followers = _bigrams[first];
        followers[second] = followers.GetValueOrDefault(second) + 1;

        // Update timestamp
        if (!_timestamps.ContainsKey(first))
            _timestamps[first] = new Dictionary<string, DateTimeOffset>();

        _timestamps[first][second] = timestamp;
    }

    /// <summary>
    /// Applies a count delta to one bigram entry with an explicit timestamp — the delta-merge
    /// counterpart of the <see cref="GetAllBigrams"/>/<see cref="GetAllTimestamps"/> snapshots.
    /// Used by the background prediction-stack swap to carry learning that happened on the OLD
    /// stack while the new one built.
    /// </summary>
    internal void LearnEntry(string word1, string word2, int countDelta, DateTimeOffset timestamp)
    {
        var first = NormalizeWord(word1);
        var second = NormalizeWord(word2);
        if (countDelta <= 0 || first is null || second is null)
            return;
        if (!_bigrams.ContainsKey(first))
            _bigrams[first] = new Dictionary<string, int>();
        _bigrams[first][second] = _bigrams[first].GetValueOrDefault(second) + countDelta;
        if (!_timestamps.ContainsKey(first))
            _timestamps[first] = new Dictionary<string, DateTimeOffset>();
        _timestamps[first][second] = timestamp;
    }

    /// <summary>
    /// Gets the most likely next words given a previous word.
    /// </summary>
    /// <param name="previousWord">The context word.</param>
    /// <param name="maxCount">Maximum number of suggestions to return.</param>
    /// <returns>List of (word, frequency) tuples, ordered by descending frequency.</returns>
    public List<(string word, int frequency)> GetNextWords(string? previousWord, int maxCount)
    {
        var key = NormalizeWord(previousWord);
        if (key is null)
            return new List<(string, int)>();

        if (!_bigrams.TryGetValue(key, out var followers))
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
    /// Phase 8: Gets all stored timestamps for serialization.
    /// </summary>
    /// <returns>Dictionary mapping context words to their followers' timestamps.</returns>
    public Dictionary<string, Dictionary<string, DateTimeOffset>> GetAllTimestamps()
    {
        return _timestamps;
    }

    /// <summary>
    /// Loads bigrams from a previously serialized state.
    /// Clears existing data before loading.
    /// </summary>
    /// <param name="data">The bigram data to load.</param>
    /// <param name="timestamps">Optional timestamp data. If not provided, defaults to current time.</param>
    public void LoadFromDictionary(Dictionary<string, Dictionary<string, int>> data, Dictionary<string, Dictionary<string, DateTimeOffset>>? timestamps = null)
    {
        _bigrams.Clear();
        foreach (var (word1, followers) in data)
        {
            var first = NormalizeWord(word1);
            if (first is null) continue;

            if (!_bigrams.TryGetValue(first, out var mergedFollowers))
            {
                mergedFollowers = new Dictionary<string, int>();
                _bigrams[first] = mergedFollowers;
            }

            foreach (var (word2, count) in followers)
            {
                var second = NormalizeWord(word2);
                if (second is null || count <= 0) continue;
                mergedFollowers[second] = mergedFollowers.GetValueOrDefault(second) + count;
            }
        }

        // Phase 8: Load timestamps if provided, otherwise default to now. Legacy keys are
        // normalised the same way; colliding entries keep the newest timestamp.
        _timestamps.Clear();
        if (timestamps != null && timestamps.Count > 0)
        {
            foreach (var (word1, word2Timestamps) in timestamps)
            {
                var first = NormalizeWord(word1);
                if (first is null || !_bigrams.TryGetValue(first, out var followers)) continue;

                var mergedTimestamps = _timestamps.TryGetValue(first, out var existingEntry)
                    ? existingEntry
                    : new Dictionary<string, DateTimeOffset>();

                foreach (var (word2, timestamp) in word2Timestamps)
                {
                    var second = NormalizeWord(word2);
                    if (second is null || !followers.ContainsKey(second)) continue;
                    if (!mergedTimestamps.TryGetValue(second, out var existing) || timestamp > existing)
                        mergedTimestamps[second] = timestamp;
                }

                if (mergedTimestamps.Count > 0)
                    _timestamps[first] = mergedTimestamps;
            }
        }

        // Fill in missing timestamps with current time
        foreach (var (word1, followers) in _bigrams)
        {
            if (!_timestamps.ContainsKey(word1))
                _timestamps[word1] = new Dictionary<string, DateTimeOffset>();

            foreach (var word2 in followers.Keys)
            {
                if (!_timestamps[word1].ContainsKey(word2))
                    _timestamps[word1][word2] = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Phase 8: Applies exponential decay to all bigram frequencies based on age.
    /// Removes entries that fall below MinFrequency threshold after decay.
    /// </summary>
    /// <param name="decayEnabled">Whether decay is enabled.</param>
    /// <param name="halfLifeDays">The half-life period in days for exponential decay.</param>
    public void ApplyDecay(bool decayEnabled, int halfLifeDays)
    {
        if (!decayEnabled || halfLifeDays <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var entriesToRemove = new List<(string word1, string word2)>();

        foreach (var (word1, followers) in _bigrams)
        {
            foreach (var (word2, frequency) in followers.ToList())
            {
                // Get timestamp for this entry (default to now if missing)
                var timestamp = _timestamps.TryGetValue(word1, out var word2Timestamps) &&
                                word2Timestamps.TryGetValue(word2, out var ts)
                    ? ts
                    : now;

                // Calculate age in days (clamp to >= 0 to prevent future timestamps from amplifying frequencies)
                var ageInDays = Math.Max(0, (now - timestamp).TotalDays);

                // Apply exponential decay: decayedFrequency = frequency * 0.5^(age / halfLife)
                var decayFactor = Math.Pow(0.5, ageInDays / halfLifeDays);
                var decayedFrequency = frequency * decayFactor;

                // Update frequency or mark for removal (round to avoid float drift pruning recent entries)
                var roundedFrequency = (int)Math.Round(decayedFrequency);
                if (roundedFrequency < MinFrequency)
                {
                    entriesToRemove.Add((word1, word2));
                }
                else
                {
                    followers[word2] = roundedFrequency;
                    // Update timestamp to now to prevent compounding decay on subsequent runs
                    if (word2Timestamps != null)
                        word2Timestamps[word2] = now;
                }
            }
        }

        // Remove decayed entries
        foreach (var (word1, word2) in entriesToRemove)
        {
            // Use TryGetValue to guard against removal after word1 key was already removed
            if (_bigrams.TryGetValue(word1, out var followers))
            {
                followers.Remove(word2);

                // Clean up empty dictionaries
                if (followers.Count == 0)
                {
                    _bigrams.Remove(word1);
                    _timestamps.Remove(word1);
                }
            }

            // Remove timestamp entry if it still exists
            if (_timestamps.TryGetValue(word1, out var timestamps))
            {
                timestamps.Remove(word2);
            }
        }
    }
}
