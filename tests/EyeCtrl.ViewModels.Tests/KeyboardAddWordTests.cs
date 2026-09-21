namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The explicit add-word action (prediction-controls #22, OptiKey's "Add To Dictionary" analog):
/// teaching the predictor the word currently being typed, on demand, without waiting for the
/// silent space-commit learning. Self-contained fixture for the same reason as
/// KeyboardPredictionCountTests — the shared provider lives in a file this branch keeps clear of.
/// </summary>
public class KeyboardAddWordTests
{
    private sealed class SparseProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words = new() { { "hello", 100 } };
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static KeyboardViewModel Create(out WeakReferenceMessenger messenger)
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(new SparseProvider())));
        return vm;
    }

    [Fact]
    public void HandleAddWord_LearnsTheCurrentInputSoItPredictsImmediately()
    {
        var vm = Create(out _);
        vm.CurrentInput = "zeppelin"; // not in the fixture dictionary

        vm.HandleAddWord();

        var suggestions = vm.GetSuggestionsWithContext();
        Check.That(suggestions).HasElementThatMatches(s => s.Word == "zeppelin");
    }

    [Fact]
    public void HandleAddWord_EmptyInput_IsANoOp()
    {
        var vm = Create(out _);
        vm.CurrentInput = "";

        Check.ThatCode(() => vm.HandleAddWord()).DoesNotThrow();
        Check.That(vm.GetSuggestionsWithContext().Select(s => s.Word)).Not.Contains("zeppelin");
    }

    [Fact]
    public void HandleAddWord_KeepsTypingStateIntact()
    {
        // The key is a local teach action, not a typing action: the input keeps its word (the
        // user is mid-typing), nothing is sent to the OS, and the word does not commit (that is
        // Space's job — add-word must not advance the next-word context on its own).
        var vm = Create(out var messenger);
        vm.CurrentInput = "zeppelin";
        string? sentKey = null;
        string? sentSpecialKey = null;
        messenger.Register<KeyboardAddWordTests, KeyActivatedMessage>(this, (_, m) => sentKey = m.Key);
        messenger.Register<KeyboardAddWordTests, SpecialKeyMessage>(this, (_, m) => sentSpecialKey = m.KeyName);

        vm.HandleAddWord();

        Check.That(vm.CurrentInput).IsEqualTo("zeppelin");
        Check.That(sentKey).IsNull();
        Check.That(sentSpecialKey).IsNull();
    }
}
