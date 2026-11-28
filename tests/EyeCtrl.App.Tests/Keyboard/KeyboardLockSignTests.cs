namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// D3: a password field (or a poll that could not answer) leaves the bar EMPTY and puts a steady lock sign in
/// its place — slot 0, never a dwell target, never pickable. An empty bar alone also means "nothing suggested
/// yet", so the owner could not tell protection from a wrong detector.
///
/// <para>Real window, never shown: the off-screen <c>WS_POPUP</c> HwndSource pattern of
/// <see cref="KeyboardPasswordBarTests"/>.</para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardLockSignTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const string LockId = "Keyboard_PredictionsLocked";

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePoll : IFocusedPasswordPoll
    {
        public Func<bool?> Read = () => false;
        public bool? ReadIsPassword() => Read();
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
        public FakePoll Poll = new();
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
            p.Vm = new KeyboardViewModel(settings, messenger, sensitiveContext: p.Sensitive, sensitivePoll: p.Poll);
            p.Vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(new InMemoryDictProvider(), "QWERTY")));
            p.Window = new KeyboardWindow();
            p.Window.SetViewModel(p.Vm);
            p.Window.LoadLayout(KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json")), 1.0,
                keyDwellMs: 450, predictionDwellMs: 500, cooldownMs: 400);
            p.Window.SetSettings(settings);
            p.Content = (FrameworkElement)p.Window.Content;
            p.Window.Content = null;
            p._source = new HwndSource(new HwndSourceParameters("lock-sign-tests")
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

        /// <summary>The keyboard shows: the poll's outcome is applied before this returns.</summary>
        public void Show(string at = "show") => Vm.BeginSensitiveCheck(at).GetAwaiter().GetResult();

        public List<string> Rendered() =>
            ((IEnumerable<PredictionResult>)typeof(KeyboardWindow)
                .GetField("_renderedPredictions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window)!)
            .Select(r => r.Word).ToList();

        public List<Border> Cells() => Window.PredictionBar.Children.Cast<Border>().ToList();

        public List<Border> LockCells() =>
            Cells().Where(c => AutomationProperties.GetAutomationId(c) == LockId).ToList();

        public void Gaze(long tUs) => Window.UpdateGaze(new PhysicalPoint(-5000, -5000), tUs);

        public void Dispose() { _source.Dispose(); Window.Close(); }
    }

    [StaFact]
    public void PollSensitive_TheBarIsEmpty_AndSlotZeroShowsTheLock_WhileTheEventPathSaysNot()
    {
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => true;
            p.Show();

            p.Window.UpdatePredictions();
            p.Content.UpdateLayout();

            Check.That(p.Rendered()).IsEmpty();
            var locks = p.LockCells();
            Check.WithCustomMessage("exactly one lock sign").That(locks.Count).IsEqualTo(1);
            Check.WithCustomMessage("in the bar's first slot").That(p.Cells().IndexOf(locks[0])).IsEqualTo(0);
            Check.WithCustomMessage("a sign, not a key: it can never be hit")
                .That(locks[0].IsHitTestVisible).IsFalse();
            Check.WithCustomMessage("it has a drawn glyph").That(locks[0].Child).IsNotNull();
            Check.WithCustomMessage("the localized text is its UIA name")
                .That(AutomationProperties.GetName(locks[0]))
                .IsEqualTo(StringTable.Current.T("overlays.keyboard.predictionsLocked"));
            Check.That(AutomationProperties.GetName(locks[0])).Not.IsEqualTo("");
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void PollUnknown_TheBarIsEmpty_AndTheLockShows()
    {
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => throw new InvalidOperationException("UIA fault");
            p.Show();

            p.Window.UpdatePredictions();

            Check.That(p.Rendered()).IsEmpty();
            Check.That(p.LockCells().Count).IsEqualTo(1);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void PollNotSensitive_NoLock_AndTheWordsAreThere()
    {
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => false;
            p.Show();
            foreach (var ch in new[] { "h", "e", "l" }) { p.Vm.ActivateKey(ch, null); p.Window.UpdatePredictions(); }

            Check.That(p.LockCells()).IsEmpty();
            Check.That(p.Rendered()).Contains("hello");
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheLock_AppearsOnAnAlreadyEmptyBar_WhenThePollSaysPassword_AndGoesWhenANewerPollSaysNot()
    {
        // The bar was empty (nothing typed yet) when the verdict landed, so the "words on screen" test alone
        // would never re-render it: the lock must come from the per-sample refresh.
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => false;
            p.Show();
            p.Window.UpdatePredictions();
            p.Gaze(1_000_000);
            Check.That(p.LockCells()).IsEmpty();

            p.Poll.Read = () => true;
            p.Show("hide");
            p.Gaze(2_000_000);
            Check.WithCustomMessage("the lock appears at the next gaze sample").That(p.LockCells().Count).IsEqualTo(1);

            p.Gaze(3_000_000);
            Check.WithCustomMessage("steady: still exactly one").That(p.LockCells().Count).IsEqualTo(1);

            p.Poll.Read = () => false;
            p.Show();
            p.Gaze(4_000_000);
            Check.WithCustomMessage("a newer poll lifts it").That(p.LockCells()).IsEmpty();
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void AWordRenderedBeforeThePollSaidPassword_IsReplacedByTheLock()
    {
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => false;
            p.Show();
            foreach (var ch in new[] { "h", "e", "l" }) { p.Vm.ActivateKey(ch, null); p.Window.UpdatePredictions(); }
            Check.That(p.Rendered()).Contains("hello");

            p.Poll.Read = () => true;
            p.Show("hide");
            p.Gaze(1_000_000);

            Check.That(p.Rendered()).IsEmpty();
            Check.That(p.LockCells().Count).IsEqualTo(1);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void APickOnTheLockedBar_SendsNothing()
    {
        var p = Probe.Open();
        try
        {
            p.Poll.Read = () => true;
            p.Show();
            p.Window.UpdatePredictions();

            p.Window.OnPredictionDwelled("pred_0");

            Check.That(p.PicksSent).IsEqualTo(0);
        }
        finally { p.Dispose(); }
    }
}
