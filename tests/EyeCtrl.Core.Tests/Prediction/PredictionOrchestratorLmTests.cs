namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

/// <summary>
/// The LM half of the bar: asked only with a context and a prefix, only its latest request's
/// results are merged, only those that match the current prefix and are longer, and one fresh
/// completion gets a reserved slot right after the top dictionary match.
/// </summary>
public class PredictionOrchestratorLmTests
{
    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class FakeLm : ILanguageModelPredictor
    {
        public List<(string Context, string Prefix)> Calls { get; } = [];
        public string? NextWord { get; set; }
        private PredictionResult? _pending;

        public bool IsAvailable => true;

        public void Predict(string context, string? prefix = null)
        {
            Calls.Add((context, prefix ?? ""));
            if (NextWord is not null)
                _pending = new PredictionResult(NextWord, 50, PredictionType.LMSuggestion, 0.5);
        }

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            var result = _pending;
            _pending = null;
            return result is null ? null : [result];
        }

        public void Cancel() { }
        public void Dispose() { }
    }

    private static PredictionOrchestrator CreateOrchestrator(
        Dictionary<string, int> words, FakeLm lm)
        => new(new WordPredictor(new InMemoryDictProvider(words), "QWERTY"),
            sentenceIndex: null, lmPredictor: lm);

    private static Dictionary<string, int> GermanWetterWords() => new()
    {
        ["Wetter"] = 100, ["Wettkampf"] = 90, ["Wettbewerb"] = 80,
    };

    [Fact]
    public void GetSuggestions_WithoutContext_DoesNotAskTheLm()
    {
        var lm = new FakeLm { NextWord = "Wetterdaten" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: null, maxCount: 5);

        Check.That(lm.Calls).IsEmpty();
    }

    [Fact]
    public void GetSuggestions_WithContextAndPrefix_AsksTheLm()
    {
        var lm = new FakeLm { NextWord = "Wetterdaten" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);

        Check.That(lm.Calls).ContainsExactly(("das", "Wet"));
    }

    [Fact]
    public void GetSuggestions_ResultsFromAnEarlierRequest_AreNotShown()
    {
        var lm = new FakeLm { NextWord = "Wetterdaten" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
        var current = orchestrator.GetSuggestions("Wet", context: "ich", maxCount: 5);

        Check.WithCustomMessage($"rendered: {string.Join("|", current.Select(r => r.Word))}")
            .That(current.Any(r => r.Word == "Wetterdaten")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_ResultsNotMatchingTheCurrentPrefix_AreNotShown()
    {
        var lm = new FakeLm { NextWord = "ist" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
        var current = orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);

        Check.WithCustomMessage($"rendered: {string.Join("|", current.Select(r => r.Word))}")
            .That(current.Any(r => r.Word == "ist")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_FreshMatchingCompletion_GoesIntoSlotTwo()
    {
        var lm = new FakeLm { NextWord = "Wetterdaten" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
        var current = orchestrator.GetSuggestions("Wet", context: "das", maxCount: 3);

        // Slot 1 is the top dictionary match; the completion replaces the lowest-ranked entry.
        Check.That(current).HasSize(3);
        Check.That(current[0].Word).IsEqualTo("Wetter");
        Check.That(current[1].Word).IsEqualTo("Wetterdaten");
    }
}
