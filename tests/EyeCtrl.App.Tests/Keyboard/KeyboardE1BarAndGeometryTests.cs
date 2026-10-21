namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// The window-side values of E1's lines, which the privacy gate does not read: the bar pick's
/// reading fingerprint (the time from the bar's render to the dwell start, and how many OTHER
/// slots the gaze dwelt on first) and the dock edge on a key line. Driven through the real window
/// and the real gaze entry point, hosted in the never-shown HwndSource probe.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardE1BarAndGeometryTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int PredictionDwellMs = 500;
    private const int KeyDwellMs = 450;

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

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source, Capture Log)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static Probe Open()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var log = new Capture();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger(), logger: log);
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(
            new InMemoryDictProvider(new Dictionary<string, int>
            {
                ["hello"] = 1000, ["help"] = 900, ["helmet"] = 800,
            }), "QWERTY")));

        var layout = KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(layout, 1.0, keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: 400);
        w.SetSettings(settings);

        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var source = new HwndSource(new HwndSourceParameters("kb-e1-bar-probe")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: the bar's hit areas would be unreachable and the pick would "
                + "never happen, leaving every assertion here vacuous. Fix the fixture.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();
        return new Probe(w, vm, content, source, log);
    }

    private static PhysicalPoint ScreenCentre(FrameworkElement element)
    {
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new PhysicalPoint(p.X, p.Y);
    }

    /// <summary>The id (as the window mints it) of the plain key whose output is <paramref name="output"/>.</summary>
    private static string LetterId(Probe probe, string output)
    {
        var layout = KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));
        var rows = layout.SubLayouts[probe.Vm.ActiveSubLayoutId].Rows;
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                if (string.IsNullOrEmpty(rows[r][c].SpecialAction) && rows[r][c].Output == output)
                    return $"key_{r}_{c}";
        throw new InvalidOperationException($"no key outputs '{output}'");
    }

    private static long Feed(Probe probe, PhysicalPoint at, long fromUs, long durationUs)
    {
        var t = fromUs;
        for (; t <= fromUs + durationUs; t += 30_000)
            probe.Window.UpdateGaze(at, t);
        return t;
    }

    [StaFact]
    public void ABarPick_LogsTheSlotSourceLengthsAndTheReadingFingerprint_NeverTheWord()
    {
        var probe = Open();
        try
        {
            probe.Vm.ActivateKey("h", null);
            probe.Vm.ActivateKey("e", null);
            probe.Window.UpdatePredictions();                  // the bar renders: the render clock starts
            probe.Content.UpdateLayout();
            Check.That(probe.Window.PredictionBar.Children.Count).IsStrictlyGreaterThan(2);

            Thread.Sleep(150);                                  // the render-to-dwell interval to measure

            var cell0 = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[0]);
            var cell2 = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[2]);

            // A glance at slot 2 (visited, not picked), then the dwell that completes slot 0.
            var t = Feed(probe, cell2, 1_000_000, 120_000);
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), t); t += 30_000;
            Feed(probe, cell0, t, PredictionDwellMs * 1000L + 60_000);

            var pick = probe.Log.Of("pick").Single();
            Check.That(pick).StartsWith("KB-E1 pick slot=0 src=dict prefixlen=2 inslen=5 sincekey=-1 sincepick=-1 renderms=");
            Check.That(pick).EndsWith(" visited=1");
            var renderMs = int.Parse(pick.Split(' ').Single(p => p.StartsWith("renderms=", StringComparison.Ordinal))[9..]);
            Check.WithCustomMessage($"renderms={renderMs}: the dwell began after the 150 ms wait, so it cannot be less.")
                .That(renderMs).IsStrictlyGreaterThan(100);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ADirectDwellOnTheFirstSlot_HasNoOtherSlotsVisited()
    {
        var probe = Open();
        try
        {
            probe.Vm.ActivateKey("h", null);
            probe.Vm.ActivateKey("e", null);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();

            Feed(probe, ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[1]),
                1_000_000, PredictionDwellMs * 1000L + 60_000);

            var pick = probe.Log.Of("pick").Single();
            Check.That(pick).StartsWith("KB-E1 pick slot=1 src=dict ");
            Check.That(pick).EndsWith(" visited=0");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AKeyLine_CarriesTheWindowsDockEdge()
    {
        var probe = Open();
        try
        {
            probe.Window.SetScreenEdge("top");
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(0, 0), 0);                       // collect the centres
            var keys = (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
                .GetField("_keyScreenPositions", System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)!.GetValue(probe.Window)!;
            var letterId = LetterId(probe, "a");
            var centre = keys[letterId];

            Feed(probe, centre, 1_000_000, KeyDwellMs * 1000L + 60_000);

            var key = probe.Log.Of("key").First();
            Check.That(key).Contains(" edge=top ");
            Check.That(key).Contains(" cls=letter ");
        }
        finally { probe.Dispose(); }
    }
}
