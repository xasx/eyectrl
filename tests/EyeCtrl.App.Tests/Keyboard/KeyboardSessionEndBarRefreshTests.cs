namespace EyeCtrl.App.Tests.Keyboard;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// A session-ending key (Tab, a <c>key:</c> name, a <c>shortcut:</c>) must refresh the bar, so it stops
/// offering completions of the word the session ended with (R1-d: the VM clears the mirror, the window
/// owns the repaint). Off-screen WS_POPUP HwndSource, never shown (the KeyboardPredictionClickTests
/// pattern).
/// </summary>
[Collection("WpfApplication")]
public class KeyboardSessionEndBarRefreshTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private static List<string> Rendered(KeyboardWindow w) =>
        ((IEnumerable<PredictionResult>)typeof(KeyboardWindow)
            .GetField("_renderedPredictions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!)
        .Select(r => r.Word).ToList();

    [StaTheory]
    [InlineData("qwertz.json", "tab")]
    [InlineData("qwertz.json", "key:")]
    [InlineData("qwertz.json", "shortcut:")]
    public void SessionEndingKey_RefreshesTheBar(string layoutFile, string actionPrefix)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(new() { ["hello"] = 1000 }), "QWERTZ")));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", layoutFile)), 1.0,
            keyDwellMs: 450, predictionDwellMs: 500, cooldownMs: 400);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var source = new HwndSource(new HwndSourceParameters("session-end-bar-refresh")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        try
        {
            var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
            content.LayoutTransform = new ScaleTransform(factor, factor);
            source.RootVisual = content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();

            // Every key of every sub-layout is in _keyDefinitions only for the active grid: find
            // the key with the wanted action in any sub-layout and switch to it first.
            var layout = (KeyboardLayout)typeof(KeyboardViewModel)
                .GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
            string? subId = null;
            foreach (var (id, sub) in layout.SubLayouts)
                if (sub.Rows.SelectMany(r => r).Any(k => k.SpecialAction is { } a
                        && a.StartsWith(actionPrefix, System.StringComparison.OrdinalIgnoreCase)
                        && !a.Contains("printscreen", System.StringComparison.OrdinalIgnoreCase)
                        && !a.Equals("shortcut:Ctrl+C", System.StringComparison.OrdinalIgnoreCase)))
                { subId = id; break; }
            Check.WithCustomMessage($"no key with action {actionPrefix}* in {layoutFile}").That(subId).IsNotNull();
            vm.SwitchSubLayout(subId!);
            content.UpdateLayout();

            var defs = (System.Collections.IDictionary)typeof(KeyboardWindow)
                .GetField("_keyDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
            string? keyId = null;
            foreach (System.Collections.DictionaryEntry e in defs)
                if (((KeyDefinition)e.Value!).SpecialAction is { } a
                    && a.StartsWith(actionPrefix, System.StringComparison.OrdinalIgnoreCase)
                    && !a.Contains("printscreen", System.StringComparison.OrdinalIgnoreCase)
                    && !a.Equals("shortcut:Ctrl+C", System.StringComparison.OrdinalIgnoreCase))
                { keyId = (string)e.Key; break; }
            Check.WithCustomMessage("no key id found in the active grid").That(keyId).IsNotNull();

            vm.CurrentInput = "hel";
            w.UpdatePredictions();
            content.UpdateLayout();
            Check.WithCustomMessage("arrange: the bar must show 'hello' for 'hel'")
                .That(Rendered(w)).Contains("hello");

            typeof(KeyboardWindow).GetMethod("OnKeyDwellCompleted", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(w, [keyId]);
            content.UpdateLayout();

            Check.WithCustomMessage($"{keyId} ended the session (CurrentInput='{vm.CurrentInput}') but the bar still renders [{string.Join(",", Rendered(w))}]")
                .That(Rendered(w)).Not.Contains("hello");
        }
        finally { source.Dispose(); w.Close(); }
    }
}
