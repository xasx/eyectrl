namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
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

        public void Complete(string word)
            => _pending = new PredictionResult(word, 50, PredictionType.LMSuggestion, 0.5);

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
        var words = GermanWetterWords();
        words["Wetterdaten"] = 1;   // known, but cut from the top 3 by score
        var lm = new FakeLm { NextWord = "Wetterdaten" };
        using var orchestrator = CreateOrchestrator(words, lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
        var current = orchestrator.GetSuggestions("Wet", context: "das", maxCount: 3);

        // Slot 1 is the top dictionary match; the completion replaces the lowest-ranked entry.
        Check.That(current).HasSize(3);
        Check.That(current[0].Word).IsEqualTo("Wetter");
        Check.That(current[1].Word).IsEqualTo("Wetterdaten");
    }

    [Fact]
    public void GetSuggestions_LetterByLetter_CompletionAppearsOnTheNextKeystroke()
    {
        // The worker answers after the keystroke that asked, so the result for one prefix is
        // read at the next. maxCount 5 with six matching words keeps the completion out of the
        // bar by score, so seeing it proves the LM merge, not the dictionary.
        var words = new Dictionary<string, int>
        {
            ["Wetter"] = 100, ["Wettkampf"] = 90, ["Wettbewerb"] = 80,
            ["Wette"] = 70, ["Wetten"] = 60, ["Wetterdaten"] = 1,
        };
        var lm = new FakeLm();
        using var orchestrator = CreateOrchestrator(words, lm);

        orchestrator.GetSuggestions("W", context: "das", maxCount: 5);
        lm.Complete("Wetterdaten");

        var atWe = orchestrator.GetSuggestions("We", context: "das", maxCount: 5);
        Check.WithCustomMessage($"rendered at 'We': {string.Join("|", atWe.Select(r => r.Word))}")
            .That(atWe.Any(r => r.Word == "Wetterdaten")).IsTrue();

        lm.Complete("Wetterdaten");
        var atWett = orchestrator.GetSuggestions("Wett", context: "das", maxCount: 5);
        Check.WithCustomMessage($"rendered at 'Wett': {string.Join("|", atWett.Select(r => r.Word))}")
            .That(atWett.Any(r => r.Word == "Wetterdaten")).IsTrue();

        lm.Complete("Wetterdaten");
        var atNotContinued = orchestrator.GetSuggestions("Wettx", context: "das", maxCount: 5);
        Check.That(atNotContinued.Any(r => r.Word == "Wetterdaten")).IsFalse();

        lm.Complete("Wetterdaten");
        var atOtherContext = orchestrator.GetSuggestions("Wettx", context: "ich", maxCount: 5);
        Check.That(atOtherContext.Any(r => r.Word == "Wetterdaten")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_LmWordNotInTheDictionary_IsNotShown()
    {
        // A continuation the model invents ("Wetterfrosch") is junk even though it starts with
        // the typed prefix: the dictionary is the vocabulary gate.
        var lm = new FakeLm { NextWord = "Wetterfrosch" };
        using var orchestrator = CreateOrchestrator(GermanWetterWords(), lm);

        orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
        var current = orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);

        Check.WithCustomMessage($"rendered: {string.Join("|", current.Select(r => r.Word))}")
            .That(current.Any(r => r.Word == "Wetterfrosch")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_LmCompletionOfAKnownWord_ShowsTheDictionaryDisplayForm()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dict_lm_casing_{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var basePath = Path.Combine(dir, "de.json");
            File.WriteAllText(basePath, JsonSerializer.Serialize(new
            {
                words = new Dictionary<string, int> { ["wetter"] = 100, ["wetterdaten"] = 1 },
                casing = new Dictionary<string, string>
                {
                    ["wetter"] = "Wetter",
                    ["wetterdaten"] = "Wetterdaten",
                },
                abbreviations = Array.Empty<string>(),
            }));
            var provider = new FileDictionaryProvider(basePath, dir, "de");
            var lm = new FakeLm();
            using var orchestrator = new PredictionOrchestrator(
                new WordPredictor(provider, "QWERTZ"), lmPredictor: lm);

            orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);
            lm.Complete("wetterdaten");
            var current = orchestrator.GetSuggestions("Wet", context: "das", maxCount: 5);

            Check.WithCustomMessage($"rendered: {string.Join("|", current.Select(r => r.Word))}")
                .That(current.Any(r => r.Word == "Wetterdaten")).IsTrue();
            Check.That(current.Any(r => r.Word == "wetterdaten")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
