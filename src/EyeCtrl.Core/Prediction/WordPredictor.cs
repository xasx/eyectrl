namespace EyeCtrl.Core.Prediction;

using EyeCtrl.Core.Settings;

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

    // Fuzzy eligibility: a candidate must be at least as long as the prefix, and the prefix must
    // be at least three letters. One adjacent-key slip (0.5) is allowed for 3-5 letters; from
    // six letters a non-adjacent slip (1.0) is tolerated too. The span below is only the
    // confidence normaliser - it does not admit matches.
    private const double FuzzyConfidenceSpan = 1.5;
    private const int MinFuzzyPrefixLength = 3;
    private const int LongPrefixLength = 6;

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
    public List<PredictionResult> GetSuggestions(
        string prefix, string? context = null,
        int maxCount = KeyboardSettings.MaxSuggestionsCeiling, string? fullText = null)
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

                var displayWord = GetDisplayWord(word);
                score = ApplyCaseTierBonus(score, displayWord, prefix);

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

                    score = ApplyCaseTierBonus(score, phraseSuggestion, prefix);

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
            foreach (var (word, frequency) in _words)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var score = frequency * ExactPrefixMultiplier;

                    // Context boost for bigram relevance (using precomputed set)
                    if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                        score *= BigramContextBoost;

                    var displayWord = GetDisplayWord(word);
                    score = ApplyCaseTierBonus(score, displayWord, prefix);

                    // Only preserve an existing entry when it already has an equal or higher score
                    if (!scored.ContainsKey(word) || scored[word].score < score)
                        scored[word] = (score, PredictionType.ExactPrefix, displayWord, null);
                    continue;
                }

                // Skip fuzzy matching for words already in scored (from bigram/phrase)
                if (scored.ContainsKey(word))
                    continue;

                // Category 4: Fuzzy matches (only with a long enough prefix).
                // Note: Fuzzy matching intentionally does NOT apply case tiering because it's
                // designed to find approximate matches based on edit distance, not exact character matching.
                // Case-insensitive comparison is used to maximize match flexibility.
                if (prefix.Length < MinFuzzyPrefixLength)
                    continue;

                // The typed word may only grow: a shorter candidate is not a plausible completion
                // of what the user is typing ("he" for "hel"), however frequent it is.
                if (word.Length < prefix.Length)
                    continue;

                // Cheap pre-filter before the O(m*n) DP (runs per dictionary word per
                // keystroke). Any alignment within the longest allowed distance (1.0) cannot
                // use insert/delete (a pair costs 2.0), so distance >= 0.5 * positional
                // case-insensitive mismatches; 4+ mismatches can never qualify.
                int mismatches = 0;
                for (int i = 0; i < prefix.Length && mismatches <= 3; i++)
                {
                    if (char.ToLowerInvariant(word[i]) != char.ToLowerInvariant(prefix[i]))
                        mismatches++;
                }
                if (mismatches > 3) continue;

                // Only the same-length prefix of the (not shorter) word: one adjacent-key slip
                // for 3-5 letters, one slip of either kind from six letters.
                var wordPrefix = word[..prefix.Length];
                var maxDistance = prefix.Length >= LongPrefixLength ? 1.0 : 0.5;

                var dist = FuzzyEditDistance.Compute(prefix, wordPrefix, _adjacencyMap);
                if (dist > maxDistance) continue;

                double multiplier = dist <= 0.5 ? 1.5 : dist <= 1.0 ? 1.2 : 1.0;
                var fuzzyScore = frequency * multiplier;

                // Context boost for bigram relevance (using precomputed set)
                if (hasContext && contextBoostWords != null && contextBoostWords.Contains(word))
                    fuzzyScore *= BigramContextBoost;

                var fuzzyDisplayWord = GetDisplayWord(word);

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
                        ? Math.Max(0.0, 1.0 - (kv.Value.fuzzyDistance.Value / FuzzyConfidenceSpan))
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

    /// <summary>Returns the user's learned casing for <paramref name="word"/> (Phase 7.3),
    /// or the word unchanged when no preference is recorded.</summary>
    private string GetDisplayWord(string word) =>
        _casingPreferences.HasPreference(word)
            ? _casingPreferences.GetPreferredCasing(word) ?? word
            : word;

    /// <summary>
    /// Phase 7.1 tiered case matching: candidates whose exact case matches the typed
    /// prefix get <see cref="ExactCaseMatchBonus"/>; case-insensitive-only matches get
    /// <see cref="CaseInsensitiveMultiplier"/>. No-op when the prefix is empty.
    /// </summary>
    private static double ApplyCaseTierBonus(double score, string candidate, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return score;
        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            ? score * ExactCaseMatchBonus
            : score * CaseInsensitiveMultiplier;
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

    /// <summary>
    /// Removes a word from the in-memory dictionary — the dictionary-management counterpart of
    /// <see cref="LearnWord(string)"/>. Storage keys are lowercase invariant, so the lookup is
    /// too. Does NOT touch bigrams, phrases, or casing preferences: those decay on their own and
    /// cannot resurrect a word whose dictionary entry is gone. The caller owns persistence —
    /// pair this with <see cref="SaveUserDictionary"/> or the removal returns with the next save.
    /// </summary>
    /// <returns><c>true</c> if the word was present and removed.</returns>
    public bool ForgetWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        return _words.Remove(word.ToLowerInvariant());
    }

    private void LearnWordInternal(string normalizedWord)
    {
        var lower = normalizedWord.ToLowerInvariant();
        if (_words.ContainsKey(lower))
        {
            _words[lower]++;
            return;
        }

        if (_words.Count >= MaxDictionaryWords)
        {
            // Bound the per-keystroke scan (GetSuggestions is O(n) over this dictionary) and
            // the persisted file: an all-day communicator learns novel words forever, an
            // unbounded dictionary turns every keystroke into a slower full scan. Evict the
            // globally least-frequent entry — with 10k-word base dictionaries and this cap,
            // that is a learned word in practice, never head-of-dictionary vocabulary.
            string? leastFrequent = null;
            var minFrequency = int.MaxValue;
            foreach (var kv in _words)
            {
                if (kv.Value < minFrequency)
                {
                    minFrequency = kv.Value;
                    leastFrequent = kv.Key;
                }
            }
            if (leastFrequent is not null)
                _words.Remove(leastFrequent);
        }

        _words[lower] = 1;
    }

    /// <summary>
    /// Captures the full learned state (words, bigrams, phrases, casing — with timestamps) as
    /// immutable copies. Cheap (dictionary copies, no serialization); the background
    /// prediction-stack rebuild snapshots this on the UI thread before a build and diffs it at
    /// swap time. See <see cref="ApplyLearnedDelta"/>.
    /// </summary>
    public PredictionLearnedState CaptureLearnedState() => new()
    {
        Words = WordFrequencies,
        Bigrams = CopyNested(_bigrams.GetAllBigrams()),
        BigramTimestamps = CopyNested(_bigrams.GetAllTimestamps()),
        Phrases = CopyNested(_phrases.GetAllPhrases()),
        PhraseTimestamps = CopyNested(_phrases.GetAllTimestamps()),
        Casing = CopyNested(_casingPreferences.GetAllPreferences()),
    };

    /// <summary>
    /// Applies the learning that happened between <paramref name="before"/> was captured and NOW
    /// — as observed on THIS instance's sibling (the old stack): pass
    /// <c>old.CaptureLearnedState()</c> as <paramref name="after"/> on the NEW instance. Every
    /// positive delta (new entries and frequency increases) is applied additively; nothing is
    /// ever lowered, so a decayed baseline on the new stack is preserved.
    /// </summary>
    public void ApplyLearnedDelta(PredictionLearnedState before, PredictionLearnedState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // Words: additive deltas (the round-1 mechanism, now routed through the same entry point).
        var wordDeltas = new List<KeyValuePair<string, int>>();
        foreach (var (word, freq) in after.Words)
        {
            var delta = freq - before.Words.GetValueOrDefault(word);
            if (delta > 0)
                wordDeltas.Add(new(word, delta));
        }
        LearnWordDeltas(wordDeltas);

        ApplyNestedDelta(before.Bigrams, after.Bigrams, after.BigramTimestamps,
            (a, b, d, ts) => _bigrams.LearnEntry(a, b, d, ts));
        ApplyNestedDelta(before.Phrases, after.Phrases, after.PhraseTimestamps,
            (a, b, d, ts) => _phrases.LearnEntry(a, b, d, ts));

        foreach (var (lower, variants) in after.Casing)
        {
            var beforeVariants = before.Casing.GetValueOrDefault(lower);
            foreach (var (variant, freq) in variants)
            {
                var delta = freq - (beforeVariants?.GetValueOrDefault(variant) ?? 0);
                if (delta > 0)
                    _casingPreferences.LearnEntry(lower, variant, delta);
            }
        }
    }

    private static void ApplyNestedDelta(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> before,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> after,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> afterTimestamps,
        Action<string, string, int, DateTimeOffset> apply)
    {
        foreach (var (outer, entries) in after)
        {
            var beforeEntries = before.GetValueOrDefault(outer);
            var timestamps = afterTimestamps.GetValueOrDefault(outer);
            foreach (var (inner, freq) in entries)
            {
                var delta = freq - (beforeEntries?.GetValueOrDefault(inner) ?? 0);
                if (delta > 0)
                    apply(outer, inner, delta, timestamps?.GetValueOrDefault(inner) ?? DateTimeOffset.UtcNow);
            }
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, TValue>> CopyNested<TValue>(
        Dictionary<string, Dictionary<string, TValue>> source)
    {
        var copy = new Dictionary<string, IReadOnlyDictionary<string, TValue>>(source.Count);
        foreach (var (key, inner) in source)
            copy[key] = new Dictionary<string, TValue>(inner);
        return copy;
    }

    /// <summary>Hard bound on the learned-words dictionary (base dictionaries ship 10,000 words
    /// per language; the headroom above that is for learned vocabulary). Public for tests.</summary>
    public const int MaxDictionaryWords = 50_000;

    /// <summary>
    /// A copy of the current word frequencies (base + learned). The background prediction-stack
    /// rebuild snapshots this before starting a build and diffs it against the state at swap
    /// time, so words learned on the OLD stack while the new one built are carried over.
    /// </summary>
    public IReadOnlyDictionary<string, int> WordFrequencies => new Dictionary<string, int>(_words);

    /// <summary>
    /// Applies positive frequency deltas (word → additional uses). The inverse of the snapshot
    /// diff: each entry adds to whatever the new stack already knows, so a word learned N times
    /// during the build window ends up N higher, never lower, and never duplicated.
    /// </summary>
    public void LearnWordDeltas(IEnumerable<KeyValuePair<string, int>> deltas)
    {
        foreach (var (word, delta) in deltas)
        {
            if (delta <= 0 || string.IsNullOrEmpty(word)) continue;
            var lower = word.ToLowerInvariant();
            _words[lower] = Math.Max(1, _words.GetValueOrDefault(lower) + delta);
        }
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
