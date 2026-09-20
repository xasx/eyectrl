namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// The suggestion count flows from KeyboardSettings.MaxSuggestions through
/// GetSuggestionsWithContext to the orchestrator — the bar renders suggestions.Count cells, so
/// the count here IS the count the user sees. Self-contained on purpose (review round 1,
/// finding 3): the shared InMemoryDictionaryProvider lives in KeyboardViewModelTests.cs, the
/// exact file this work avoids because feat/keyboard-action-edge-toggle is actively editing it —
/// a local fixture keeps this file compiling no matter how that one is reshaped.
/// </summary>
public class KeyboardPredictionCountTests
{
    private sealed class SixWordHelProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words = new()
        {
            { "hello", 100 }, { "help", 90 }, { "helm", 80 },
            { "helium", 70 }, { "helmet", 60 }, { "helix", 50 }
        };
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static KeyboardViewModel Create(int maxSuggestions)
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.MaxSuggestions = maxSuggestions;
        // Capitalization is orthogonal to the count and on by default — off, so the word
        // assertions don't depend on "capitalizes at start" behavior.
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(new SixWordHelProvider())));
        return vm;
    }

    [Fact]
    public void GetSuggestions_DefaultsToFiveSlots()
    {
        var vm = Create(5);
        vm.CurrentInput = "hel";

        Check.That(vm.GetSuggestionsWithContext()).HasSize(5);
    }

    [Fact]
    public void GetSuggestions_RespectsMaxSuggestions()
    {
        var vm = Create(2);
        vm.CurrentInput = "hel";

        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).HasSize(2);
        Check.That(suggestions[0].Word).IsEqualTo("hello"); // capped from the TOP, not the tail
    }

    [Fact]
    public void GetSuggestions_MaxSuggestionsOfOne_YieldsSingleBest()
    {
        var vm = Create(1);
        vm.CurrentInput = "hel";

        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).HasSize(1);
        Check.That(suggestions[0].Word).IsEqualTo("hello");
    }
}
