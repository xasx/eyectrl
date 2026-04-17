namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;
    private readonly BigramModel _bigrams;
    private readonly PhraseModel _phrases;
    private readonly HashSet<string>? _abbreviations;

    private const double MaxFuzzyDistance = 1.5;
    private const double BigramBoost = 2.0;
    private const double PhraseBoost = 1.5;

    /// <summary>
    /// Gets the language-specific abbreviations loaded from the dictionary.
    /// </summary>
    public HashSet<string>? Abbreviations => _abbreviations;

    public WordPredictor(IDictionaryProvider provider, string layoutName = "QWERTY")
    {
        _provider = provider;
        _words = provider.Load();
        _adjacencyMap = KeyboardAdjacencyMap.For(layoutName);
        _bigrams = new BigramModel();
        _phrases = new PhraseModel();

        // Load bigrams and phrases if provider supports extensions
        if (provider is FileDictionaryProvider fileProvider)
        {
            var bigramData = fileProvider.LoadBigrams();
            if (bigramData.Count > 0)
                _bigrams.LoadFromDictionary(bigramData);

            var phraseData = fileProvider.LoadPhrases();
            if (phraseData.Count > 0)
                _phrases.LoadFromDictionary(phraseData);

            // Load abbreviations
            _abbreviations = fileProvider.Abbreviations.Count > 0 ? fileProvider.Abbreviations : null;
        }
    }

    public List<PredictionResult> GetSuggestions(string prefix, string? previousWord, int maxCount = 5)
    {
        var scored = new Dictionary<string, (double score, PredictionType type)>();
        var hasContext = !string.IsNullOrWhiteSpace(previousWord);

        // If we have context (previous word), check for bigram and phrase matches
        if (hasContext)
        {
            // Check bigram predictions (case-sensitive)
            var nextWords = _bigrams.GetNextWords(previousWord!, maxCount * 2);
            foreach (var (word, frequency) in nextWords)
            {
                if (string.IsNullOrEmpty(prefix) || word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var baseScore = frequency * BigramBoost;
                    scored[word] = (baseScore, PredictionType.NextWord);
                }
            }

            // Check phrase predictions (case-sensitive)
            var phrases = _phrases.GetPhrases(previousWord!, maxCount * 2);
            foreach (var (phrase, frequency) in phrases)
            {
                // Extract the next word from the phrase
                var phraseWords = phrase.Split(' ');
                if (phraseWords.Length > 1)
                {
                    var nextWord = phraseWords[^1];
                    if (string.IsNullOrEmpty(prefix) || nextWord.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var baseScore = frequency * PhraseBoost;
                        if (!scored.ContainsKey(nextWord) || scored[nextWord].score < baseScore)
                            scored[nextWord] = (baseScore, PredictionType.Phrase);
                    }
                }
            }
        }

        // Standard word matching (prefix + fuzzy)
        foreach (var (word, frequency) in _words)
        {
            if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var baseScore = frequency * 3.0;
                // Only add if not already scored higher by bigram/phrase (preserve context predictions)
                if (!scored.ContainsKey(word))
                    scored[word] = (baseScore, PredictionType.ExactPrefix);
                continue;
            }

            // Skip fuzzy matching for words already in scored (from bigram/phrase)
            if (scored.ContainsKey(word))
                continue;

            // Compare prefix against the same-length prefix of the word, or full word if shorter
            // Use case-insensitive comparison for fuzzy matching
            var wordPrefix = word.Length >= prefix.Length
                ? word[..prefix.Length]
                : word;

            var dist = FuzzyEditDistance.Compute(prefix.ToLowerInvariant(), wordPrefix.ToLowerInvariant(), _adjacencyMap);
            if (dist > MaxFuzzyDistance) continue;

            double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
            var fuzzyScore = frequency * multiplier;
            if (!scored.ContainsKey(word))
                scored[word] = (fuzzyScore, PredictionType.FuzzyMatch);
        }

        // Convert to PredictionResult and sort
        var results = scored
            .Select(kv =>
            {
                var confidence = Math.Min(1.0, kv.Value.score / 1000.0);
                return new PredictionResult(kv.Key, kv.Value.score, kv.Value.type, confidence);
            })
            .OrderByDescending(r => r.Score)
            .Take(maxCount)
            .ToList();

        return results;
    }

    public void LearnWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    public void LearnSequence(string[] words)
    {
        // Learn individual words
        foreach (var word in words)
            LearnWord(word);

        // Learn bigrams
        for (int i = 0; i < words.Length - 1; i++)
            _bigrams.Learn(words[i], words[i + 1]);

        // Learn phrases (2-7 word sequences)
        if (words.Length >= 2)
            _phrases.LearnPhrase(words);
    }

    public void SaveUserDictionary()
    {
        _provider.Save(_words);

        // Save bigrams and phrases if provider supports extensions
        if (_provider is FileDictionaryProvider fileProvider)
        {
            fileProvider.SaveBigrams(_bigrams.GetAllBigrams());
            fileProvider.SavePhrases(_phrases.GetAllPhrases());
        }
    }
}
