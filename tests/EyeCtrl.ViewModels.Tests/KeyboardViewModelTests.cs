namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class KeyboardViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void ShowsKeyboard_OnShowMessage()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new ShowKeyboardMessage());
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void HidesKeyboard_OnHideMessage()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        _messenger.Send(new ShowKeyboardMessage());
        _messenger.Send(new HideKeyboardMessage());
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void ToggleModifier_AddsAndRemovesFromActiveModifiers()
    {
        var vm = CreateViewModel();
        Check.That(vm.ActiveModifiers).IsEmpty();

        vm.ToggleModifier(ModifierKey.Ctrl);
        Check.That(vm.ActiveModifiers).Contains(ModifierKey.Ctrl);

        vm.ToggleModifier(ModifierKey.Ctrl);
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
    }

    [Fact]
    public void ToggleModifier_MultipleCombine()
    {
        var vm = CreateViewModel();

        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Shift);
        Check.That(vm.ActiveModifiers).Contains(ModifierKey.Ctrl);
        Check.That(vm.ActiveModifiers).Contains(ModifierKey.Shift);
    }

    [Fact]
    public void IsShifted_TrueWhenShiftActive()
    {
        var vm = CreateViewModel();
        Check.That(vm.IsShifted).IsFalse();

        vm.ToggleModifier(ModifierKey.Shift);
        Check.That(vm.IsShifted).IsTrue();
    }

    [Fact]
    public void IsShifted_TrueWhenCapsLockActive()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.CapsLock);
        Check.That(vm.IsShifted).IsTrue();
    }

    [Fact]
    public void ActivateKey_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Shift);
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("c", shiftOutput: "C");

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void ActivateKey_CapsLockSurvivesClearing()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.CapsLock);
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: "A");

        Check.That(sentKey).IsEqualTo("A");
        Check.That(vm.ActiveModifiers).Contains(ModifierKey.CapsLock);
    }

    [Fact]
    public void ActivateKey_OneShotShiftCleared()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Shift);
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: "A");

        Check.That(sentKey).IsEqualTo("A");
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsFalse();
    }

    [Fact]
    public void HandleBackspace_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleBackspace();

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void HandleEnter_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleEnter();

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void HandleSpace_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleSpace();

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void HandleTab_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleTab();

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void SwitchSubLayout_DoesNotClearModifiers()
    {
        var vm = CreateViewModel();
        var layout = new KeyboardLayout
        {
            Name = "Test",
            DefaultSubLayout = "letters",
            SubLayouts = new()
            {
                ["letters"] = new SubLayout { Label = "ABC", Rows = new() },
                ["numbers"] = new SubLayout { Label = "123", Rows = new() }
            }
        };
        vm.SetLayout(layout);

        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.SwitchSubLayout("numbers");

        Check.That(vm.ActiveModifiers).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void ProcessKeyGaze_FindsNearestKey()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
            ["key_0_1"] = new(200, 100),
        };

        // Gaze at (110, 105) — closest to key_0_0
        vm.ProcessKeyGaze(positions, new PhysicalPoint(110, 105), isGazeInsideWindow: true, timestampUs: 0);

        Check.That(vm.NearestKeyId).IsEqualTo("key_0_0");
    }

    [Fact]
    public void ProcessKeyGaze_AccumulatesDwellProgress()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        // First frame: enter key
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        Check.That(vm.NearestKeyProgress).IsEqualTo(0.0); // just entered, no progress yet

        // Second frame: 250ms later = 50% progress
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 250_000);
        Check.That(vm.NearestKeyProgress).IsCloseTo(0.5, 2);
    }

    [Fact]
    public void ProcessKeyGaze_FiresCallbackOnDwellComplete()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        string? activatedKeyId = null;
        vm.KeyDwellCompleted += id => activatedKeyId = id;

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);

        Check.That(activatedKeyId).IsEqualTo("key_0_0");
    }

    [Fact]
    public void ProcessKeyGaze_CooldownPreventsReactivation()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        string? activatedKeyId = null;
        int activationCount = 0;
        vm.KeyDwellCompleted += id => { activatedKeyId = id; activationCount++; };

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        // Activate
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);
        Check.That(activationCount).IsEqualTo(1);

        // During cooldown (100ms after activation, cooldown is 200ms)
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 600_000);
        Check.That(vm.NearestKeyId).IsNull(); // reset during cooldown

        // After cooldown (250ms after activation)
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 750_000);
        Check.That(vm.NearestKeyId).IsEqualTo("key_0_0"); // tracking again
    }

    [Fact]
    public void ProcessKeyGaze_ResetsWhenGazeOutsideWindow()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        Check.That(vm.NearestKeyId).IsEqualTo("key_0_0");

        // Gaze moves outside window
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), isGazeInsideWindow: false, 100_000);
        Check.That(vm.NearestKeyId).IsNull();
    }

    // --- Text special key tests ---

    [Fact]
    public void HandleBackspace_RemovesLastCharAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hel";
        vm.HandleBackspace();

        Check.That(vm.CurrentInput).IsEqualTo("he");
        Check.That(sentKey).IsEqualTo("\b");
    }

    [Fact]
    public void HandleBackspace_DoesNothingWhenEmpty()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "";
        vm.HandleBackspace();

        Check.That(vm.CurrentInput).IsEqualTo("");
        Check.That(sentKey).IsEqualTo("\b");
    }

    [Fact]
    public void HandleEnter_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hello";
        vm.HandleEnter();

        Check.That(vm.CurrentInput).IsEqualTo("");
        Check.That(sentKey).IsEqualTo("\n");
    }

    [Fact]
    public void HandleSpace_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hello";
        vm.HandleSpace();

        Check.That(vm.CurrentInput).IsEqualTo("");
        Check.That(sentKey).IsEqualTo(" ");
    }

    [Fact]
    public void HandleTab_SendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.HandleTab();

        Check.That(sentKey).IsEqualTo("\t");
    }

    // --- Key activation tests ---

    [Fact]
    public void ActivateKey_SendsOutputAndAppendsToInput()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: null);

        Check.That(sentKey).IsEqualTo("a");
        Check.That(vm.CurrentInput).IsEqualTo("a");
    }

    [Fact]
    public void ActivateKey_UsesShiftOutputWhenShifted()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Shift);
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: "A");

        Check.That(sentKey).IsEqualTo("A");
        Check.That(vm.CurrentInput).IsEqualTo("A");
        Check.That(vm.IsShifted).IsFalse(); // one-shot cleared
    }

    [Fact]
    public void ActivateKey_ShiftAlwaysClearedOneShot()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Shift);
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("1", shiftOutput: null);

        Check.That(sentKey).IsEqualTo("1");
        Check.That(vm.IsShifted).IsFalse(); // one-shot: always cleared
    }

    // --- System modifier tests (no text input pollution) ---

    [Fact]
    public void ActivateKey_WithCtrl_DoesNotUpdateCurrentInput()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.ActivateKey("c", shiftOutput: "C");

        Check.That(vm.CurrentInput).IsEqualTo("");
    }

    [Fact]
    public void HandleBackspace_WithCtrl_DoesNotUpdateCurrentInput()
    {
        var vm = CreateViewModel();
        vm.CurrentInput = "hel";
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleBackspace();

        Check.That(vm.CurrentInput).IsEqualTo("hel");
    }

    [Fact]
    public void HandleEnter_WithAlt_DoesNotClearCurrentInput()
    {
        var vm = CreateViewModel();
        vm.CurrentInput = "hello";
        vm.ToggleModifier(ModifierKey.Alt);
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, _) => { });

        vm.HandleEnter();

        Check.That(vm.CurrentInput).IsEqualTo("hello");
    }

    // --- Prediction tests ---

    [Fact]
    public void GetSuggestions_DelegatesToPredictor()
    {
        var vm = CreateViewModel();
        var suggestions = vm.GetSuggestions();
        Check.That(suggestions).IsEmpty();
    }

    [Fact]
    public void HandlePredictionSelected_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        vm.CurrentInput = "hel";

        PredictionSelectedMessage? sentMsg = null;
        _messenger.Register<PredictionSelectedMessage>(_messenger, (_, msg) => sentMsg = msg);

        vm.HandlePredictionSelected("hello");

        Check.That(vm.CurrentInput).IsEqualTo("");
        Check.That(sentMsg).IsNotNull();
        Check.That(sentMsg!.Word).IsEqualTo("hello");
        Check.That(sentMsg.DeleteCount).IsEqualTo(3);
    }

    // --- Special key tests (function and navigation keys) ---

    [Fact]
    public void HandleSpecialKey_SendsSpecialKeyMessageWithoutModifiers()
    {
        var vm = CreateViewModel();
        SpecialKeyMessage? sentMsg = null;
        _messenger.Register<SpecialKeyMessage>(_messenger, (_, msg) => sentMsg = msg);

        vm.HandleSpecialKey("F1");

        Check.That(sentMsg).IsNotNull();
        Check.That(sentMsg!.KeyName).IsEqualTo("F1");
        Check.That(sentMsg.Modifiers).IsNull();
    }

    [Fact]
    public void HandleSpecialKey_SendsSpecialKeyMessageWithModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        SpecialKeyMessage? sentMsg = null;
        _messenger.Register<SpecialKeyMessage>(_messenger, (_, msg) => sentMsg = msg);

        vm.HandleSpecialKey("F5");

        Check.That(sentMsg).IsNotNull();
        Check.That(sentMsg!.KeyName).IsEqualTo("F5");
        Check.That(sentMsg.Modifiers).IsNotNull();
        Check.That(sentMsg.Modifiers!).Contains(ModifierKey.Ctrl);
    }

    [Fact]
    public void HandleSpecialKey_ClearsOneShotModifiers()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.Ctrl);
        vm.ToggleModifier(ModifierKey.Alt);
        _messenger.Register<SpecialKeyMessage>(_messenger, (_, _) => { });

        vm.HandleSpecialKey("Escape");

        Check.That(vm.ActiveModifiers).IsEmpty();
    }

    [Fact]
    public void HandleSpecialKey_CapsLockSurvivesClearing()
    {
        var vm = CreateViewModel();
        vm.ToggleModifier(ModifierKey.CapsLock);
        vm.ToggleModifier(ModifierKey.Ctrl);
        _messenger.Register<SpecialKeyMessage>(_messenger, (_, _) => { });

        vm.HandleSpecialKey("Home");

        Check.That(vm.ActiveModifiers).Contains(ModifierKey.CapsLock);
        Check.That(vm.ActiveModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_CapitalizesAtStart()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "hello", 100 }, { "help", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        vm.CurrentInput = "hel";
        var suggestions = vm.GetSuggestions();

        // Should capitalize at start (empty recent text)
        Check.That(suggestions).Contains("Hello");
        Check.That(suggestions.Contains("hello")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_CapitalizesAfterPeriod()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "world", 100 }, { "work", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        // Simulate typing "Hello. "
        vm.CurrentInput = "Hello";
        vm.HandleSpace();
        vm.ActivateKey(".", null);
        vm.HandleSpace();

        vm.CurrentInput = "wo";
        var suggestions = vm.GetSuggestions();

        // Should capitalize after period
        Check.That(suggestions).Contains("World");
        Check.That(suggestions.Contains("world")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_DoesNotCapitalizeInMiddle()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "world", 100 }, { "work", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        // Simulate typing "Hello "
        vm.CurrentInput = "Hello";
        vm.HandleSpace();

        vm.CurrentInput = "wo";
        var suggestions = vm.GetSuggestions();

        // Should NOT capitalize in middle of sentence
        Check.That(suggestions).Contains("world");
        Check.That(suggestions.Contains("World")).IsFalse();
    }

    [Fact]
    public void GetSuggestions_SmartCapitalization_DisabledWhenSettingIsFalse()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "hello", 100 }, { "help", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        vm.CurrentInput = "hel";
        var suggestions = vm.GetSuggestions();

        // Should NOT capitalize when disabled
        Check.That(suggestions).Contains("hello");
        Check.That(suggestions.Contains("Hello")).IsFalse();
    }

    [Fact]
    public void HandleEnter_ClearsRecentTextForCapitalization()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "world", 100 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        // Type "Hello "
        vm.CurrentInput = "Hello";
        vm.HandleSpace();

        // Press Enter to start new line
        vm.HandleEnter();

        // Type "wo" - should capitalize after Enter
        vm.CurrentInput = "wo";
        var suggestions = vm.GetSuggestions();

        Check.That(suggestions).Contains("World");
    }

    [Fact]
    public void GetSuggestionsWithContext_ReturnsEmptyWhenNoPredictorSet()
    {
        var vm = CreateViewModel();
        var results = vm.GetSuggestionsWithContext();
        Check.That(results).IsEmpty();
    }

    [Fact]
    public void GetSuggestionsWithContext_ReturnsPredictionResultsWithMetadata()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "hello", 100 }, { "help", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        vm.CurrentInput = "hel";
        var results = vm.GetSuggestionsWithContext();

        Check.That(results).Not.IsEmpty();
        Check.That(results).ContainsOnlyElementsThatMatch(r =>
            r.Word != null && r.Score > 0 && r.Confidence >= 0);
        // Should have valid prediction types
        Check.That(results).HasElementThatMatches(r =>
            r.Type == EyeCtrl.Core.Prediction.PredictionType.ExactPrefix ||
            r.Type == EyeCtrl.Core.Prediction.PredictionType.FuzzyMatch ||
            r.Type == EyeCtrl.Core.Prediction.PredictionType.NextWord ||
            r.Type == EyeCtrl.Core.Prediction.PredictionType.Phrase);
    }

    [Fact]
    public void GetSuggestionsWithContext_IncludesTypeInformation()
    {
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "hello", 100 }, { "help", 80 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        vm.CurrentInput = "hel";
        var results = vm.GetSuggestionsWithContext();

        // Should have ExactPrefix type for exact matches
        Check.That(results).HasElementThatMatches(r => r.Type == EyeCtrl.Core.Prediction.PredictionType.ExactPrefix);
    }

    [Fact]
    public void GetSuggestionsWithContext_PassesContextToPredictorForBigrams()
    {
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "thank", 100 }, { "you", 100 }, { "are", 50 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        // Learn "thank you" bigram multiple times to exceed MinFrequency
        predictor.LearnSequence(new[] { "thank", "you" });
        predictor.LearnSequence(new[] { "thank", "you" });
        predictor.LearnSequence(new[] { "thank", "you" });

        // Type "thank " (space sets previous word)
        vm.CurrentInput = "thank";
        vm.HandleSpace();

        // Now type empty prefix - should get bigram suggestions
        vm.CurrentInput = "";
        var results = vm.GetSuggestionsWithContext();

        // Should include "you" with NextWord type (may be capitalized due to context)
        Check.That(results).HasElementThatMatches(r => string.Equals(r.Word, "you", StringComparison.OrdinalIgnoreCase) && r.Type == EyeCtrl.Core.Prediction.PredictionType.NextWord);
    }

    [Fact]
    public void GetSuggestionsWithContext_PassesFullTextForSmartCapitalization()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = true;
        var vm = new KeyboardViewModel(settings, _messenger);

        var provider = new InMemoryDictionaryProvider(new() { { "world", 100 } });
        var predictor = new EyeCtrl.Core.Prediction.WordPredictor(provider);
        vm.SetPredictor(predictor);

        // Simulate typing "Hello. "
        vm.CurrentInput = "Hello";
        vm.HandleSpace();
        vm.ActivateKey(".", null);
        vm.HandleSpace();

        vm.CurrentInput = "wo";
        var results = vm.GetSuggestionsWithContext();

        // Should capitalize after period
        Check.That(results).HasElementThatMatches(r => r.Word == "World");
        Check.That(results.Any(r => r.Word == "world")).IsFalse();
    }

    private KeyboardViewModel CreateViewModel()
    {
        return new KeyboardViewModel(_settings, _messenger);
    }
}

// Test helper for predictions
public class InMemoryDictionaryProvider : EyeCtrl.Core.Prediction.IDictionaryProvider
{
    private readonly Dictionary<string, int> _words;
    public InMemoryDictionaryProvider(Dictionary<string, int> words) => _words = words;
    public Dictionary<string, int> Load() => new(_words);
    public void Save(Dictionary<string, int> words) { }
}
