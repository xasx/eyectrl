namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// The suggestion count flows from KeyboardSettings.MaxSuggestions through
/// GetSuggestionsWithContext to the orchestrator — the bar renders suggestions.Count cells, so
/// the count here IS the count the user sees. New file (not added to KeyboardViewModelTests)
/// because that file is under active edit on feat/keyboard-action-edge-toggle.
/// </summary>
public class KeyboardPredictionCountTests
{
    private static KeyboardViewModel Create(KeyboardSettings keyboard, out WeakReferenceMessenger messenger)
    {
        messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.MaxSuggestions = keyboard.MaxSuggestions;
        // Capitalization is orthogonal to the count and on by default — off, so the word
        // assertions don't depend on "capitalizes at start" behavior.
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, messenger);

        // Six "hel" words at descending frequencies so the dictionary alone fills a 5-slot bar.
        var provider = new InMemoryDictionaryProvider(new()
        {
            { "hello", 100 }, { "help", 90 }, { "helm", 80 },
            { "helium", 70 }, { "helmet", 60 }, { "helix", 50 }
        });
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(provider)));
        return vm;
    }

    [Fact]
    public void GetSuggestions_DefaultsToFiveSlots()
    {
        var vm = Create(new KeyboardSettings(), out _);
        vm.CurrentInput = "hel";

        Check.That(vm.GetSuggestionsWithContext()).HasSize(5);
    }

    [Fact]
    public void GetSuggestions_RespectsMaxSuggestions()
    {
        var keyboard = new KeyboardSettings { MaxSuggestions = 2 };
        var vm = Create(keyboard, out _);
        vm.CurrentInput = "hel";

        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).HasSize(2);
        Check.That(suggestions[0].Word).IsEqualTo("hello"); // capped from the TOP, not the tail
    }

    [Fact]
    public void GetSuggestions_MaxSuggestionsOfOne_YieldsSingleBest()
    {
        var keyboard = new KeyboardSettings { MaxSuggestions = 1 };
        var vm = Create(keyboard, out _);
        vm.CurrentInput = "hel";

        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).HasSize(1);
        Check.That(suggestions[0].Word).IsEqualTo("hello");
    }
}
