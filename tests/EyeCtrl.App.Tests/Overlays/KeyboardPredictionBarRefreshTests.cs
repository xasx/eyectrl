namespace EyeCtrl.App.Tests.Overlays;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Pins the effect the settings-apply wiring relies on (review round 1, finding 1): the
/// prediction bar is built from whatever <see cref="KeyboardViewModel.GetSuggestionsWithContext"/>
/// returns at redraw time, so a lowered MaxSuggestions redraws to fewer LIVE cells — no stale
/// cells left dwellable with a completed dwell that silently does nothing. The wiring itself
/// (SettingsUiCoordinator.OnSettingsChanged → UpdatePredictions) is one untested-inline line in a
/// method whose body has never been coordinator-testable; this window test is the headless half
/// of the evidence, the other half is code review.
/// </summary>
public class KeyboardPredictionBarRefreshTests
{
    private sealed class SixWordHelProvider : EyeCtrl.Core.Prediction.IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words = new()
        {
            { "hello", 100 }, { "help", 90 }, { "helm", 80 },
            { "helium", 70 }, { "helmet", 60 }, { "helix", 50 }
        };
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    [StaFact]
    public void UpdatePredictions_AfterLoweringMaxSuggestions_DropsTheStaleTailCells()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new SixWordHelProvider())));
        vm.CurrentInput = "hel";

        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.UpdatePredictions();
        Check.That(window.PredictionBar.Children.Count).IsEqualTo(5);

        // The mid-session change: same window, same input, only the setting moved. The bar must
        // rebuild to the new count — the redraw is what retires the now-unreachable targets.
        settings.Keyboard.MaxSuggestions = 2;
        window.UpdatePredictions();
        Check.That(window.PredictionBar.Children.Count).IsEqualTo(2);
    }
}
