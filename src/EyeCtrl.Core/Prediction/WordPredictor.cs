namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Primary word prediction engine combining dictionary lookups, bigram context, phrase matching,
/// fuzzy edit distance, casing preferences, and smart capitalization.
/// </summary>
public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<char, HashSet<char>> _adjacencyMap;
    private readonly BigramModel _bigrams;
    private readonly PhraseModel _phrases;
    private readonly IReadOnlySet<string> _abbreviations;
    private readonly CasingPreferenceTracker _casingPreferences;

    private const double MaxFuzzyDistance = 1.5;

    // Scoring weights (Phase 2)
    private const double ExactPrefixMultiplier = 3.0;
    private const double NextWordMultiplier = 5.0;
    private const double PhraseMultiplier = 4.0;
    private const double BigramContextBoost = 1.3;

    // Phase 7: Case-sensitivity scoring weights
    private const double ExactCaseMatchBonus = 1.2;      // Tier 1: Exact case match (highest priority)
    private const double CaseInsensitiveMultiplier = 0.8; // Tier 2: Case-insensitive match (lower priority)

    /// <summary>
    /// Gets the language-specific abbreviations loaded from the dictionary.
    /// </summary>
    public IReadOnlySet<string> Abbreviations => _abbreviations;

    /// <summary>
    /// Initializes a new instance of the <see cref="WordPredictor"/> class.
    /// Loads the word dictionary, keyboard adjacency map, bigrams, phrases, and casing preferences.
    /// </summary>
    /// <param name="provider">Dictionary provider for loading and saving word data.</param>
    /// <param name="layoutName">Keyboard layout name for fuzzy matching adjacency (default: QWERTY).</param>
    public WordPredictor(IDictionaryProvider provider, string layoutName = "QWERTY")
    {
        _provider = provider;
        _words = provider.Load();
        _adjacencyMap = KeyboardAdjacencyMap.For(layoutName);
        _bigrams = new BigramModel();
        _phrases = new PhraseModel();
        _casingPreferences = new CasingPreferenceTracker();

        // Load bigrams and phrases if provider supports extensions
        if (provider is FileDictionaryProvider fileProvider)
        {
            var bigramData = fileProvider.LoadBigrams();
            var bigramTimestamps = fileProvider.LoadBigramTimestamps();
            if (bigramData.Count > 0)
                _bigrams.LoadFromDictionary(bigramData, bigramTimestamps);

            var phraseData = fileProvider.LoadPhrases();
            var phraseTimestamps = fileProvider.LoadPhraseTimestamps();
            if (phraseData.Count > 0)
                _phrases.LoadFromDictionary(phraseData, phraseTimestamps);

            // Load casing preferences (Phase 7.3)
            var casingData = fileProvider.LoadCasingPreferences();
            if (casingData.Count > 0)
                _casingPreferences.LoadFromDictionary(casingData);

            // Load abbreviations (always non-null, empty set if none provided)
            _abbreviations = fileProvider.Abbreviations;
        }
        else
        {
            // Default to empty set for non-FileDictionaryProvider
            _abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns ranked word predictions based on the typed prefix and optional context.
    /// Combines exact prefix matches, next-word predictions, phrase completions,
    /// and fuzzy (typo-tolerant) matches with scoring weights.
    /// </summary>
    /// <param name="prefix">The current word prefix being typed.</param>
    /// <param name="context">The previous word for bigram context, or null if at start of sentence.</param>
    /// <param name="maxCount">Maximum number of suggestions to return.</param>
    /// <param name="fullText">Full text typed so far for smart capitalization decisions.</param>
    /// <returns>A list of <see cref="PredictionResult"/> ranked by score descending.</returns>
    public List<PredictionResult> GetSuggestions(string prefix, string? context = null, int maxCount = 5, string? fullText = null)
    {
        // Use case-insensitive dictionary to prevent duplicate suggestions with different casing
        var scored = new Dictionary<string, (double score, PredictionType type, string displayWord, double? fuzzyDistance)>(StringComparer.OrdinalIgnoreCase);
        var hasContext = !string.IsNullOrWhiteSpace(context);

        // Determine if we should apply smart capitalization
        var shouldCapitalize = fullText != null && CapitalizationHelper.ShouldCapitalize(fullText, _abbreviations);

        // Precompute context boost set once (performance optimization)
        HashSet<string>? contextBoostWords = null;
        List<(string word, int frequency)>? nextWords = null;
        if (hasContext)
        {
            // Call GetNextWords once with the max needed count and reuse for both predictions and boost set
            nextWords = _bigrams.GetNextWords(context!, Math.Max(100, maxCount * 2));
            contextBoostWords = new HashSet<string>(
                nextWords.Select(nw => nw.word.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        // Category 1: Next-word predictions (when we have context)
        if (hasContext && nextWords != null)
        {
            // Phase 7.1: Tiered matching for bigram predictions
            // Tier 1: Exact case match (higher priority)
            // Tier 2: Case-insensitive match (lower priority)
            foreach (var (word, frequency) in nextWords)
            {
                bool matchesPrefix = string.IsNullOrEmpty(prefix) || word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if (!matchesPrefix)
                    continue;

                var baseFreq = _words.GetValueOrDefault(word.ToLowerInvariant(), 1);
                var score = baseFreq * frequency * NextWordMultiplier;

                var key = word.ToLowerInvariant();

                // Phase 7.3: Apply user casing preference if available
                var displayWord = _casingPreferences.HasPreference(word)
                    ? _casingPreferences.GetPreferredCasing(word) ?? word
                    : word;

                // Apply tiered case matching bonus (only when prefix is non-empty)
                // Use displayWord after applying casing preference for accurate case comparison
                if (!string.IsNullOrEmpty(prefix))
                {
                    bool exactCaseMatch = displayWord.StartsWith(prefix, StringComparison.Ordinal);
                    if (exactCaseMatch)
                        score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                    else
                        score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                }

                // Keep higher-scored version for display
                if (!scored.ContainsKey(key) || scored[key].score < score)
                    scored[key] = (score, PredictionType.NextWord, displayWord, null);
            }

            // Category 2: Phrase predictions with tiered case matching
            var phrases = _phrases.GetPhrases(context!, maxCount * 2);
            foreach (var (phrase, frequency) in phrases)
            {
                // Return the remaining phrase after the current context
                // so Phrase results are distinguishable from single-word NextWord predictions
                var phrasePrefix = context! + " ";
                var phraseSuggestion = phrase;

                if (phrase.StartsWith(phrasePrefix, StringComparison.Ordinal))
                    phraseSuggestion = phrase[phrasePrefix.Length..].TrimStart();

                bool matchesPrefix = string.IsNullOrWhiteSpace(phraseSuggestion) == false &&
                    (string.IsNullOrEmpty(prefix) || phraseSuggestion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (matchesPrefix)
                {
                    var score = frequency * PhraseMultiplier;

                    // Apply tiered case matching bonus (only when prefix is non-empty)
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        bool exactCaseMatch = phraseSuggestion.StartsWith(prefix, StringComparison.Ordinal);
                        if (exactCaseMatch)
                            score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                        else
                            score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                    }

                    var key = phraseSuggestion.ToLowerInvariant();

                    // Only update if not already present or if we have a higher score
                    if (!scored.ContainsKey(key) || scored[key].score < score)
                        scored[key] = (score, PredictionType.Phrase, phraseSuggestion, null);
                }
            }
        }

        // Category 3: Exact prefix matches
        // Skip if prefix is empty AND we have context (context predictions take priority)
        var shouldIncludeStandardMatching = !string.IsNullOrEmpty(prefix) || !hasContext;

        if (shouldIncludeStandardMatching)
        {
            // Precompute lowercase prefix once for fuzzy matching (performance optimization)
            var lowerPrefix = prefix.ToLowerInvariant();

            foreach (var (word, frequency) in _words)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var score = frequency * ExactPrefixMultiplier;

                    // Context boost for bigram relevance (using precomputed set)
                    if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                        score *= BigramContextBoost;

                    // Phase 7.3: Apply user casing preference if available
                    var displayWord = _casingPreferences.HasPreference(word)
                        ? _casingPreferences.GetPreferredCasing(word) ?? word
                        : word;

                    // Phase 7.1: Apply tiered case matching bonus (only when prefix is non-empty)
                    // Use displayWord for case comparison after applying casing preference
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        bool exactCaseMatch = displayWord.StartsWith(prefix, StringComparison.Ordinal);
                        if (exactCaseMatch)
                            score *= ExactCaseMatchBonus;  // Tier 1: Exact case match
                        else
                            score *= CaseInsensitiveMultiplier; // Tier 2: Case-insensitive match
                    }

                    // Only preserve an existing entry when it already has an equal or higher score
                    if (!scored.ContainsKey(word) || scored[word].score < score)
                        scored[word] = (score, PredictionType.ExactPrefix, displayWord, null);
                    continue;
                }

                // Skip fuzzy matching for words already in scored (from bigram/phrase)
                if (scored.ContainsKey(word))
                    continue;

                // Category 4: Fuzzy matches (only if we have a non-empty prefix)
                // Note: Fuzzy matching intentionally does NOT apply case tiering because it's
                // designed to find approximate matches based on edit distance, not exact character matching.
                // Case-insensitive comparison is used to maximize match flexibility.
                if (string.IsNullOrEmpty(prefix))
                    continue;

                // Compare prefix against the same-length prefix of the word, or full word if shorter
                // Use case-insensitive comparison for fuzzy matching
                var wordPrefix = word.Length >= prefix.Length
                    ? word[..prefix.Length]
                    : word;

                var dist = FuzzyEditDistance.Compute(lowerPrefix, wordPrefix.ToLowerInvariant(), _adjacencyMap);
                if (dist > MaxFuzzyDistance) continue;

                double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
                var fuzzyScore = frequency * multiplier;

                // Context boost for bigram relevance (using precomputed set)
                if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                    fuzzyScore *= BigramContextBoost;

                // Phase 7.3: Apply user casing preference if available
                var fuzzyDisplayWord = _casingPreferences.HasPreference(word)
                    ? _casingPreferences.GetPreferredCasing(word) ?? word
                    : word;

                if (!scored.ContainsKey(word))
                    scored[word] = (fuzzyScore, PredictionType.FuzzyMatch, fuzzyDisplayWord, dist);
            }
        }

        // Convert to PredictionResult and sort
        var results = scored
            .Select(kv =>
            {
                // Apply smart capitalization if enabled (using precomputed decision for performance)
                var displayWord = shouldCapitalize
                    ? CapitalizationHelper.ApplyCapitalization(kv.Value.displayWord, shouldCapitalize)
                    : kv.Value.displayWord;

                // Improved confidence calculation based on prediction type
                double confidence = kv.Value.type switch
                {
                    PredictionType.NextWord => Math.Min(1.0, kv.Value.score / 500.0),
                    PredictionType.Phrase => Math.Min(1.0, kv.Value.score / 400.0),
                    PredictionType.ExactPrefix => 1.0, // Exact matches have high confidence
                    PredictionType.FuzzyMatch => kv.Value.fuzzyDistance.HasValue
                        ? Math.Max(0.0, 1.0 - (kv.Value.fuzzyDistance.Value / MaxFuzzyDistance))
                        : 0.5,
                    _ => Math.Min(1.0, kv.Value.score / 1000.0)
                };
                return new PredictionResult(displayWord, kv.Value.score, kv.Value.type, confidence);
            })
            .OrderByDescending(r => r.Score)
            .Take(maxCount)
            .ToList();

        return results;
    }

    /// <summary>Learns a single word, normalizing ALL-CAPS and recording casing preferences.</summary>
    /// <param name="word">The word to learn in its typed casing.</param>
    public void LearnWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        // Phase 7.2: Normalize ALL-CAPS words before learning
        var normalizedWord = CasingHelper.NormalizeAllCaps(word)!;

        // Phase 7.3: Record casing preference for the normalized word
        _casingPreferences.RecordCasing(normalizedWord);

        // Learn the normalized word
        LearnWordInternal(normalizedWord);
    }

    private void LearnWordInternal(string normalizedWord)
    {
        var lower = normalizedWord.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    /// <summary>Learns a sequence of words for bigram and phrase prediction.</summary>
    /// <param name="words">The words in order as typed by the user.</param>
    public void LearnSequence(string[] words)
    {
        // Normalize ALL-CAPS words before learning (Phase 7.2)
        var normalizedWords = new string[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            normalizedWords[i] = CasingHelper.NormalizeAllCaps(words[i]) ?? words[i];
        }

        // Record casing preferences and learn individual words (Phase 7.3)
        foreach (var word in normalizedWords)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                _casingPreferences.RecordCasing(word);
                LearnWordInternal(word);
            }
        }

        // Learn bigrams (use normalized words)
        for (int i = 0; i < normalizedWords.Length - 1; i++)
            _bigrams.Learn(normalizedWords[i], normalizedWords[i + 1]);

        // Learn phrases (2-7 word sequences)
        if (normalizedWords.Length >= 2)
            _phrases.LearnPhrase(normalizedWords);
    }

    /// <summary>Persists the learned word dictionary, bigrams, phrases, and casing preferences to disk.</summary>
    public void SaveUserDictionary()
    {
        _provider.Save(_words);

        // Save bigrams, phrases, and casing preferences if provider supports extensions
        if (_provider is FileDictionaryProvider fileProvider)
        {
            fileProvider.SaveBigrams(_bigrams.GetAllBigrams());
            fileProvider.SaveBigramTimestamps(_bigrams.GetAllTimestamps());
            fileProvider.SavePhrases(_phrases.GetAllPhrases());
            fileProvider.SavePhraseTimestamps(_phrases.GetAllTimestamps());
            fileProvider.SaveCasingPreferences(_casingPreferences.GetAllPreferences()); // Phase 7.3
        }
    }

    /// <summary>
    /// Phase 8: Applies exponential decay to bigrams and phrases based on age.
    /// Should be called by the app layer after loading with user-configured settings.
    /// Typically invoked during initialization to clean up old patterns.
    /// </summary>
    /// <param name="decayEnabled">Whether decay is enabled.</param>
    /// <param name="halfLifeDays">The half-life period in days for exponential decay.</param>
    public void ApplyDecay(bool decayEnabled, int halfLifeDays)
    {
        _bigrams.ApplyDecay(decayEnabled, halfLifeDays);
        _phrases.ApplyDecay(decayEnabled, halfLifeDays);
    }
}
