namespace EyeCtrl.App.Tests.Keyboard;

using System;
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
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The prediction bar is a fixed row: one slot per configured suggestion, always present in text
/// mode, empty slots as quiet placeholders. The window must not resize - and no key may move -
/// when the suggestion count changes while typing.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionBarStabilityTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int CooldownMs = 0;
    private const int PredictionDwellMs = 500;

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content,
        HwndSource Source, WeakReferenceMessenger Messenger, DwellDetector PredictionDetector)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static Dictionary<string, PhysicalPoint> KeyCentres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, PhysicalRect> KeyBoxes(KeyboardWindow w) =>
        (Dictionary<string, PhysicalRect>)typeof(KeyboardWindow)
            .GetField("_keyScreenBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Probe Open(Dictionary<string, int> words)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        settings.Keyboard.MaxSuggestions = 5;

        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY")));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.SetSettings(settings);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-stability-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: the key boxes and prediction targets would never publish, "
                + "and the stability assertions would pass vacuously.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source, messenger, PredictionDetector(w));
    }

    private static void Layout(Probe probe, string input)
    {
        probe.Vm.CurrentInput = input;
        probe.Window.UpdatePredictions();
        probe.Content.UpdateLayout();
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
    }

    private static List<string> RenderedWords(KeyboardWindow w) =>
        w.PredictionBar.Children.Cast<Border>().Select(b => CellText(b.Child))
            .Where(text => text.Length > 0).ToList();

    private static string CellText(UIElement? element) => element switch
    {
        TextBlock tb when !string.IsNullOrEmpty(tb.Text) => tb.Text,
        Border border => CellText(border.Child),
        Panel panel => panel.Children.Cast<UIElement>()
            .Select(CellText).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
        ContentControl content when content.Content is UIElement child => CellText(child),
        _ => ""
    };

    private static List<DwellTarget> RegisteredTargets(DwellDetector detector) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<string, DwellTarget>)typeof(DwellDetector)
            .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(detector)!).Values.ToList();

    private static void Feed(Probe probe, PhysicalPoint at, long fromUs, long toUs, long stepUs = 30_000)
    {
        Check.That((toUs - fromUs) % stepUs).IsEqualTo(0);
        for (var t = fromUs; t <= toUs; t += stepUs)
            probe.Window.UpdateGaze(at, t);
    }

    private static PhysicalPoint ScreenCentre(FrameworkElement element)
    {
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new PhysicalPoint(p.X, p.Y);
    }

    private static (double Width, double Height) ContentSize(Probe probe) =>
        (probe.Content.ActualWidth, probe.Content.ActualHeight);

    [StaFact]
    public void Bar_KeepsTheWindowAndEveryKeyStill_ForZeroOneFiveAndOneVeryLongSuggestion()
    {
        var longWord = new string('a', 60);
        var probe = Open(new Dictionary<string, int>
        {
            ["hello"] = 1000, ["help"] = 900, ["helm"] = 800, ["helium"] = 700, ["helmet"] = 600,
            ["zebra"] = 10, [longWord] = 1_000_000,
        });
        try
        {
            var scenarios = new (string Label, string Input, int Expected)[]
            {
                ("0 suggestions", "mmm", 0),
                ("one 60-character suggestion", "aaa", 1),
                ("5 suggestions", "hel", 5),
            };

            var referenceSize = default((double Width, double Height));
            Dictionary<string, PhysicalRect>? referenceKeys = null;
            foreach (var scenario in scenarios)
            {
                Layout(probe, scenario.Input);
                Check.WithCustomMessage($"{scenario.Label}: rendered {string.Join("|", RenderedWords(probe.Window))}")
                    .That(RenderedWords(probe.Window)).HasSize(scenario.Expected);
                Check.WithCustomMessage($"{scenario.Label}: the bar must always hold MaxSuggestions slots")
                    .That(probe.Window.PredictionBar.Children.Count).IsEqualTo(5);

                var size = ContentSize(probe);
                var keys = KeyBoxes(probe.Window);
                if (referenceKeys == null)
                {
                    referenceSize = size;
                    referenceKeys = new Dictionary<string, PhysicalRect>(keys);
                    continue;
                }

                Check.WithCustomMessage($"{scenario.Label}: the keyboard width changed with the suggestions")
                    .That(size.Width).IsEqualTo(referenceSize.Width);
                Check.WithCustomMessage($"{scenario.Label}: the keyboard height changed with the suggestions")
                    .That(size.Height).IsEqualTo(referenceSize.Height);
                Check.That(keys).HasSize(referenceKeys.Count);
                foreach (var (id, box) in referenceKeys)
                {
                    Check.WithCustomMessage($"{scenario.Label}: key {id} moved")
                        .That(keys[id]).IsEqualTo(box);
                }
            }
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void EmptySlots_RegisterNoDwellTarget()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            Layout(probe, "mmm");
            Check.That(probe.Window.PredictionBar.Children.Count).IsEqualTo(5);
            Check.WithCustomMessage("An empty bar registered dwell targets")
                .That(RegisteredTargets(probe.PredictionDetector)).IsEmpty();

            Layout(probe, "hell");
            Check.That(RenderedWords(probe.Window)).HasSize(1);
            var targets = RegisteredTargets(probe.PredictionDetector);
            Check.WithCustomMessage("Only the filled slot may register a target")
                .That(targets).HasSize(1);
            Check.That(targets[0].Id).IsEqualTo("pred_0");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void FilledSlot_DwellSelectsItsRenderedWord()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000, ["help"] = 900, ["helm"] = 800 });
        try
        {
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            Layout(probe, "hel");
            var words = RenderedWords(probe.Window);
            Check.That(words.Count).IsStrictlyGreaterThan(1);

            var secondCell = (FrameworkElement)probe.Window.PredictionBar.Children[1];
            Feed(probe, ScreenCentre(secondCell), 30_000, 540_000);

            Check.That(inserted).IsEqualTo(words[1]);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void PredictionDisabled_CollapsesTheBar_AndKeepsItCollapsedWhileTyping()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000, ["help"] = 900 });
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Keyboard.PredictionEnabled = false;
            probe.Window.SetSettings(settings);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();

            Check.That(probe.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Collapsed);

            Layout(probe, "hel");
            Check.That(probe.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void EmojiMode_KeepsTheBarCollapsed()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            probe.Vm.IsEmojiMode = true;
            Layout(probe, "hel");

            Check.That(probe.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally { probe.Dispose(); }
    }
}
