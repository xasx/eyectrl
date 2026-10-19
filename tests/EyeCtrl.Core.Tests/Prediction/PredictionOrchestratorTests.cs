namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class PredictionOrchestratorTests
{
    private class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static PredictionOrchestrator CreateOrchestrator(
        Dictionary<string, int>? words = null,
        SentenceCompletionIndex? trie = null)
    {
        var provider = new InMemoryDictProvider(words ?? new Dictionary<string, int> { ["hello"] = 100 });
        var predictor = new WordPredictor(provider, "QWERTY");
        return new PredictionOrchestrator(predictor, trie, lmPredictor: null);
    }

    [Fact]
    public void GetSuggestions_ExactPrefixMatch_ReturnsSuggestion()
    {
        var orchestrator = CreateOrchestrator(words: new Dictionary<string, int> { ["hello"] = 100, ["help"] = 80 });
        var results = orchestrator.GetSuggestions("hel", context: null, maxCount: 5);
        Check.That(results).Not.IsEmpty();
        Check.That(results).HasElementThatMatches(r => r.Word == "hello");
        Check.That(results).HasElementThatMatches(r => r.Type == PredictionType.ExactPrefix);
    }

    [Fact]
    public void GetSuggestions_WithTrie_ReturnsSentenceCompletions()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("thank you very much", 10);
        trie.Insert("thank you for your help", 7);

        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int>
            {
                ["thank"] = 50, ["you"] = 40, ["very"] = 30, ["much"] = 30,
                ["for"] = 30, ["your"] = 30, ["help"] = 30,
            },
            trie: trie);

        var results = orchestrator.GetSuggestions("thank", context: null, maxCount: 5);

        // The suggestion carries the typed prefix, not the bare trie remainder ("you very much"):
        // accepting one deletes the typed word first, so a prefix-less suggestion would drop it.
        Check.That(results).HasElementThatMatches(r =>
            r.Type == PredictionType.SentenceCompletion && r.Word == "thank you very much");
    }

    [Fact]
    public void GetSuggestions_MergePriority_NextWordFirst()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("hello world today", 100);
        var words = new Dictionary<string, int> { ["hello"] = 50, ["world"] = 40, ["today"] = 30 };
        var orchestrator = CreateOrchestrator(words, trie);
        var results = orchestrator.GetSuggestions("wo", context: "hello", maxCount: 5);
        var types = results.Select(r => r.Type).Distinct().ToList();
        Check.That(types).Not.IsEmpty();
    }

    [Fact]
    public void GetSuggestions_EmptyInput_ReturnsEmpty()
    {
        var orchestrator = CreateOrchestrator();
        var results = orchestrator.GetSuggestions("", context: null, maxCount: 5);
        Check.That(results).IsEmpty();
    }

    [Fact]
    public void GetSuggestions_DeduplicatesResults()
    {
        var words = new Dictionary<string, int> { ["hello"] = 100 };
        var trie = new SentenceCompletionIndex();
        trie.Insert("hello world", 50);
        var orchestrator = CreateOrchestrator(words, trie);
        var results = orchestrator.GetSuggestions("hello", context: null, maxCount: 5);
        var helloResults = results.Where(r => r.Word.Equals("hello", StringComparison.OrdinalIgnoreCase)).ToList();
        Check.That(helloResults.Count <= 1).IsTrue();
    }

    [Fact]
    public void GetSuggestions_HighScoreFuzzy_LandsAfterEveryPrefixMatch()
    {
        // "hwl" is an adjacent-key slip from "hel" (0.5) with a million-fold frequency, so it
        // wins on score alone. The owner sees the typed letters respected first: every prefix
        // match outranks every non-match, regardless of score.
        var orchestrator = CreateOrchestrator(words: new Dictionary<string, int>
        {
            ["help"] = 100, ["hwl"] = 1_000_000,
        });

        var results = orchestrator.GetSuggestions("hel", context: null, maxCount: 5);

        Check.That(results).Not.IsEmpty();
        Check.That(results[0].Word).IsEqualTo("help");
        Check.That(results).HasElementThatMatches(r => r.Word == "hwl");
        Check.That(results.Last().Word).IsEqualTo("hwl");
    }

    [Fact]
    public void GetSuggestions_FuzzyFlood_RealMatchesFillTheBar()
    {
        // Six near-misses are 0.5 slips from "Wet" and a million times more frequent than the
        // real matches. The bar must be the five prefix matches, not the flood.
        var orchestrator = CreateOrchestrator(words: new Dictionary<string, int>
        {
            ["Wetter"] = 100, ["Wettkampf"] = 90, ["Wettbewerb"] = 80, ["Wette"] = 70, ["Wetten"] = 60,
            ["qet"] = 1_000_000_000, ["eet"] = 1_000_000_000, ["Wrt"] = 1_000_000_000,
            ["Wwt"] = 1_000_000_000, ["Wer"] = 1_000_000_000, ["Wey"] = 1_000_000_000,
        });

        var results = orchestrator.GetSuggestions("Wet", context: null, maxCount: 5);

        Check.That(results.Select(r => r.Word))
            .ContainsExactly("Wetter", "Wettkampf", "Wettbewerb", "Wette", "Wetten");
    }

    [Fact]
    public void GetSuggestions_LowercasePrefix_RanksTheCapitalisedMatchFirst()
    {
        // German nouns are capitalised while the user types lowercase; the ranking is
        // case-insensitive.
        var orchestrator = CreateOrchestrator(words: new Dictionary<string, int>
        {
            ["Wetter"] = 100, ["werden"] = 1_000_000,
        });

        var results = orchestrator.GetSuggestions("wet", context: null, maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void Rank_LowercasePrefix_CapitalisedMatchOutranksAHighScoreNonMatch()
    {
        var results = PredictionOrchestrator.Rank(
            [
                new PredictionResult("werden", 1_000_000, PredictionType.FuzzyMatch, 0.5),
                new PredictionResult("Wetter", 100, PredictionType.ExactPrefix, 1.0),
            ],
            prefix: "wet", maxCount: 5);

        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_WithinThePrefixGroup_HigherScoreFirst()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("Wet weather is nice", 1_000_000);
        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int>
            {
                ["Wetter"] = 1, ["weather"] = 1, ["is"] = 1, ["nice"] = 1,
            },
            trie: trie);

        var results = orchestrator.GetSuggestions("Wet", context: null, maxCount: 5);

        // "Wetter" scores 3; the trie completion scores ln(1e6)*3 and is appended after it.
        Check.That(results[0].Word).IsEqualTo("Wet weather is nice");
        Check.That(results[1].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void GetSuggestions_TrieCompletionWithAnUnknownWord_IsDropped()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("ist tom taub?", 10);
        trie.Insert("ist das gut?", 5);
        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int> { ["ist"] = 100, ["das"] = 100, ["gut"] = 100 },
            trie: trie);

        var words = orchestrator.GetSuggestions("ist", context: null, maxCount: 5)
            .Select(result => result.Word).ToList();

        Check.That(words).Contains("ist das gut?");
        Check.That(words).Not.Contains("ist tom taub?");
    }

    [Fact]
    public void GetSuggestions_TrieCompletion_TrimsBoundaryPunctuationAndKeepsApostrophes()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("ist geht's gut?", 10);
        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int> { ["ist"] = 100, ["geht's"] = 100, ["gut"] = 100 },
            trie: trie);

        var words = orchestrator.GetSuggestions("ist", context: null, maxCount: 5)
            .Select(result => result.Word).ToList();

        Check.That(words).Contains("ist geht's gut?");
    }

    [Fact]
    public void GetSuggestions_TrieCompletion_WithADigitToken_IsDropped()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("um 5 uhr", 10);
        // "5" is in the dictionary on purpose: only the explicit digit rule can drop it.
        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int> { ["um"] = 100, ["uhr"] = 100, ["5"] = 100 },
            trie: trie);

        var words = orchestrator.GetSuggestions("um", context: null, maxCount: 5)
            .Select(result => result.Word).ToList();

        Check.That(words).Not.Contains("um 5 uhr");
    }

    [Fact]
    public void GetSuggestions_LearnedWord_MakesACompletionValid()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("ist tom taub?", 10);
        var orchestrator = CreateOrchestrator(
            words: new Dictionary<string, int> { ["ist"] = 100, ["taub"] = 100 },
            trie: trie);

        Check.That(orchestrator.GetSuggestions("ist", context: null, maxCount: 5)
            .Select(result => result.Word)).Not.Contains("ist tom taub?");

        orchestrator.LearnWord("tom");

        Check.That(orchestrator.GetSuggestions("ist", context: null, maxCount: 5)
            .Select(result => result.Word)).Contains("ist tom taub?");
    }

    [Fact]
    public void GetSuggestions_TrieFilter_DoesNotRefillPastTheFilteredTopSet()
    {
        var trie = new SentenceCompletionIndex();
        trie.Insert("ist tom taub?", 10);
        trie.Insert("ist das gut?", 5);
        var orchestrator = CreateOrchestrator(words: [], trie: trie);

        // The trie's top 1 is the unknown-word completion; it is dropped and no second fetch
        // happens, so no sentence completion survives maxCount 1.
        var results = orchestrator.GetSuggestions("ist", context: null, maxCount: 1);

        Check.That(results.Select(result => result.Type)).Not.Contains(PredictionType.SentenceCompletion);
    }

    [Fact]
    public void LearnWord_DelegatesToWordPredictor()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.LearnWord("newtestword");
        var results = orchestrator.GetSuggestions("newtest", context: null, maxCount: 5);
        Check.That(results).HasElementThatMatches(r => r.Word == "newtestword");
    }

    [Fact]
    public void LearnSequence_DelegatesToWordPredictor()
    {
        var orchestrator = CreateOrchestrator();
        orchestrator.LearnSequence(["hello", "world"]);
        var results = orchestrator.GetSuggestions("wo", context: "hello", maxCount: 5);
        Check.That(results).Not.IsEmpty();
    }
}
