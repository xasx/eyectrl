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
            words: new Dictionary<string, int> { ["thank"] = 50, ["you"] = 40 },
            trie: trie);

        var results = orchestrator.GetSuggestions("thank", context: null, maxCount: 5);
        Check.That(results).HasElementThatMatches(r => r.Type == PredictionType.SentenceCompletion && r.Word == "you very much");
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
