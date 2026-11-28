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
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Core.Interfaces;
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
/// F8 on the prediction bar: the per-slot latch (F4/F7) gets the same long look the keys have. After
/// a pick, a second dwell of <see cref="KeyboardLongLook.RepeatDwellUs"/> on the SAME slot — counted
/// after the bar's pick cooldown, on-slot time only, with the shared 400 ms excursion tolerance —
/// picks whatever word the slot shows NOW. An empty slot (F7's reserved slot 0) has no target and
/// cannot fire, and its long-look progress holds back the late slot-0 fill.
///
/// <para>
/// The window is never shown: the probe hosts it in an off-screen <c>WS_POPUP</c> HwndSource, like
/// the prediction-exclusion tests, so nothing paints on the owner's screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionLongLookTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int CooldownMs = 400;
    private const int PredictionDwellMs = 500;
    private const long StepUs = 33_000; // ~30 Hz tracker cadence

    private sealed class InMemoryDictProvider(Dictionary<string, int> words) : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new(words);
        public void Save(Dictionary<string, int> w) { }
    }

    /// <summary>Consume-once next-word predictor: null means "still running", like the real one.</summary>
    private sealed class FakeLm : ILanguageModelPredictor
    {
        private PredictionResult? _pending;

        public List<string> NextWordCalls { get; } = [];
        public bool IsAvailable => true;

        public void Predict(string context, string? prefix = null) { }
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

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content,
        HwndSource Source, WeakReferenceMessenger Messenger, DwellDetector PredictionDetector,
        FakeLm Lm, PredictionOrchestrator Orchestrator)
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

    private static PredictionBarRearmLatch RearmLatch(KeyboardWindow w) =>
        (PredictionBarRearmLatch)typeof(KeyboardWindow)
            .GetField("_predictionRearm", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Probe Open(Dictionary<string, int> words, bool withLm)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };

        var vm = new KeyboardViewModel(settings, messenger);
        var lm = new FakeLm();
        var orchestrator = new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY"),
            lmPredictor: withLm ? lm : null);
        vm.SetOrchestrator(orchestrator);
        vm.IsVisible = true;

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-longlook-probe")
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

        return new Probe(w, vm, content, source, messenger, PredictionDetector(w), lm, orchestrator);
    }

    /// <summary>Learns ("hello" → "world") and ("hello" → "there") twice each, then leaves "hel"
    /// typed so the bar offers "hello" in slot 0 and the after-pick bar is not empty.</summary>
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

    /// <summary>Brings the view model to a word boundary: "Haus" typed, then Space.</summary>
    private static void TypeIntoTheBoundary(Probe probe, string word = "Haus")
    {
        var e = StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext()) probe.Vm.ActivateKey((string)e.Current, null);
        probe.Vm.HandleSpace();
    }

    private static PhysicalPoint SlotCenter(Probe probe, string id)
        => probe.PredictionDetector.Targets.First(target => target.Id == id).Center;

    private static PhysicalPoint KeyPoint(KeyboardWindow window)
    {
        var positions = (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;
        return positions.Values.First();
    }

    private static List<string> CollectPicks(Probe probe)
    {
        var picks = new List<string>();
        probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => picks.Add(m.Word));
        return picks;
    }

    private static List<string> RenderedWords(KeyboardWindow w) =>
        w.PredictionBar.Children.Cast<Border>().Select(CellText).ToList();

    private static string CellText(UIElement? element) => element switch
    {
        TextBlock tb when !string.IsNullOrEmpty(tb.Text) => tb.Text,
        // F17's greyed typed prefix makes the label two Runs; TextBlock.Text stays empty for an
        // inline-built label, so read the runs' text instead.
        TextBlock tb => string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)),
        Border border => CellText(border.Child),
        Panel panel => panel.Children.Cast<UIElement>()
            .Select(CellText).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
        ContentControl content when content.Content is UIElement child => CellText(child),
        _ => ""
    };

    /// <summary>The drawn fill of a prediction cell: the accent outline's thickness over its max.</summary>
    private static double CellProgress(Border cell)
    {
        var grid = (Grid)cell.Child;
        var accent = grid.Children.OfType<System.Windows.Shapes.Rectangle>().Last();
        return accent.StrokeThickness / DwellOutlineOverlay.MaxThickness;
    }

    private static void Feed(Probe probe, PhysicalPoint point, long fromUs, long toUs)
    {
        for (var ts = fromUs; ts <= toUs; ts += StepUs)
        {
            probe.Window.UpdateGaze(point, ts);
            // The real UI thread runs a layout pass between gaze samples. The probe has no
            // dispatcher loop, so run it here or re-rendered cells never acquire a size.
            probe.Content.UpdateLayout();
        }
    }

    /// <summary>Feeds a slot until it fires, returning the pick sample's timestamp.</summary>
    private static long PickSlot(Probe probe, PhysicalPoint slot, List<string> picks)
    {
        var seen = picks.Count;
        for (long ts = 100_000; ts <= 3_000_000; ts += StepUs)
        {
            probe.Window.UpdateGaze(slot, ts);
            probe.Content.UpdateLayout();
            if (picks.Count > seen) return ts;
        }
        throw new InvalidOperationException("the slot never fired");
    }

    /// <summary>
    /// Anchors the latched slot's long-look clock exactly at the cooldown's end and credits exactly
    /// <paramref name="creditedMs"/> of continuous on-slot time. The 33 ms cadence alone cannot land
    /// on an exact millisecond, so the boundary test drives its own timestamps.
    /// </summary>
    private static void SlotSecondDwellExactly(Probe probe, PhysicalPoint slot, long pickTs, int creditedMs)
    {
        // One sample inside the cooldown re-registers the re-rendered bar's targets before the anchor.
        probe.Window.UpdateGaze(slot, pickTs + StepUs);
        probe.Content.UpdateLayout();
        var anchor = pickTs + (CooldownMs * 1000L);
        probe.Window.UpdateGaze(slot, anchor);
        probe.Content.UpdateLayout();
        for (long t = anchor + StepUs; t < anchor + (creditedMs * 1000L); t += StepUs)
        {
            probe.Window.UpdateGaze(slot, t);
            probe.Content.UpdateLayout();
        }
        probe.Window.UpdateGaze(slot, anchor + (creditedMs * 1000L));
        probe.Content.UpdateLayout();
    }

    [StaTheory]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    public void TheSlotLongLookBoundary_IsExact(int creditedMs, int expectedPicks)
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            var pickTs = PickSlot(probe, slot, picks);
            Check.WithCustomMessage("arrange: the first pick must happen").That(picks).HasSize(1);

            SlotSecondDwellExactly(probe, slot, pickTs, creditedMs);

            Check.WithCustomMessage(
                    $"{creditedMs} ms of on-slot time produced {picks.Count} picks, expected {expectedPicks}")
                .That(picks).HasSize(expectedPicks);
        }
        finally { probe.Dispose(); }
    }

    // ---- the long look -------------------------------------------------------------------------

    [StaFact]
    public void TheLongLookOnAPickedSlot_PicksTheWordItShowsNow()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            // First pick from the normal 500 ms dwell; then the long look on the same slot. The
            // after-pick re-render put a NEW word in that slot, and the repeat must pick that one.
            Feed(probe, slot, 100_000, 3_100_000);

            Check.WithCustomMessage("the long look did not re-pick the slot").That(picks).HasSize(2);
            Check.That(picks[0]).IsEqualTo("hello");
            Check.WithCustomMessage($"the repeat picked '{picks[1]}', not the slot's current word")
                .That(new[] { "world", "there" }).Contains(picks[1]);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ARestShorterThanTheLongLook_DoesNotPickAgain()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            // The pick lands at ~628 ms; cooldown plus 900 ms of on-slot time stays under the
            // shared KeyboardLongLook.RepeatDwellUs long look.
            Feed(probe, slot, 100_000, 628_000 + CooldownMs * 1000L + 900_000);

            Check.WithCustomMessage("a rest shorter than the long look re-picked the slot")
                .That(picks).HasSize(1);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TheBarLongLook_UsesTheSameRepeatDwellAsTheKeys()
    {
        // The one constant, measured from the OUTSIDE: the second pick may not happen before
        // cooldown + KeyboardLongLook.RepeatDwellUs of on-slot time, and happens on the first
        // sample after it. A bar value that diverged (say 800 ms) lands below this window; a key
        // value that diverged fails the key tests the same way.
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            long? firstPickTs = null, secondPickTs = null;
            var seen = 0;
            for (long ts = 100_000; ts <= 4_000_000; ts += StepUs)
            {
                probe.Window.UpdateGaze(slot, ts);
                probe.Content.UpdateLayout();
                if (picks.Count == seen) continue;
                seen = picks.Count;
                if (seen == 1) firstPickTs = ts;
                else if (seen == 2) { secondPickTs = ts; break; }
            }

            Check.WithCustomMessage("the first pick did not happen").That(firstPickTs).IsNotNull();
            Check.WithCustomMessage("the long look did not complete").That(secondPickTs).IsNotNull();

            var elapsedUs = secondPickTs!.Value - firstPickTs!.Value;
            var expectedUs = CooldownMs * 1000L + KeyboardLongLook.RepeatDwellUs;
            Check.WithCustomMessage(
                    $"the bar's long look completed after {elapsedUs / 1000} ms; the shared constant "
                    + $"with the {CooldownMs} ms cooldown needs {expectedUs / 1000} ms")
                .That(elapsedUs).IsGreaterOrEqualThan(expectedUs - (2 * StepUs));
            Check.WithCustomMessage(
                    $"the bar's long look completed late ({elapsedUs / 1000} ms vs "
                    + $"{expectedUs / 1000} ms) - it did not count from the cooldown")
                .That(elapsedUs).IsStrictlyLessThan(expectedUs + (3 * StepUs));
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TheLongLookFill_ShowsOnTheSlotLikeANormalDwell()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            Feed(probe, slot, 100_000, 628_000);            // the first pick
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);

            // F18: the pick now lights a 120 ms fired step on the fresh cell. A pumpless STA test
            // never ticks that timer, so the step is ended here (the seam stands in for the tick)
            // before the long-look fill is measured; production ends it at +120 ms.
            probe.Window.EndPredictionFiredStep();

            // Through the cooldown and ~400 ms of the long look.
            Feed(probe, slot, 661_000, 628_000 + CooldownMs * 1000L + 400_000);

            var expected = 400.0 / (KeyboardLongLook.RepeatDwellUs / 1000.0);
            var progress = RearmLatch(probe.Window).LongLookProgress("pred_0");
            Check.WithCustomMessage($"latch progress {progress:F2} is not ~{expected:F2} of the long look")
                .That(progress).IsCloseTo(expected, 0.08);
            var drawn = CellProgress((Border)probe.Window.PredictionBar.Children[0]);
            Check.WithCustomMessage($"the slot's drawn fill {drawn:F2} does not follow the long look")
                .That(drawn).IsCloseTo(progress, 0.02);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AGapLongerThanTheClamp_DoesNotCompleteTheLongLook()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            Feed(probe, slot, 100_000, 800_000);   // the first pick at ~628 ms
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);

            // Start the long look a couple of samples past the cooldown, THEN a blink/dropout of
            // 1.2 s while the gaze rests on the slot: a silence is not proof of rest, so it must
            // reset the running clock rather than be credited (the detector's own 150 ms clamp).
            Feed(probe, slot, 833_000, 1_200_000);
            probe.Window.UpdateGaze(slot, 1_200_000 + 1_200_000);
            Feed(probe, slot, 1_200_000 + 1_233_000, 1_200_000 + 1_233_000 + 900_000);

            Check.WithCustomMessage("a 1.2 s sample gap was credited as on-slot time")
                .That(picks).HasSize(1);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void LeavingTheLatchedSlot_ClearsItsLongLookFill()
    {
        // Review 01 F1: the paint loop used to skip an unlatched slot, so a half-filled outline
        // stayed frozen on a slot the gaze had left (the detector never reports that slot - the
        // target filter kept it out while it was latched). The fill must go to 0 on the sample the
        // latch drops, without touching any other slot and without a re-render.
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            var pickTs = PickSlot(probe, slot, picks);
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);
            // F18: end the pick's 120 ms fired step (no timer tick in a pumpless test), so the
            // long-look fill measured below is the fill, not the step.
            probe.Window.EndPredictionFiredStep();

            var anchor = pickTs + (CooldownMs * 1000L);
            Feed(probe, slot, anchor, anchor + 600_000);   // ~0.6 of the long look

            var cell = (Border)probe.Window.PredictionBar.Children[0];
            var fill = CellProgress(cell);
            Check.WithCustomMessage($"arrange: the long look must show a fill (was {fill:F2})")
                .That(fill).IsStrictlyGreaterThan(0.25);

            var leaveFrom = anchor + 633_000;
            Feed(probe, KeyPoint(probe.Window), leaveFrom, leaveFrom + 250_000);

            Check.WithCustomMessage("the latch did not drop after a 250 ms leave")
                .That(RearmLatch(probe.Window).IsLatched("pred_0")).IsFalse();
            Check.WithCustomMessage("the leave re-rendered the bar; the frozen-cell case is untested")
                .That(ReferenceEquals(probe.Window.PredictionBar.Children[0], cell)).IsTrue();
            Check.WithCustomMessage($"a stale long-look fill {CellProgress(cell):F2} stayed on the slot")
                .That(CellProgress(cell)).IsEqualTo(0.0);
        }
        finally { probe.Dispose(); }
    }

    // ---- the excursion tolerance (review 01 F2) ------------------------------------------------

    [StaFact]
    public void ClearingTheLeftSlotsFill_LeavesANormalDwellOnAnotherSlotAlone()
    {
        // The other half of review 01 F1's fix: only the outline THIS loop painted may be cleared.
        // A normal dwell on another slot is driven by OnPredictionProgress and must survive the
        // clear (clearing unlatched slots wholesale would erase it - the old comment warned so).
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slotZero = SlotCenter(probe, "pred_0");

            var pickTs = PickSlot(probe, slotZero, picks);
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);
            // F18: end the pick's 120 ms fired step (no timer tick in a pumpless test).
            probe.Window.EndPredictionFiredStep();

            var anchor = pickTs + (CooldownMs * 1000L);
            Feed(probe, slotZero, anchor, anchor + 400_000);   // paint slot 0's long-look fill

            var zeroCell = (Border)probe.Window.PredictionBar.Children[0];
            var oneCell = (Border)probe.Window.PredictionBar.Children[1];
            Check.WithCustomMessage($"arrange: slot 0 must show a long-look fill (was {CellProgress(zeroCell):F2})")
                .That(CellProgress(zeroCell)).IsStrictlyGreaterThan(0.2);

            // A normal dwell on slot 1: slot 0's leave clock runs, its latch drops and only its
            // fill may clear, while slot 1's detector-driven fill keeps growing below its 500 ms fire.
            var slotOne = SlotCenter(probe, "pred_1");
            var leaveFrom = anchor + 433_000;
            Feed(probe, slotOne, leaveFrom, leaveFrom + 400_000);

            Check.WithCustomMessage("the latch did not drop after the leave")
                .That(RearmLatch(probe.Window).IsLatched("pred_0")).IsFalse();
            Check.WithCustomMessage($"the stale long-look fill {CellProgress(zeroCell):F2} stayed on slot 0")
                .That(CellProgress(zeroCell)).IsEqualTo(0.0);
            Check.WithCustomMessage("the leave re-rendered the bar; the fill case is untested")
                .That(ReferenceEquals(probe.Window.PredictionBar.Children[1], oneCell)).IsTrue();
            Check.WithCustomMessage($"the normal dwell fill on slot 1 was cleared ({CellProgress(oneCell):F2})")
                .That(CellProgress(oneCell)).IsStrictlyGreaterThan(0.5);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AReloadOrHideReset_ClearsThePaintedLongLookFill()
    {
        // The reset path (hide/re-show, layout reload) clears the latch without a leave sample, so
        // it must drop the fills the latch painted itself (review 01 F1): otherwise a fill frozen at
        // hide time is still drawn when the bar comes back without a re-render.
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            var pickTs = PickSlot(probe, slot, picks);
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);
            // F18: end the pick's 120 ms fired step (no timer tick in a pumpless test).
            probe.Window.EndPredictionFiredStep();

            var anchor = pickTs + (CooldownMs * 1000L);
            Feed(probe, slot, anchor, anchor + 400_000);
            var cell = (Border)probe.Window.PredictionBar.Children[0];
            Check.WithCustomMessage($"arrange: the long look must show a fill (was {CellProgress(cell):F2})")
                .That(CellProgress(cell)).IsStrictlyGreaterThan(0.2);

            typeof(KeyboardWindow)
                .GetMethod("ResetPredictionRearm", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(probe.Window, null);

            Check.WithCustomMessage("the reset left the latch alive")
                .That(RearmLatch(probe.Window).IsLatched("pred_0")).IsFalse();
            Check.WithCustomMessage($"the reset left a long-look fill {CellProgress(cell):F2} on the slot")
                .That(CellProgress(cell)).IsEqualTo(0.0);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AOneSampleBlipOffTheLatchedSlot_PausesTheLongLook()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");
            var pickTs = PickSlot(probe, slot, picks);
            var anchor = pickTs + (CooldownMs * 1000L);

            Feed(probe, slot, anchor, anchor + 500_000);              // 500 ms credited
            probe.Window.UpdateGaze(KeyPoint(probe.Window), anchor + 533_000);   // one blip sample
            probe.Content.UpdateLayout();
            Feed(probe, slot, anchor + 566_000, anchor + 1_200_000);  // 634 more: total 1134

            Check.WithCustomMessage(
                    "a 33 ms blip reset the long-look clock instead of pausing it")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ATwoSampleBlipOffTheLatchedSlot_PausesTheLongLook()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: false);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");
            var pickTs = PickSlot(probe, slot, picks);
            var anchor = pickTs + (CooldownMs * 1000L);

            Feed(probe, slot, anchor, anchor + 500_000);              // 500 ms credited
            probe.Window.UpdateGaze(KeyPoint(probe.Window), anchor + 533_000);   // two blip samples,
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(KeyPoint(probe.Window), anchor + 566_000);   // 67 ms away
            probe.Content.UpdateLayout();
            Feed(probe, slot, anchor + 600_000, anchor + 1_250_000);  // 650 more: total 1150

            Check.WithCustomMessage(
                    "a 67 ms blip reset the long-look clock instead of pausing it")
                .That(picks).HasSize(2);
        }
        finally { probe.Dispose(); }
    }

    [Fact]
    public void AnExcursionPastTheTolerance_ResetsTheLongLookClock()
    {
        // A target that disappears while the latch survives (a re-render leaves the slot empty)
        // keeps the latch alive through the remembered rect but pauses the clock; past the shared
        // 400 ms tolerance the accumulated progress resets instead of resuming. Real off-slot
        // excursions are capped by the 120 ms leave, so this is the branch's only reachable path.
        var detector = new DwellDetector(cooldownMs: 0);
        var rect = new PhysicalRect(0, 0, 100, 100);
        detector.AddTarget(new DwellTarget("pred_0", rect, 500));
        var latch = new PredictionBarRearmLatch();
        var inside = new PhysicalPoint(50, 50);

        latch.Latch("pred_0", rect, 0);
        for (long t = 0; t <= 900_000; t += 30_000)
            latch.Update(inside, t, detector);

        Check.WithCustomMessage("arrange: 900 ms of on-slot time must be credited")
            .That(latch.LongLookProgress("pred_0")).IsCloseTo(0.9, 0.05);

        detector.ClearTargets();
        for (long t = 930_000; t <= 1_500_000; t += 30_000)
            latch.Update(inside, t, detector);

        Check.WithCustomMessage("the absent target cleared the latch instead of pausing")
            .That(latch.IsLatched("pred_0")).IsTrue();
        Check.WithCustomMessage("progress survived an excursion past the 400 ms tolerance")
            .That(latch.LongLookProgress("pred_0")).IsEqualTo(0.0);
    }

    // ---- F7's reserved slot 0 -----------------------------------------------------------------

    [StaFact]
    public void TheLatchedSlot_PickedBeforeAReservation_StaysEmptyAndCannotFire()
    {
        // The pick empties slot 0 (the next-word request reserves it), but the slot stays latched.
        // An empty slot has no target: the long look must neither fill its outline nor pick.
        var probe = Open(new Dictionary<string, int> { ["hello"] = 100, ["world"] = 100, ["there"] = 100 },
            withLm: true);
        try
        {
            var picks = CollectPicks(probe);
            SeedBarAndPairs(probe);
            PlaceTargets(probe);
            var slot = SlotCenter(probe, "pred_0");

            Feed(probe, slot, 100_000, 800_000);   // picks "hello"; the boundary reserves slot 0
            Check.WithCustomMessage("arrange: the pick must happen").That(picks).HasSize(1);
            Check.WithCustomMessage("arrange: the next-word request must reserve slot 0")
                .That(probe.Vm.NextWordSlotReserved).IsTrue();
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEmpty();

            Feed(probe, slot, 833_000, 833_000 + CooldownMs * 1000L + 1_500_000);

            Check.WithCustomMessage("an empty slot fired by the long look").That(picks).HasSize(1);
            Check.WithCustomMessage("the empty slot accumulated long-look progress")
                .That(RearmLatch(probe.Window).LongLookProgress("pred_0")).IsEqualTo(0.0);
            Check.WithCustomMessage("slot 0 gained a dwell target while reserved")
                .That(probe.PredictionDetector.Targets.Select(t => t.Id)).Not.Contains("pred_0");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void A_longLookInFlight_HoldsBackTheSlotZeroFill()
    {
        var probe = Open(new Dictionary<string, int>
        {
            ["ist"] = 100, ["gut"] = 100, ["leer"] = 80, ["hier"] = 70,
        }, withLm: true);
        try
        {
            var picks = CollectPicks(probe);
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("Haus", "ist");
            probe.Orchestrator.LearnPair("ist", "gut");
            probe.Orchestrator.LearnPair("ist", "gut");
            TypeIntoTheBoundary(probe);
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            PlaceTargets(probe);

            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEmpty();
            Check.WithCustomMessage("arrange: slot 1 must offer the bigram")
                .That(RenderedWords(probe.Window)[1]).IsEqualTo("ist");

            probe.Lm.Complete("leer");
            Feed(probe, new PhysicalPoint(-99999, -99999), 33_000, 66_000);
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEqualTo("leer");

            // Pick slot 1: the pick re-renders at a boundary, reserves slot 0 for a new request
            // and leaves the picked slot latched. "gut" takes slot 1.
            var slotOne = SlotCenter(probe, "pred_1");
            Feed(probe, slotOne, 99_000, 99_000 + PredictionDwellMs * 1000L + 200_000);
            Check.WithCustomMessage("arrange: the slot-1 pick must happen").That(picks).HasSize(1);
            Check.WithCustomMessage("arrange: the new boundary must reserve slot 0")
                .That(probe.Vm.NextWordSlotReserved).IsTrue();
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[1]).IsEqualTo("gut");

            // Start the long look on the latched slot 1 (past the cooldown, 200 ms in), THEN let the
            // answer for the new request arrive: a fill while the long look is in flight would
            // change the word under a look that is already counting toward a repeat pick.
            var afterPick = 99_000 + PredictionDwellMs * 1000L + 300_000;
            var longLookStart = afterPick + CooldownMs * 1000L + 200_000;
            Feed(probe, slotOne, afterPick, longLookStart);
            Check.WithCustomMessage("arrange: the long look must be in flight")
                .That(RearmLatch(probe.Window).LongLookProgress("pred_1")).IsStrictlyGreaterThan(0.0);

            probe.Lm.Complete("hier");
            Feed(probe, slotOne, longLookStart + StepUs, longLookStart + (3 * StepUs));

            Check.WithCustomMessage("the answer filled slot 0 while a long look was in flight")
                .That(RenderedWords(probe.Window)[0]).IsEmpty();

            // Leaving the slot ends the long look; the waiting answer lands on a later sample.
            Feed(probe, KeyPoint(probe.Window), longLookStart + (4 * StepUs), longLookStart + 300_000);
            Feed(probe, new PhysicalPoint(-99999, -99999), longLookStart + 333_000, longLookStart + 366_000);
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(RenderedWords(probe.Window)[0]).IsEqualTo("hier");
        }
        finally { probe.Dispose(); }
    }
}
