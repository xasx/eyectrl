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
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The prediction bar's per-slot leave-before-re-arm latch. A pick re-renders the bar under the
/// resting gaze, so the slot that just fired can hold a new suggestion a moment later; without a
/// latch one long look inserts one word per cooldown cycle. Modeled on the toolbar Pause latch:
/// per slot, a 120 ms continuous leave of the slot's LIVE hit area, with every sample (keys
/// included) advancing the clock.
///
/// <para>
/// Same off-screen HwndSource probe as <see cref="KeyboardPredictionClickTests"/>: no Show(), no
/// visible window, no audio device. Samples are driven through the real <c>UpdateGaze</c> path so
/// the latch, the detector's target filter and the after-pick re-render all run.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionRearmLatchTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    private const int KeyDwellMs = 450;
    private const int CooldownMs = 400;
    private const int PredictionDwellMs = 500;
    private const long StepUs = 33_000; // ~30 Hz tracker cadence

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

        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY"), lmPredictor: null));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-rearm-probe")
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
                "No PresentationSource: UpdateGaze's gate would catch PointToScreen's throw and "
                + "every assertion would pass VACUOUSLY. Fix the environment or the fixture.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source, messenger, PredictionDetector(w));
    }

    /// <summary>
    /// Learns ("hello"  "world") and ("hello"  "there") twice each, then leaves "hel" typed so
    /// the bar offers "hello" in slot 0. After picking it, the context "hello" offers world/there
    /// again - the non-empty after-pick list that makes the resting gaze pick twice without the
    /// latch.
    /// </summary>
    private static void SeedBarAndPairs(Probe probe)
    {
        void Commit(string word)
        {
            probe.Vm.CurrentInput = word;
            probe.Vm.HandleSpace();
        }

        for (var round = 0; round < 2; round++)
        {
            Commit("hello");
            Commit("world");
            Commit("hello");
            Commit("there");
        }

        probe.Vm.CurrentInput = "hel";
        probe.Window.UpdatePredictions();
        probe.Content.UpdateLayout();
    }

    private static void PlaceTargets(Probe probe)
        => probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

    private static PhysicalPoint SlotCenter(Probe probe, string id)
        => probe.PredictionDetector.Targets.First(target => target.Id == id).Center;

    private static PhysicalPoint KeyPoint(KeyboardWindow window)
    {
        var positions = (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;
        return positions.Values.First();
    }

    private static void Feed(Probe probe, PhysicalPoint point, long fromUs, long toUs)
    {
        for (var ts = fromUs; ts <= toUs; ts += StepUs)
        {
            probe.Window.UpdateGaze(point, ts);
            // The real UI thread runs a layout pass between gaze samples. The probe has no
            // dispatcher loop, so run it here or re-rendered cells never acquire a size and
            // UpdatePredictionTargetPositions registers nothing after a pick.
            probe.Content.UpdateLayout();
        }
    }

    private static List<string> CollectPicks(Probe probe)
    {
        var picks = new List<string>();
        probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => picks.Add(m.Word));
        return picks;
    }

    [StaFact]
    public void ThreeSecondHoldOnThePickedSlot_PicksExactlyOnce()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            Feed(probe, slot, 100_000, 3_100_000);

            Check.WithCustomMessage("the pick did not happen - the fixture never produced a pick")
                .That(picks).HasSize(1);
            Check.WithCustomMessage(
                    "the after-pick bar was empty, so a second pick was impossible and the gate "
                    + "assertion would be vacuous")
                .That(probe.Vm.GetSuggestionsWithContext()).Not.IsEmpty();
            Check.WithCustomMessage(
                    "the after-pick bar registered no dwell target - the gate would be vacuous")
                .That(probe.PredictionDetector.Targets.Select(target => target.Id)).Contains("pred_0");
            Check.WithCustomMessage("a latched slot must not accumulate dwell")
                .That(probe.PredictionDetector.GetProgress("pred_0")).IsEqualTo(0);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void LeavingOntoTheKeysFor120ms_ReArmsTheSlot()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");
            var keys = KeyPoint(probe.Window);

            Feed(probe, slot, 100_000, 800_000);   // first pick at ~628 ms
            Feed(probe, keys, 833_000, 1_000_000); // >= 120 ms continuously outside the slot
            Feed(probe, slot, 1_033_000, 2_100_000);

            Check.WithCustomMessage("returning after a real leave did not re-arm the slot")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TwoStraySamplesOff_TheSlotStaysLatched()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");
            var keys = KeyPoint(probe.Window);

            Feed(probe, slot, 100_000, 800_000);   // first pick at ~628 ms
            Feed(probe, keys, 833_000, 866_000);   // 66 ms off: not a leave
            Feed(probe, slot, 899_000, 2_000_000);

            Check.WithCustomMessage("a 66 ms excursion cleared the 120 ms latch")
                .That(picks).HasSize(1);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AnotherSlot_CanBePickedWhileTheFirstIsLatched()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var first = SlotCenter(probe, "pred_0");

            Feed(probe, first, 100_000, 700_000); // first pick at ~628 ms
            var second = SlotCenter(probe, "pred_1");
            Check.WithCustomMessage("the after-pick bar has only one slot - the fixture is wrong")
                .That(second).IsNotEqualTo(first);
            Feed(probe, second, 733_000, 2_300_000); // dwell slot 1; slot 0 stays latched

            Check.WithCustomMessage("slot 1 did not fire while slot 0 was latched")
                .That(picks).HasSize(2);
            Check.That(picks[0]).IsEqualTo("hello");
            Check.That(new[] { "world", "there" }).Contains(picks[1]);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void LayoutReload_ClearsTheLatch()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            Feed(probe, slot, 100_000, 800_000); // first pick at ~628 ms

            probe.Window.LoadLayout(ShippedQwerty(), 1.0, KeyDwellMs, PredictionDwellMs, CooldownMs);
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 900_000);
            var reloadedSlot = SlotCenter(probe, "pred_0");
            Feed(probe, reloadedSlot, 933_000, 2_000_000);

            Check.WithCustomMessage("a layout reload left the slot latched across the session")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }
}
