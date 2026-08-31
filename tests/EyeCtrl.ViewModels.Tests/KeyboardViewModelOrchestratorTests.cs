namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public class KeyboardViewModelOrchestratorTests
{
    private class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    [Fact]
    public void SetOrchestrator_ThenGetSuggestions_ReturnsResults()
    {
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);

        var words = new Dictionary<string, int> { ["hello"] = 100, ["help"] = 80 };
        var provider = new InMemoryDictProvider(words);
        var predictor = new WordPredictor(provider, "QWERTY");
        var orchestrator = new PredictionOrchestrator(predictor);
        vm.SetOrchestrator(orchestrator);

        vm.GetType().GetProperty("CurrentInput")!.SetValue(vm, "hel");
        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).Not.IsEmpty();
        Check.That(suggestions).HasElementThatMatches(r => r.Word == "hello");
    }

    [Fact]
    public void SetOrchestrator_Null_ReturnsEmptySuggestions()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(null);
        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).IsEmpty();
    }

    [Fact]
    public void HandlePredictionSelected_LearnsWord()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);
        var words = new Dictionary<string, int> { ["hello"] = 100 };
        var provider = new InMemoryDictProvider(words);
        var predictor = new WordPredictor(provider, "QWERTY");
        var orchestrator = new PredictionOrchestrator(predictor);
        vm.SetOrchestrator(orchestrator);

        vm.GetType().GetProperty("CurrentInput")!.SetValue(vm, "hel");
        vm.HandlePredictionSelected("hello");

        Check.That(vm.GetType().GetProperty("CurrentInput")!.GetValue(vm)).IsEqualTo("");
    }

    [Fact]
    public void HandlePredictionSelected_MultiWord_LearnsTheWordsNotTheWholeSentence()
    {
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var messenger = new WeakReferenceMessenger();
        using var vm = new KeyboardViewModel(settings, messenger);
        var provider = new InMemoryDictProvider([]);
        var predictor = new WordPredictor(provider, "QWERTY");
        var orchestrator = new PredictionOrchestrator(predictor);
        vm.SetOrchestrator(orchestrator);

        // A sentence completion is accepted as one string. Learning it verbatim would make
        // "thank you very much" a single key in the user's persisted dictionary, suggested back
        // whole on the next "thank" — the individual words are what the user actually typed.
        vm.GetType().GetProperty("CurrentInput")!.SetValue(vm, "thank");
        vm.HandlePredictionSelected("thank you very much");

        var suggestions = orchestrator.GetSuggestions("thank", context: null, maxCount: 10);
        Check.That(suggestions.Select(r => r.Word)).Not.Contains("thank you very much");
        Check.That(suggestions.Select(r => r.Word)).Contains("thank");
        Check.That(orchestrator.GetSuggestions("ver", context: null, maxCount: 10).Select(r => r.Word))
            .Contains("very");
    }
}
