namespace EyeCtrl.ViewModels.Tests;

using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// F7's boundary switches at the view-model seam: a Space or a pick leaves the boundary state and
/// asks the model for the NEXT word with the committed mirror text; Enter, a typed prefix, a
/// sensitive field and the owner's setting each keep that request from happening.
/// </summary>
public class KeyboardNextWordTests
{
    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class InMemoryDictProvider(Dictionary<string, int> words) : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new(words);
        public void Save(Dictionary<string, int> w) { }
    }

    private sealed class FakeLm : ILanguageModelPredictor
    {
        private PredictionResult? _pending;

        public List<string> NextWordCalls { get; } = [];
        public List<(string Context, string? Prefix)> CompletionCalls { get; } = [];
        public bool IsAvailable => true;

        public void Predict(string context, string? prefix = null) => CompletionCalls.Add((context, prefix));

        public void PredictNextWord(string context) => NextWordCalls.Add(context);

        public void Complete(string word) =>
            _pending = new PredictionResult(word, 40, PredictionType.LMSuggestion, 0.4);

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            var result = _pending;
            _pending = null;
            return result is null ? null : [result];
        }

        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public KeyboardViewModel Vm { get; }
        public FakeSensitive Sensitive { get; } = new();
        public FakeLm Lm { get; } = new();

        public Rig(Action<EyeCtrlSettings>? configure = null)
        {
            var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
            configure?.Invoke(settings);

            Vm = new KeyboardViewModel(settings, new WeakReferenceMessenger(), sensitiveContext: Sensitive);
            Vm.SetOrchestrator(new PredictionOrchestrator(
                new WordPredictor(new InMemoryDictProvider(new Dictionary<string, int>
                {
                    ["haus"] = 100, ["steht"] = 90, ["leer"] = 80, ["hier"] = 70,
                }), "QWERTY"),
                lmPredictor: Lm));
            Vm.IsVisible = true;
        }

        public void Type(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) Vm.ActivateKey((string)e.Current, null);
        }

        public void Dispose() { }
    }

    [Fact]
    public void AfterASpace_TheLmIsAskedForTheNextWord_WithTheCommittedText()
    {
        using var rig = new Rig();
        rig.Type("Haus");
        rig.Vm.HandleSpace();

        rig.Vm.GetSuggestionsWithContext();

        Check.WithCustomMessage($"next-word calls: {string.Join("|", rig.Lm.NextWordCalls)}")
            .That(rig.Lm.NextWordCalls).ContainsExactly("Haus");
        Check.That(rig.Lm.CompletionCalls).IsEmpty();
        Check.That(rig.Vm.NextWordSlotReserved).IsTrue();
    }

    [Fact]
    public void WithTheSettingOff_TheBoundaryAsksNothingAndReservesNothing()
    {
        using var rig = new Rig(s => s.Keyboard.NextWordPredictionEnabled = false);
        rig.Type("Haus");
        rig.Vm.HandleSpace();

        rig.Vm.GetSuggestionsWithContext();

        Check.That(rig.Lm.NextWordCalls).IsEmpty();
        Check.That(rig.Vm.NextWordSlotReserved).IsFalse();
    }

    [Fact]
    public void AfterEnter_NoNextWordIsAsked()
    {
        using var rig = new Rig();
        // A committed word first, so the context really exists before Enter clears it.
        rig.Type("Haus");
        rig.Vm.HandleSpace();
        rig.Type("geht");
        rig.Vm.HandleEnter();

        rig.Vm.GetSuggestionsWithContext();

        Check.That(rig.Lm.NextWordCalls).IsEmpty();
        Check.That(rig.Vm.NextWordSlotReserved).IsFalse();
    }

    [Fact]
    public void InASensitiveField_NoNextWordIsAsked_AndAPendingAnswerIsDropped()
    {
        using var rig = new Rig();
        rig.Type("Haus");
        rig.Vm.HandleSpace();
        rig.Vm.GetSuggestionsWithContext();
        Check.That(rig.Vm.NextWordSlotReserved).IsTrue();

        rig.Sensitive.IsSensitiveActive = true;
        rig.Lm.Complete("leer");

        Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();
        Check.That(rig.Vm.NextWordSlotReserved).IsFalse();
        Check.That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
    }

    [Fact]
    public void WhenTheAnswerArrives_TheViewModelHandsItOverOnce()
    {
        using var rig = new Rig();
        rig.Type("Haus");
        rig.Vm.HandleSpace();
        rig.Vm.GetSuggestionsWithContext();

        Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();   // still running
        rig.Lm.Complete("leer");

        var next = rig.Vm.TryTakeNextWordSuggestions();
        Check.That(next).IsNotNull();
        Check.That(next!).HasSize(1);
        Check.That(next![0].Word).IsEqualTo("leer");
        Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();
    }

    [Fact]
    public void AfterATypedPrefix_ThePendingNextWordIsNotHandedOver()
    {
        using var rig = new Rig();
        rig.Type("Haus");
        rig.Vm.HandleSpace();
        rig.Vm.GetSuggestionsWithContext();
        rig.Lm.Complete("leer");

        rig.Type("h");   // the boundary moved on

        Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();
    }
}
