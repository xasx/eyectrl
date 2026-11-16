namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// F7's boundary half: at a word boundary (empty prefix, a previous word) the bar offers the
/// sentence trie's next word and the bigram's, holds slot 0 back for the language model, and fills
/// that slot from a late answer only while the very same boundary is still current.
/// </summary>
public class PredictionOrchestratorNextWordTests
{
    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    /// <summary>
    /// Consume-once next-word predictor, like <c>SmallLMPredictor</c>: results are handed over
    /// exactly once, and null means "still running" (never "ran and produced nothing").
    /// </summary>
    private sealed class FakeLm : ILanguageModelPredictor
    {
        private PredictionResult? _pending;

        public List<(string Context, string? Prefix)> CompletionCalls { get; } = [];
        public List<string> NextWordCalls { get; } = [];
        public bool IsAvailable { get; set; } = true;

        public void Predict(string context, string? prefix = null) => CompletionCalls.Add((context, prefix));

        public void PredictNextWord(string context) => NextWordCalls.Add(context);

        public void Complete(string word) =>
            _pending = new PredictionResult(word, 50, PredictionType.LMSuggestion, 0.5);

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            var result = _pending;
            _pending = null;
            return result is null ? null : [result];
        }

        public void Cancel() { }
        public void Dispose() { }
    }

    private static Dictionary<string, int> Dictionary() => new()
    {
        ["ist"] = 100, ["steht"] = 90, ["leer"] = 80, ["Haus"] = 70, ["hier"] = 60,
        ["island"] = 5,
    };

    private static SentenceCompletionIndex Trie()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("Das Haus steht leer .", 5);
        index.Insert("Das Haus ist alt .", 3);
        return index;
    }

    private static PredictionOrchestrator Create(IDictionaryProvider? provider = null, FakeLm? lm = null,
        SentenceCompletionIndex? trie = null, bool mlEnabled = true)
        => new(new WordPredictor(provider ?? new InMemoryDictProvider(Dictionary()), "QWERTY"),
            sentenceIndex: trie, lmPredictor: lm, mlEnabled: mlEnabled);

    [Fact]
    public void GetSuggestions_AtABoundary_OffersTheTrieAndBigramWords_AndReservesSlotZero()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.LearnPair("Haus", "ist");
        orchestrator.LearnPair("Haus", "ist");   // BigramModel needs a count of 2

        var results = orchestrator.GetSuggestions("", context: "Haus", maxCount: 5,
            committedText: "Das Haus ", nextWordEnabled: true);

        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Word == "ist" && r.Type == PredictionType.NextWord)).IsTrue();
        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Word == "steht" && r.Type == PredictionType.SentenceCompletion)).IsTrue();
        Check.That(orchestrator.NextWordSlotReserved).IsTrue();
        Check.That(lm.NextWordCalls).ContainsExactly("Das Haus");
    }

    [Fact]
    public void GetSuggestions_AtABoundary_LeavesTheReservedSlotOutOfTheBudget()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.LearnPair("Haus", "ist");
        orchestrator.LearnPair("Haus", "ist");

        var results = orchestrator.GetSuggestions("", context: "Haus", maxCount: 2,
            committedText: "Das Haus ", nextWordEnabled: true);

        // Two slots, one of them reserved for the LM: only one conventional word may come back.
        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => r.Word))}")
            .That(results).HasSize(1);
    }

    [Fact]
    public void GetSuggestions_NextWordDisabled_AsksNothingAndReservesNothing()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.LearnPair("Haus", "ist");
        orchestrator.LearnPair("Haus", "ist");

        var results = orchestrator.GetSuggestions("", context: "Haus", maxCount: 5,
            committedText: "Das Haus ", nextWordEnabled: false);

        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
        Check.That(lm.NextWordCalls).IsEmpty();
        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Type == PredictionType.SentenceCompletion)).IsFalse();
        // The bigram source is not part of the feature: it behaves exactly as it did before F7.
        Check.That(results.Any(r => r.Word == "ist")).IsTrue();
    }

    [Fact]
    public void GetSuggestions_WithATypedPrefix_DoesNotReserveASlot()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());

        orchestrator.GetSuggestions("hau", context: "das", maxCount: 5, committedText: "Das ");

        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
        Check.That(lm.NextWordCalls).IsEmpty();
    }

    [Fact]
    public void TryTakeNextWord_BeforeTheAnswerArrives_ReturnsNullAndKeepsTheReservation()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");

        var next = orchestrator.TryTakeNextWord("Haus", "Das Haus ", smartCapitalization: false);

        Check.That(next).IsNull();
        Check.That(orchestrator.NextWordSlotReserved).IsTrue();
    }

    [Fact]
    public void TryTakeNextWord_WhenTheAnswerArrives_ReturnsItAndClosesTheReservation()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        lm.Complete("leer");

        var next = orchestrator.TryTakeNextWord("Haus", "Das Haus ", smartCapitalization: false);

        Check.That(next).IsNotNull();
        Check.That(next!).HasSize(1);
        Check.That(next![0].Word).IsEqualTo("leer");
        Check.That(next[0].Type).IsEqualTo(PredictionType.LMSuggestion);
        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
    }

    [Fact]
    public void TryTakeNextWord_WhenTheBoundaryMovedOn_ReturnsEmptyAndNeverTheWord()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        lm.Complete("leer");

        // The user typed somewhere else since the render: the answer belongs to nothing on screen.
        var next = orchestrator.TryTakeNextWord("Haus", "Das Haus und", smartCapitalization: false);

        Check.That(next).IsNotNull();
        Check.That(next!).IsEmpty();
        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
    }

    [Fact]
    public void TryTakeNextWord_WordOutsideTheDictionary_IsDropped()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        lm.Complete("Hausierer");

        var next = orchestrator.TryTakeNextWord("Haus", "Das Haus ", smartCapitalization: false);

        Check.That(next).IsNotNull();
        Check.That(next!).IsEmpty();
    }

    [Fact]
    public void TryTakeNextWord_AtASentenceStart_CapitalisesTheWord()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        // "schlimm." is longer than the four-character abbreviation window, so the shipped rule
        // says "a new sentence starts here".
        orchestrator.GetSuggestions("", context: "schlimm", maxCount: 5, committedText: "Das ist schlimm. ");
        lm.Complete("leer");

        var next = orchestrator.TryTakeNextWord("schlimm", "Das ist schlimm. ", smartCapitalization: true);

        Check.That(next).IsNotNull();
        Check.That(next!).HasSize(1);
        Check.That(next![0].Word).IsEqualTo("Leer");
    }

    [Fact]
    public void TryTakeNextWord_WithSmartCapitalizationOff_KeepsTheModelsCasing()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "schlimm", maxCount: 5, committedText: "Das ist schlimm. ");
        lm.Complete("leer");

        var next = orchestrator.TryTakeNextWord("schlimm", "Das ist schlimm. ", smartCapitalization: false);

        Check.That(next).IsNotNull();
        Check.That(next![0].Word).IsEqualTo("leer");
    }

    [Fact]
    public void GetSuggestions_ARenderAwayFromTheBoundary_DropsAPendingReservation()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        Check.That(orchestrator.NextWordSlotReserved).IsTrue();

        // A keystroke: the boundary is gone, so the reserved slot must not survive it.
        orchestrator.GetSuggestions("h", context: "Haus", maxCount: 5, committedText: "Das ");

        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
        Check.That(orchestrator.TryTakeNextWord("Haus", "Das Haus ", smartCapitalization: false)).IsNull();
    }

    [Fact]
    public void GetSuggestions_WithAPrefix_StillUsesTheCompletionRequest()
    {
        // The F7 seam must not touch the mode that already worked: a typed prefix asks the model
        // for a COMPLETION (context + prefix), never for a next word.
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());

        orchestrator.GetSuggestions("hau", context: "das", maxCount: 5, committedText: "Das ");

        Check.That(lm.CompletionCalls).ContainsExactly(("das", "hau"));
        Check.That(lm.NextWordCalls).IsEmpty();
        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
    }

    [Fact]
    public void GetSuggestions_WithAnUnavailableModel_ReservesNothing()
    {
        var lm = new FakeLm { IsAvailable = false };
        using var orchestrator = Create(lm: lm, trie: Trie());

        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");

        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
        Check.That(lm.NextWordCalls).IsEmpty();
    }

    [Fact]
    public void GetSuggestions_WithMlDisabled_ReservesNothingEvenWhenTheModelIsAvailable()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie(), mlEnabled: false);

        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");

        Check.That(orchestrator.NextWordSlotReserved).IsFalse();
        Check.That(lm.NextWordCalls).IsEmpty();
    }

    [Fact]
    public void GetSuggestions_AfterABoundaryRequest_ItsAnswerIsNeverMergedAsACompletion()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());

        // A completion request first, so the completion identity ("Haus") is live when the
        // boundary request replaces that request in the predictor.
        orchestrator.GetSuggestions("i", context: "Haus", maxCount: 5, committedText: "Das ");
        Check.That(lm.CompletionCalls).ContainsExactly(("Haus", "i"));

        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        lm.Complete("island");   // the next-word answer

        // The user types again. "island" happens to start with "is", but it was generated for the
        // boundary and is NOT a completion of the new prefix; with room for one word only, a leak
        // would replace the dictionary's own "ist".
        var results = orchestrator.GetSuggestions("is", context: "Haus", maxCount: 1,
            committedText: "Das ");

        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Word == "island" && r.Type == PredictionType.LMSuggestion)).IsFalse();
        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Word == "ist" && r.Type == PredictionType.ExactPrefix)).IsTrue();
    }

    [Fact]
    public void GetSuggestions_AfterEnter_ALateNextWordAnswerIsNotMergedIntoTheFirstWord()
    {
        var lm = new FakeLm();
        using var orchestrator = Create(lm: lm, trie: Trie());

        // The boundary request for the old sentence is still in flight.
        orchestrator.GetSuggestions("", context: "Haus", maxCount: 5, committedText: "Das Haus ");
        Check.That(lm.NextWordCalls).ContainsExactly("Das Haus");
        lm.Complete("island");

        // Enter ends the session: no context, no committed text. The render returns early, so the
        // answer bag is never consumed and the reservation is gone.
        orchestrator.GetSuggestions("", context: null, maxCount: 5, committedText: "");

        // The next line starts with "is". The old answer is not a completion of it.
        var results = orchestrator.GetSuggestions("is", context: null, maxCount: 1, committedText: "");

        Check.WithCustomMessage($"rendered: {string.Join("|", results.Select(r => $"{r.Word}/{r.Type}"))}")
            .That(results.Any(r => r.Type == PredictionType.LMSuggestion)).IsFalse();
    }
}
