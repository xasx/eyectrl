namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;

    public WordPredictor(IDictionaryProvider provider)
    {
        _provider = provider;
        _words = provider.Load();
    }

    public List<string> GetSuggestions(string prefix, int maxCount = 5)
    {
        var lowerPrefix = prefix.ToLowerInvariant();

        return _words
            .Where(kv => kv.Key.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
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
