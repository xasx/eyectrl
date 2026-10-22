namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// The suggestion bar in and around a password field. While a password field has focus the bar is
/// EMPTY (completions of a password's prefix would be shown on screen and fed to the language model),
/// a list rendered BEFORE the focus moved there is cleared and a pick on it does nothing, the bar comes
/// back once the focus leaves, and the first pick after leaving never logs the password's length (E1
/// reads the mirror before the pick, so the sensitivity guard has to run first).
///
/// <para>Real window, never shown: the off-screen <c>WS_POPUP</c> HwndSource pattern of
/// <see cref="KeyboardSessionEndBarRefreshTests"/>.</para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPasswordBarTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f)
        { lock (Lines) Lines.Add(f(s, x)); }
    }

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new() { ["hello"] = 1000, ["help"] = 900, ["helmet"] = 800 };
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class Probe : IDisposable
    {
        public KeyboardViewModel Vm = null!;
        public KeyboardWindow Window = null!;
        public FakeSensitive Sensitive = new();
        public Capture Log = new();
        public int PicksSent;
        public FrameworkElement Content = null!;
        private HwndSource _source = null!;

        public static Probe Open()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            var p = new Probe();
            var messenger = new WeakReferenceMessenger();
            messenger.Register<Probe, PredictionSelectedMessage>(p, (t, _) => t.PicksSent++);
            var settings = new EyeCtrlSettings();
            settings.Keyboard.SmartCapitalizationEnabled = false;
            p.Vm = new KeyboardViewModel(settings, messenger, logger: p.Log, sensitiveContext: p.Sensitive);
            p.Vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(new InMemoryDictProvider(), "QWERTY")));
            p.Window = new KeyboardWindow();
            p.Window.SetViewModel(p.Vm);
            p.Window.LoadLayout(KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json")), 1.0,
                keyDwellMs: 450, predictionDwellMs: 500, cooldownMs: 400);
            p.Window.SetSettings(settings);
            p.Content = (FrameworkElement)p.Window.Content;
            p.Window.Content = null;
            p._source = new HwndSource(new HwndSourceParameters("pw-bar-tests")
            {
                WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
            });
            var factor = 1.0 / p._source.CompositionTarget!.TransformToDevice.M11;
            p.Content.LayoutTransform = new ScaleTransform(factor, factor);
            p._source.RootVisual = p.Content;
            p.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            p.Content.Arrange(new Rect(p.Content.DesiredSize));
            p.Content.UpdateLayout();
            return p;
        }

        public List<string> Rendered() =>
            ((IEnumerable<PredictionResult>)typeof(KeyboardWindow)
                .GetField("_renderedPredictions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window)!)
            .Select(r => r.Word).ToList();

        /// <summary>"hel" in a normal field, the bar refreshed: hello / help / helmet on screen.</summary>
        public void TypeHelInANormalField()
        {
            foreach (var ch in new[] { "h", "e", "l" }) { Vm.ActivateKey(ch, null); Window.UpdatePredictions(); }
            Content.UpdateLayout();
            Check.WithCustomMessage("arrange: the normal bar must offer completions").That(Rendered()).Contains("hello");
        }

        public void Gaze(long tUs) => Window.UpdateGaze(new PhysicalPoint(-5000, -5000), tUs);

        public void Dispose() { _source.Dispose(); Window.Close(); }
    }

    [StaFact]
    public void AListRenderedBeforeThePasswordField_IsClearedOnTheNextGazeSample()
    {
        var p = Probe.Open();
        try
        {
            p.TypeHelInANormalField();

            p.Sensitive.IsSensitiveActive = true;        // focus moved into a password field: no event, no key
            p.Gaze(1_000_000);

            Check.WithCustomMessage("stale bar: " + string.Join(",", p.Rendered())).That(p.Rendered()).IsEmpty();
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void APickOnAStaleSlot_InAPasswordField_DoesNothing()
    {
        var p = Probe.Open();
        try
        {
            p.TypeHelInANormalField();
            p.Sensitive.IsSensitiveActive = true;

            p.Window.OnPredictionDwelled("pred_0");

            Check.WithCustomMessage("a pick was sent into the password field").That(p.PicksSent).IsEqualTo(0);
            Check.That(p.Rendered()).IsEmpty();
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void WhileTyping_InAPasswordField_TheBarStaysEmpty()
    {
        var p = Probe.Open();
        try
        {
            p.Sensitive.IsSensitiveActive = true;
            foreach (var ch in new[] { "h", "e", "l" }) { p.Vm.ActivateKey(ch, null); p.Window.UpdatePredictions(); }
            p.Content.UpdateLayout();

            Check.WithCustomMessage("bar: " + string.Join(",", p.Rendered())).That(p.Rendered()).IsEmpty();
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void AfterLeavingThePasswordField_TheBarComesBack_AndNothingOfThePasswordIsInIt()
    {
        var p = Probe.Open();
        try
        {
            p.Sensitive.IsSensitiveActive = true;
            foreach (var ch in new[] { "h", "e", "l" }) { p.Vm.ActivateKey(ch, null); p.Window.UpdatePredictions(); }
            p.Gaze(1_000_000);

            p.Sensitive.IsSensitiveActive = false;       // gaze-click on "Log in"
            p.Gaze(2_000_000);                           // the first sample after: the bar is rebuilt

            Check.WithCustomMessage("the password prefix must be gone").That(p.Vm.CurrentInput).IsEqualTo("");
            Check.WithCustomMessage("bar after leaving: " + string.Join(",", p.Rendered()))
                .That(p.Rendered().Any(w => w.StartsWith("hel", StringComparison.OrdinalIgnoreCase))).IsFalse();

            foreach (var ch in new[] { "h", "e", "l" }) { p.Vm.ActivateKey(ch, null); p.Window.UpdatePredictions(); }
            Check.WithCustomMessage("the normal bar must work again in the next field")
                .That(p.Rendered()).Contains("hello");
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheFirstPickAfterLeavingAPasswordField_DoesNotLogThePasswordsLength()
    {
        var p = Probe.Open();
        try
        {
            p.TypeHelInANormalField();                   // a list on screen (rendered before the password field)
            p.Sensitive.IsSensitiveActive = true;
            p.Vm.ActivateKey("x", null);                 // one password character, no bar refresh in between
            p.Sensitive.IsSensitiveActive = false;       // and the focus leaves by gaze

            p.Window.OnPredictionDwelled("pred_0");      // the stale slot is picked

            var pick = p.Log.Lines.SingleOrDefault(l => l.StartsWith("KB-E1 pick ", StringComparison.Ordinal));
            Check.WithCustomMessage("arrange: the pick must have happened").That(pick).IsNotNull();
            Check.WithCustomMessage("the first pick after the password logs its length: " + pick)
                .That(pick!.Contains(" prefixlen=0 ", StringComparison.Ordinal)).IsTrue();
        }
        finally { p.Dispose(); }
    }
}
