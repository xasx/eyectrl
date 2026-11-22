namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// F7's async half: a boundary render reserves slot 0 for the language model's next word, the
/// conventional words move to slots 1.., and a late answer fills slot 0 IN PLACE — never under a
/// dwell in progress, never over a filled slot, never after the boundary moved on, and never by
/// re-rendering the bar.
///
/// <para>
/// The window is never shown: the probe hosts it in an off-screen <c>WS_POPUP</c> HwndSource, like
/// the prediction-exclusion tests, so nothing paints on the owner's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardNextWordSlotTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int CooldownMs = 400;
    private const int PredictionDwellMs = 500;

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

    /// <summary>Captures the E1 lines the keyboard writes (the wordend read-out needs them).</summary>
    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add(formatter(state, exception));
        }

        public string[] Of(string eventName)
        {
            lock (_gate)
                return _lines.Where(l => l.StartsWith($"KB-E1 {eventName} ", StringComparison.Ordinal)).ToArray();
        }
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split(' ').Skip(2).Where(t => t.Contains('='))
            .Select(t => t.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    /// <summary>Consume-once next-word predictor: null means "still running", exactly like the real one.</summary>
    private sealed class FakeLm : ILanguageModelPredictor
    {
        private PredictionResult? _pending;

        public List<string> NextWordCalls { get; } = [];
        public int NextWordPolls { get; private set; }
        public bool IsAvailable => true;

        public void Predict(string context, string? prefix = null) { }

        public void PredictNextWord(string context) => NextWordCalls.Add(context);

        public void Complete(string word) =>
            _pending = new PredictionResult(word, 40, PredictionType.LMSuggestion, 0.4);

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            NextWordPolls++;
            var result = _pending;
            _pending = null;
            return result is null ? null : [result];
        }

        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content,
        HwndSource Source, DwellDetector PredictionDetector, FakeLm Lm, PredictionOrchestrator Orchestrator,
        FakeSensitive Sensitive, Capture Log)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static List<string> RenderedWords(KeyboardWindow w) =>
        w.PredictionBar.Children.Cast<Border>().Select(b => CellText(b.Child)).ToList();

    private static string CellText(UIElement? element) => element switch
    {
        TextBlock tb when !string.IsNullOrEmpty(tb.Text) => tb.Text,
        Border border => CellText(border.Child),
        Panel panel => panel.Children.Cast<UIElement>()
            .Select(CellText).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
        ContentControl content when content.Content is UIElement child => CellText(child),
        _ => ""
    };

    private static Probe Open(Action<EyeCtrlSettings>? configure = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        configure?.Invoke(settings);

        var sensitive = new FakeSensitive();
        var log = new Capture();
        var vm = new KeyboardViewModel(settings, new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger(),
            logger: log, sensitiveContext: sensitive);
        var lm = new FakeLm();
        var orchestrator = new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(new Dictionary<string, int>
            {
                ["ist"] = 100, ["steht"] = 90, ["leer"] = 80, ["hier"] = 70,
            }), "QWERTY"),
            lmPredictor: lm);
        vm.SetOrchestrator(orchestrator);
        vm.IsVisible = true;

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-nextword-probe")
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
                "No PresentationSource: UpdateGaze's geometry reads would all fail and every "
                + "assertion here would pass VACUOUSLY.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source, PredictionDetector(w), lm, orchestrator, sensitive, log);
    }

    /// <summary>Brings the view model to a word boundary: "Haus" typed, then Space.</summary>
    private static void TypeIntoTheBoundary(Probe probe, string word = "Haus")
    {
        var e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext()) probe.Vm.ActivateKey((string)e.Current, null);
        probe.Vm.HandleSpace();
    }

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

    private static PhysicalPoint KeyAreaPoint(KeyboardWindow w)
    {
        var tl = w.KeyRows.PointToScreen(new Point(0, 0));
        var br = w.KeyRows.PointToScreen(new Point(w.KeyRows.ActualWidth, w.KeyRows.ActualHeight));
        return new PhysicalPoint((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
    }

    private static long BarRenderedMs(KeyboardWindow w) =>
        (long)typeof(KeyboardWindow)
            .GetField("_barRenderedMs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    [StaFact]
    public void BoundaryRender_ReservesSlotZero_AndStartsTheWordsAtSlotOne()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var words = RenderedWords(probe.Window);
            Check.WithCustomMessage($"bar: {string.Join("|", words)}")
                .That(words[0]).IsEmpty();
            Check.WithCustomMessage($"bar: {string.Join("|", words)}")
                .That(words[1]).IsEqualTo("ist");
            Check.WithCustomMessage($"next-word calls: {string.Join("|", probe.Lm.NextWordCalls)}")
                .That(probe.Lm.NextWordCalls).ContainsExactly("Haus");

            var targets = probe.PredictionDetector.Targets.Select(t => t.Id).ToList();
            Check.WithCustomMessage($"targets: {string.Join("|", targets)}")
                .That(targets).Not.Contains("pred_0");
            Check.WithCustomMessage($"targets: {string.Join("|", targets)}")
                .That(targets).Contains("pred_1");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void WithTheSettingOff_TheBoundaryRendersFromSlotZero_AsToday()
    {
        using var probe = Open(s => s.Keyboard.NextWordPredictionEnabled = false);
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();

            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEqualTo("ist");
            Check.That(probe.Lm.NextWordCalls).IsEmpty();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void WhenTheAnswerArrives_SlotZeroIsFilledOnTheNextSample()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            probe.Lm.Complete("leer");
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);
            probe.Content.UpdateLayout();

            var words = RenderedWords(probe.Window);
            Check.WithCustomMessage($"bar: {string.Join("|", words)}")
                .That(words[0]).IsEqualTo("leer");
            Check.That(probe.Vm.NextWordSlotReserved).IsFalse();

            // The new cell becomes dwellable like any other: its target appears on the next
            // geometry pass (an unarranged cell is never registered - the latch's whole point).
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 60_000);
            var targets = probe.PredictionDetector.Targets.Select(t => t.Id).ToList();
            Check.WithCustomMessage($"targets: {string.Join("|", targets)}")
                .That(targets).Contains("pred_0");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void WhileASlotHasDwellProgressInFlight_TheAnswerWaits()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            // A dwell is in progress on the conventional word in slot 1.
            var slotOne = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[1]);
            var progress = new List<double>();
            probe.PredictionDetector.TargetProgress += (_, p) => progress.Add(p);
            Feed(probe, slotOne, 30_000, 180_000);
            Check.WithCustomMessage("arrange: the dwell never started").That(progress.LastOrDefault())
                .IsStrictlyGreaterThan(0.0);

            probe.Lm.Complete("leer");
            probe.Window.UpdateGaze(slotOne, 210_000);

            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEmpty();

            // Leaving the bar resets that dwell; the next sample may fill the reserved slot.
            Feed(probe, KeyAreaPoint(probe.Window), 240_000, 270_000);
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 300_000);
            probe.Content.UpdateLayout();

            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEqualTo("leer");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void WhenTheBoundaryMovedOn_TheAnswerIsNeverShown()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            // The user keeps typing: the boundary this request belongs to is gone.
            probe.Vm.ActivateKey("h", null);
            probe.Lm.Complete("leer");
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);

            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEmpty();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AFilledSlotZero_IsNeverRewritten_AndNothingElseOnTheBarChanges()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var before = probe.Window.PredictionBar.Children.Cast<Border>().ToList();
            var renderedMs = BarRenderedMs(probe.Window);

            probe.Lm.Complete("leer");
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);
            probe.Content.UpdateLayout();

            // A second answer for the same slot arrives after it was filled: it must be ignored.
            probe.Lm.Complete("hier");
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 60_000);

            var after = probe.Window.PredictionBar.Children.Cast<Border>().ToList();
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEqualTo("leer");

            // No whole-bar re-render: the neighbouring cells are the very same instances and the
            // E1 render clock did not restart (a re-render would replace all of them).
            for (var i = 1; i < after.Count; i++)
                Check.WithCustomMessage($"cell {i} was replaced: the bar re-rendered")
                    .That(ReferenceEquals(before[i], after[i])).IsTrue();
            Check.WithCustomMessage("the bar re-rendered: the E1 render clock restarted")
                .That(BarRenderedMs(probe.Window)).IsEqualTo(renderedMs);
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// F16: the late slot-0 fill is a render of that slot, so a word typed anyway is reported as
    /// offered from the word boundary (firstprefix=0) — the case the whole measurement exists for.
    /// </summary>
    [StaFact]
    public void ALateNextWordFill_IsAFirstPrefixZeroBarHit_ForTheWordTypedAnyway()
    {
        using var probe = Open();
        try
        {
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            probe.Lm.Complete("leer");
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);   // fills slot 0
            probe.Content.UpdateLayout();
            Check.That(RenderedWords(probe.Window)[0]).IsEqualTo("leer");

            foreach (var letter in "leer")
            {
                probe.Vm.ActivateKey(letter.ToString(), null);
                probe.Window.UpdatePredictions();
            }
            probe.Vm.HandleSpace();

            // The first wordend is "Haus" (typed before this test's boundary); the one under test
            // is the word typed after the fill.
            var f = Fields(probe.Log.Of("wordend").Last());
            Check.That(f["how"]).IsEqualTo("typed");
            Check.That(f["len"]).IsEqualTo("4");
            Check.That(f["typed"]).IsEqualTo("4");
            Check.That(f["onbar"]).IsEqualTo("1");
            Check.That(f["firstprefix"]).IsEqualTo("0");
            Check.That(f["firstslot"]).IsEqualTo("0");
        }
        finally { probe.Dispose(); }
    }
}
