namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;
    private readonly BigramModel _bigrams;
    private readonly PhraseModel _phrases;
    private readonly IReadOnlySet<string> _abbreviations;

    private const double MaxFuzzyDistance = 1.5;

    // Scoring weights (Phase 2)
    private const double ExactPrefixMultiplier = 3.0;
    private const double NextWordMultiplier = 5.0;
    private const double PhraseMultiplier = 4.0;
    private const double BigramContextBoost = 1.3;

    /// <summary>
    /// Gets the language-specific abbreviations loaded from the dictionary.
    /// </summary>
    public IReadOnlySet<string> Abbreviations => _abbreviations;

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

            // Load abbreviations (always non-null, empty set if none provided)
            _abbreviations = fileProvider.Abbreviations;
        }
        else
        {
            // Default to empty set for non-FileDictionaryProvider
            _abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public List<PredictionResult> GetSuggestions(string prefix, string? context = null, int maxCount = 5)
    {
        var scored = new Dictionary<string, (double score, PredictionType type)>();
        var hasContext = !string.IsNullOrWhiteSpace(context);

        // Category 1: Next-word predictions (when we have context)
        if (hasContext)
        {
            // Check bigram predictions (case-sensitive)
            var nextWords = _bigrams.GetNextWords(context!, maxCount * 2);
            foreach (var (word, frequency) in nextWords)
            {
                if (string.IsNullOrEmpty(prefix) || word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var baseFreq = _words.GetValueOrDefault(word.ToLowerInvariant(), 1);
                    var score = baseFreq * frequency * NextWordMultiplier;
                    scored[word] = (score, PredictionType.NextWord);
                }
            }

            // Category 2: Phrase predictions (case-sensitive)
            var phrases = _phrases.GetPhrases(context!, maxCount * 2);
            foreach (var (phrase, frequency) in phrases)
            {
                // Return the remaining phrase after the current context
                // so Phrase results are distinguishable from single-word NextWord predictions
                var phrasePrefix = context! + " ";
                var phraseSuggestion = phrase;

                if (phrase.StartsWith(phrasePrefix, StringComparison.Ordinal))
                    phraseSuggestion = phrase[phrasePrefix.Length..].TrimStart();

                if (!string.IsNullOrWhiteSpace(phraseSuggestion) &&
                    (string.IsNullOrEmpty(prefix) || phraseSuggestion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var score = frequency * PhraseMultiplier;
                    // Only update if not already present or if we have a higher score
                    if (!scored.ContainsKey(phraseSuggestion) || scored[phraseSuggestion].score < score)
                        scored[phraseSuggestion] = (score, PredictionType.Phrase);
                }
            }
        }

        // Category 3: Exact prefix matches
        // Skip if prefix is empty AND we have context (context predictions take priority)
        var shouldIncludeStandardMatching = !string.IsNullOrEmpty(prefix) || !hasContext;

        if (shouldIncludeStandardMatching)
        {
            foreach (var (word, frequency) in _words)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var score = frequency * ExactPrefixMultiplier;

                    // Context boost for bigram relevance
                    if (hasContext)
                    {
                        var nextWords = _bigrams.GetNextWords(context!, 100);
                        if (nextWords.Any(nw => string.Equals(nw.word, word, StringComparison.OrdinalIgnoreCase)))
                            score *= BigramContextBoost;
                    }

                    // Only preserve an existing entry when it already has an equal or higher score
                    if (!scored.ContainsKey(word) || scored[word].score < score)
                        scored[word] = (score, PredictionType.ExactPrefix);
                    continue;
                }

                // Skip fuzzy matching for words already in scored (from bigram/phrase)
                if (scored.ContainsKey(word))
                    continue;

                // Category 4: Fuzzy matches (only if we have a non-empty prefix)
                if (string.IsNullOrEmpty(prefix))
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

                // Context boost for bigram relevance
                if (hasContext)
                {
                    var nextWords = _bigrams.GetNextWords(context!, 100);
                    if (nextWords.Any(nw => string.Equals(nw.word, word, StringComparison.OrdinalIgnoreCase)))
                        fuzzyScore *= BigramContextBoost;
                }

                if (!scored.ContainsKey(word))
                    scored[word] = (fuzzyScore, PredictionType.FuzzyMatch);
            }
        }

        // Convert to PredictionResult and sort
        var results = scored
            .Select(kv =>
            {
                // Improved confidence calculation based on prediction type
                double confidence = kv.Value.type switch
                {
                    PredictionType.NextWord => Math.Min(1.0, kv.Value.score / 500.0),
                    PredictionType.Phrase => Math.Min(1.0, kv.Value.score / 400.0),
                    PredictionType.ExactPrefix => 1.0, // Exact matches have high confidence
                    PredictionType.FuzzyMatch => Math.Max(0.0, 1.0 - (MaxFuzzyDistance / 2.0)),
                    _ => Math.Min(1.0, kv.Value.score / 1000.0)
                };
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
