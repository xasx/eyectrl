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

    private static KeyboardLayout ShippedQwerty() => ShippedLayout("qwerty.json");

    private static KeyboardLayout ShippedLayout(string file) =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", file));

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Probe Open(Dictionary<string, int> words, string layoutFile = "qwerty.json")
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
        w.LoadLayout(ShippedLayout(layoutFile), 1.0,
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

    private static void Feed(Probe probe, PhysicalPoint point, long fromUs, long toUs, bool layout = true)
    {
        for (var ts = fromUs; ts <= toUs; ts += StepUs)
        {
            probe.Window.UpdateGaze(point, ts);
            if (!layout) continue;
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
            Feed(probe, slot, 899_000, 1_200_000); // back on for > 120 ms: the clock must reset
            Feed(probe, keys, 1_233_000, 1_266_000); // second 66 ms off
            Feed(probe, slot, 1_299_000, 2_300_000);

            Check.WithCustomMessage("two short excursions accumulated into a 120 ms leave")
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
    public void LeavingOntoAnotherSlotFor120ms_ReArmsThePickedSlot()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var first = SlotCenter(probe, "pred_0");

            Feed(probe, first, 100_000, 800_000); // first pick at ~628 ms
            var second = SlotCenter(probe, "pred_1");
            Feed(probe, second, 833_000, 1_000_000); // >= 120 ms on another slot
            Feed(probe, first, 1_033_000, 2_100_000);

            Check.WithCustomMessage("a leave onto another slot did not re-arm the picked slot")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void PickedSlotWithAnEmptyAfterPickBar_ReArmsAfterLeavingOntoTheKeys()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 300, ["helmet"] = 200, ["help"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_2");
            var keys = KeyPoint(probe.Window);

            Feed(probe, slot, 100_000, 800_000); // picks "help"; the after-pick bar is empty
            Check.WithCustomMessage("the after-pick bar was not empty - the fixture is wrong")
                .That(probe.Vm.GetSuggestionsWithContext()).IsEmpty();

            Feed(probe, keys, 833_000, 1_400_000); // >= 120 ms on the keys, slot target absent
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            Feed(probe, slot, 1_433_000, 2_500_000);

            Check.WithCustomMessage(
                    "the empty slot stayed latched after a real leave onto the keys - it is dead")
                .That(picks).HasSize(2);
            Check.That(picks).ContainsOnlyElementsThatMatch(word => word == "help");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void LeavingOnTheVeryNextSample_ReArmsThePickedSlot()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");
            var keys = KeyPoint(probe.Window);

            Feed(probe, slot, 100_000, 628_000); // ends exactly on the pick sample
            Feed(probe, keys, 661_000, 1_100_000); // the very next sample leaves the bar
            Feed(probe, slot, 1_133_000, 2_100_000);

            Check.WithCustomMessage("a leave starting on the next sample did not re-arm the slot")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AbsentTargetUnderARestingGaze_IsNotALeave()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            // Pick directly: the re-render clears every target, and no sample follows before the
            // no-layout feed, so the slot is genuinely absent under the resting gaze.
            probe.Window.OnPredictionDwelled("pred_0");

            Feed(probe, slot, 662_000, 800_000, layout: false);

            probe.Content.UpdateLayout();
            Feed(probe, slot, 833_000, 1_900_000);

            Check.WithCustomMessage("absence under a resting gaze cleared the latch")
                .That(picks).HasSize(1);
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

    // ---- S1: a pick from an auto-return sub-layout (accents) latches the slot's real rect ----------

    /// <summary>The rect <see cref="PredictionBarRearmLatch"/> remembered for a latched slot, or null.</summary>
    private static PhysicalRect? RememberedRect(Probe probe, string slotId)
    {
        var latch = typeof(KeyboardWindow)
            .GetField("_predictionRearm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(probe.Window)!;
        var slots = (System.Collections.IDictionary)latch.GetType()
            .GetField("_slots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(latch)!;
        var state = slots[slotId]!;
        return (PhysicalRect?)state.GetType().GetField("RememberedRect")!.GetValue(state);
    }

    /// <summary>The keyboard on its accents sub-layout with "hel" typed: the bar offers help/hello/helmet.</summary>
    private static Probe OpenOnAccents(Dictionary<string, int> words)
    {
        var probe = Open(words, "qwertz.json");
        probe.Vm.SwitchSubLayout("accents");
        Check.WithCustomMessage("arrange: the keyboard must be on the accents sub-layout")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");
        probe.Vm.CurrentInput = "hel";
        probe.Window.UpdatePredictions();
        probe.Content.UpdateLayout();
        return probe;
    }

    [StaFact]
    public void APickFromTheAccentsSubLayout_LatchesTheSlotsRealRect_NotNull()
    {
        // HandlePredictionSelected ends in AutoReturnIfNeeded: the swap back to the letters rebuilds the keys
        // and clears the prediction targets. The rect lookup that follows it found nothing, so the latch
        // remembered NO geometry. The lookup must run BEFORE the selection.
        var probe = OpenOnAccents(new Dictionary<string, int> { ["hello"] = 300, ["helmet"] = 200, ["help"] = 100 });
        try
        {
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_2");
            var expectedRect = probe.PredictionDetector.Targets.First(t => t.Id == "pred_2").Bounds;

            probe.Window.OnPredictionDwelled("pred_2");

            Check.WithCustomMessage("the pick did not leave the accents sub-layout - the fixture is wrong")
                .That(probe.Vm.ActiveSubLayoutId).IsNotEqualTo("accents");
            var remembered = RememberedRect(probe, "pred_2");
            Check.WithCustomMessage("a pick from the accents sub-layout latched the slot with a NULL rect")
                .That(remembered).IsNotNull();
            Check.That(remembered!.Value.Contains(slot)).IsTrue();
            Check.That(remembered).IsEqualTo(expectedRect);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void PickedSlotFromTheAccents_WithAnEmptyAfterPickBar_ReArmsAfterLeavingOntoTheKeys()
    {
        // The P2 shape (an absent slot after a real leave onto the keys re-arms), picked from the accents
        // sub-layout: with a null remembered rect absence counts as "still inside" for ever, so the slot
        // stayed dead.
        var probe = OpenOnAccents(new Dictionary<string, int> { ["hello"] = 300, ["helmet"] = 200, ["help"] = 100 });
        try
        {
            var picks = CollectPicks(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_2");
            var keys = KeyPoint(probe.Window);

            Feed(probe, slot, 100_000, 800_000);                                // picks "help" from the accents
            Check.WithCustomMessage("the first pick did not happen - the fixture is wrong").That(picks).HasSize(1);
            Check.WithCustomMessage("the after-pick bar was not empty - the fixture is wrong")
                .That(probe.Vm.GetSuggestionsWithContext()).IsEmpty();

            Feed(probe, keys, 833_000, 1_400_000);                              // >= 120 ms on the keys, slot absent
            probe.Vm.SwitchSubLayout("accents");
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            PlaceTargets(probe);
            Feed(probe, slot, 1_433_000, 2_500_000);

            Check.WithCustomMessage(
                    "the slot stayed latched after a real leave onto the keys: a pick from the accents left it dead")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }
}
