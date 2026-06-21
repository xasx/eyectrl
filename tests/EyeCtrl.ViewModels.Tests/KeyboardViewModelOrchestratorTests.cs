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
}
