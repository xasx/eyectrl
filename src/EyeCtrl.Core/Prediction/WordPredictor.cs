namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;

    private const double MaxFuzzyDistance = 1.5;

    public WordPredictor(IDictionaryProvider provider, string layoutName = "QWERTY")
    {
        _provider = provider;
        _words = provider.Load();
        _adjacencyMap = KeyboardAdjacencyMap.For(layoutName);
    }

    public List<string> GetSuggestions(string prefix, int maxCount = 5)
    {
        var lowerPrefix = prefix.ToLowerInvariant();
        var scored = new Dictionary<string, double>();

        foreach (var (word, frequency) in _words)
        {
            if (word.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                scored[word] = frequency * 3.0;
                continue;
            }

            // Compare prefix against the same-length prefix of the word, or full word if shorter
            var compareTarget = word.Length >= lowerPrefix.Length
                ? word[..lowerPrefix.Length]
                : word;

            var dist = FuzzyEditDistance.Compute(lowerPrefix, compareTarget, _adjacencyMap);
            if (dist > MaxFuzzyDistance) continue;

            double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
            scored[word] = frequency * multiplier;
        }

        return scored
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => kv.Key)
            .ToList();
    }

    public void LearnWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    public void SaveUserDictionary()
    {
        _provider.Save(_words);
    }
}
