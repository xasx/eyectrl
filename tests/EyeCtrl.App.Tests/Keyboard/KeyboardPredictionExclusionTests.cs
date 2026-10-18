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
using EyeCtrl.Core.Adaptation;
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
/// The prediction bar is the user's only editing affordance (with <c>space</c> dead it is the only
/// way a space reaches the target app), so the two dwell surfaces — the key grid and the bar —
/// must be disjoint. The bar's hit areas are circles of each cell's half-diagonal, which bulge
/// below the bar into the key grid, so before the exclusion in <see cref="KeyboardWindow.UpdateGaze"/>
/// a gaze reaching for a key could complete a suggestion and insert a word the user never dwelled.
///
/// <para>
/// All tests use the off-screen <c>WS_POPUP</c> HwndSource path from T1's probe: no
/// <c>Show()</c>, no visible window, CI-subset safe. They drive the real <c>UpdateGaze</c> chain
/// and read the prediction detector's own <c>TargetProgress</c> stream (via reflection on the
/// field, as with the key-centre dictionaries), so "the outline cleared" is asserted on the
/// event that moves it, not inferred from a lack of firing.
/// </para>
///
/// <para>
/// <b>Which test is the probe and which is a guard.</b> Only "gaze inside the key area does not
/// accumulate prediction dwell" is a defect probe — it is red before the exclusion. "Gaze on the
/// bar does not accumulate key dwell" <b>already held before the exclusion</b> (outside the
/// KeyRows rect the gate is false and <c>ProcessKeyGaze</c> early-returns), so it is a regression
/// guard for the other half of the acceptance, not evidence of a second fix. Do not count its
/// green as a second defect closed.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionExclusionTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    // The live configuration the ticket measured: the surplus between consecutive key
    // activations (cooldown + keyDwell) is what opens the misfire window between them.
    private const int KeyDwellMs = 450;
    private const int CooldownMs = 400;
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

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>
    /// The words the bar currently displays, in cell order. The cell's child is a
    /// <c>DwellOutlineOverlay</c> Grid wrapping the original label (plus two Rectangles), so the
    /// label has to be found through the wrapper — a flat <c>Child as TextBlock</c> read returns
    /// an empty string and quietly makes "the bar rendered something" pass on nothing.
    /// </summary>
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

    /// <summary>
    /// A laid-out keyboard at effective scale 1.0 with a working prediction orchestrator, hosted
    /// in an HwndSource that is never made visible.
    /// </summary>
    private static Probe Open(Dictionary<string, int> words, ILanguageModelPredictor? lm = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;

        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY"), lmPredictor: lm));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-probe")
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
                + "return false, and every exclusion assertion would pass VACUOUSLY. Fix the "
                + "environment or the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source, messenger, PredictionDetector(w));
    }

    private static void Feed(Probe probe, PhysicalPoint at, long fromUs, long toUs, long stepUs = 30_000)
    {
        // The endpoint must be reachable on the step grid. (to - from) % step != 0 silently drops
        // the final sample, and when that sample is the one that completes a dwell the test fails
        // exactly as if the feature were broken. This happened while writing the LM-starvation
        // row: a 530 ms endpoint on a 30 ms grid stopped at 510 ms, the accept never ran, and the
        // red output read as the LM defect. This helper is shared into the later tickets, so the
        // guard is structural rather than one-off — round every endpoint to the cadence.
        Check.WithCustomMessage(
                $"Feed({fromUs}..{toUs}) step {stepUs}: the endpoint is not reachable on the step "
                + "grid, so the final sample would be dropped and a completion at the endpoint "
                + "would silently never happen. Round the endpoint to the cadence.")
            .That((toUs - fromUs) % stepUs).IsEqualTo(0);

        for (var t = fromUs; t <= toUs; t += stepUs)
            probe.Window.UpdateGaze(at, t);
    }

    private static PhysicalPoint ScreenCentre(FrameworkElement element)
    {
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return new PhysicalPoint(p.X, p.Y);
    }

    /// <summary>
    /// A point 20 DIP below a suggestion cell's bottom edge. Inside the cell's half-diagonal hit
    /// circle for ANY cell width (the circle's radius is at least ~69.5 DIP) and inside KeyRows
    /// (about 11 DIP below its top), so it is the robust overlap point for the mechanism test.
    /// </summary>
    private static PhysicalPoint BelowCell(FrameworkElement cell, double dipBelowBottom)
    {
        var p = cell.PointToScreen(new Point(cell.ActualWidth / 2, cell.ActualHeight + dipBelowBottom));
        return new PhysicalPoint(p.X, p.Y);
    }

    private static bool IsInsideKeyArea(KeyboardWindow w, PhysicalPoint p)
    {
        var tl = w.KeyRows.PointToScreen(new Point(0, 0));
        var br = w.KeyRows.PointToScreen(new Point(w.KeyRows.ActualWidth, w.KeyRows.ActualHeight));
        return new PhysicalRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y).Contains(p.X, p.Y);
    }

    private static bool IsInsidePredictionCircle(KeyboardWindow w, int cellIndex, PhysicalPoint p)
    {
        var cell = (FrameworkElement)w.PredictionBar.Children[cellIndex];
        var centre = ScreenCentre(cell);
        var radius = DpiHelper.LogicalToPhysicalX(
            Math.Sqrt(cell.ActualWidth * cell.ActualWidth + cell.ActualHeight * cell.ActualHeight) / 2);
        return Math.Sqrt(Math.Pow(p.X - centre.X, 2) + Math.Pow(p.Y - centre.Y, 2)) <= radius;
    }

    // ---- the defect probe: inside the key area, prediction dwell is excluded ----

    [StaFact]
    public void KeyAreaGaze_AcrossTwoKeyActivations_NeverCompletesAPrediction()
    {
        var probe = Open(new Dictionary<string, int> { ["hellowonderfulworld"] = 1000 });
        try
        {
            probe.Vm.KeyDwellCompleted += _ => { };
            var keyActivations = 0;
            probe.Vm.KeyDwellCompleted += _ => keyActivations++;
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            Check.WithCustomMessage(
                    "The live configuration's surplus closed: cooldown + keyDwell must exceed the "
                    + "prediction dwell or the misfire window between two key activations does not "
                    + "exist and this test goes vacuous while staying green.")
                .That(CooldownMs + KeyDwellMs).IsStrictlyGreaterThan(PredictionDwellMs);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
            Check.WithCustomMessage("The bar rendered nothing — nothing to exclude.")
                .That(RenderedWords(probe.Window)).Not.IsEmpty();

            var cell = (FrameworkElement)probe.Window.PredictionBar.Children[0];
            var barCentre = ScreenCentre(cell);

            // Guard: the prediction target must demonstrably work before we assert it is excluded;
            // a detector that never accumulates would pass the rest for the wrong reason.
            var progress = new List<double>();
            probe.PredictionDetector.TargetProgress += (_, p) => progress.Add(p);
            Feed(probe, barCentre, 30_000, 180_000);
            Check.WithCustomMessage(
                    "Pre-entry prediction progress never left 0 on the bar, so the exclusion "
                    + "assertion would be vacuous.")
                .That(progress.LastOrDefault()).IsStrictlyGreaterThan(0.0);

            // The overlap point: inside the key grid and inside the suggestion's hit circle.
            var overlap = BelowCell(cell, dipBelowBottom: 20);
            Check.WithCustomMessage("The overlap point is not inside the key area.")
                .That(IsInsideKeyArea(probe.Window, overlap)).IsTrue();
            Check.WithCustomMessage("The overlap point is not inside the prediction circle.")
                .That(IsInsidePredictionCircle(probe.Window, 0, overlap)).IsTrue();

            var beforeMove = progress.Count;
            Feed(probe, overlap, 230_000, 1_820_000);

            Check.WithCustomMessage("Fewer than two key activations occurred, so the hold did not "
                    + "cross the window this test exists for.")
                .That(keyActivations).IsGreaterOrEqualThan(2);
            Check.WithCustomMessage(
                    "A word was inserted while the gaze was inside the key grid. The bar's "
                    + "half-diagonal hit circles bulge into the keys, so without the exclusion a "
                    + "reach for a key completes a suggestion the user never dwelled.")
                .That(inserted).IsNull();
            Check.WithCustomMessage(
                    "The prediction target did not reset on entering the key area: its outline "
                    + "would freeze mid-progress instead of clearing. ResetAllState raises "
                    + "TargetProgress(id, 0), so a 0 must appear after the move.")
                .That(progress.Skip(beforeMove).Where(p => p > 0.0)).IsEmpty();
            Check.WithCustomMessage("The final prediction progress after the move should be 0.")
                .That(progress.Last()).IsEqualTo(0.0);
        }
        finally { probe.Dispose(); }
    }

    // ---- the user-visible symptom leg: the Win key must not insert a word ----

    [StaFact]
    public void GazeOnTheWinKey_InsertsNothing_AndStillActivatesTheKey()
    {
        var probe = Open(new Dictionary<string, int> { ["hellowonderfulworldthisisalongword"] = 1000 });
        try
        {
            var keyActivations = 0;
            probe.Vm.KeyDwellCompleted += _ => keyActivations++;
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var centre = KeyCentres(probe.Window)["modifier:win"];
            Check.WithCustomMessage(
                    "The suggestion cell is narrower than the ~165 DIP the Win-key leg needs: its "
                    + "hit circle does not reach the Win key centre, so the misfire under the bug "
                    + "would be impossible and this test vacuous.")
                .That(IsInsidePredictionCircle(probe.Window, 0, centre)).IsTrue();

            // Arming read: the circle check above is computed from the RENDERED visuals, so a
            // detector that renders cells but registers no targets would pass both it and the
            // no-insert assertion for the wrong reason. Prove the prediction target is alive on
            // the bar first — the same family as T1's live-key leg.
            var progress = new List<double>();
            probe.PredictionDetector.TargetProgress += (_, p) => progress.Add(p);
            var barCentre = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[0]);
            Feed(probe, barCentre, 30_000, 180_000);
            Check.WithCustomMessage(
                    "Prediction progress never left 0 on the bar, so the Win-key no-insert "
                    + "assertion below would be vacuous — the detector may be dead on arrival.")
                .That(progress.LastOrDefault()).IsStrictlyGreaterThan(0.0);

            // Continue into the Win key. The accumulated 180 ms must not matter: under the fix the
            // first sample inside the key area resets the prediction target.
            Feed(probe, centre, 210_000, 1_830_000);

            Check.WithCustomMessage("The Win key itself never activated — the gaze hold was not "
                    + "being processed as a key gaze.")
                .That(keyActivations).IsGreaterOrEqualThan(2);
            Check.WithCustomMessage(
                    "Looking at the Win key inserted a word: its centre lies inside the first "
                    + "suggestion's half-diagonal hit circle, and the prediction detector ran "
                    + "unconditionally. This is the field symptom.")
                .That(inserted).IsNull();
        }
        finally { probe.Dispose(); }
    }

    // ---- Part B: the activation path must index the RENDERED list, never a re-fetch ----

    /// <summary>
    /// Consume-once LM stand-in: `TryGetResults` hands its pending result over exactly once, like
    /// `SmallLMPredictor`, so a re-fetch at activation starves the next render.
    /// </summary>
    private sealed class FakeLmPredictor : ILanguageModelPredictor
    {
        private IReadOnlyList<PredictionResult>? _pending;

        public bool IsAvailable => true;
        public int PredictCalls { get; private set; }
        public int PollCount { get; private set; }

        public void Predict(string context, string? prefix = null) => PredictCalls++;

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            PollCount++;
            var results = _pending;
            _pending = null;
            return results;
        }

        public void Complete(string word) =>
            _pending = new List<PredictionResult>
            {
                new(word, 5.0, PredictionType.LMSuggestion, 0.5)
            };

        public void Cancel() { }

        public void Dispose() { }
    }

    [StaFact]
    public void PredictionDwell_InsertsTheRenderedWord_NotARefetchedOne()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000, ["world"] = 1000 });
        try
        {
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            Check.WithCustomMessage("The bar did not render the prefix's word.")
                .That(RenderedWords(probe.Window)).Contains("hello");

            var barCentre = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[0]);
            Feed(probe, barCentre, 30_000, 180_000);

            // Mid-dwell the typed prefix changes, so a RE-FETCH at activation would resolve index
            // 0 against a different list ("world") while the bar still displays "hello".
            probe.Vm.CurrentInput = "wor";
            Check.WithCustomMessage(
                    "The rendered list changed mid-dwell, so this test would not exercise the "
                    + "render/activate divergence at all.")
                .That(RenderedWords(probe.Window)).Contains("hello");

            Feed(probe, barCentre, 230_000, 530_000);   // completes at 500 ms from entry

            Check.WithCustomMessage(
                    "The inserted word came from a re-fetch, not from the rendered list: the user "
                    + "dwelled 'hello' and got a word that was never on screen. "
                    + "HandlePredictionSelected then learns that wrong word into the persisted "
                    + "dictionary, which the user cannot repair by gaze.")
                .That(inserted).IsEqualTo("hello");
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// With the same context and prefix still current, the next fetch (the next keystroke's
    /// render - no results-ready repaint) shows the LM completion. The activation path no
    /// longer re-fetches through a throwaway list, so the poll that renders is the poll that
    /// consumes the pending result.
    /// </summary>
    [StaFact]
    public void LmCompletion_RendersOnTheNextFetchWithTheSameInput()
    {
        var lm = new FakeLmPredictor();
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000, ["world"] = 1000 }, lm);
        try
        {
            // Give the keyboard a context: accepting "hello" sets the previous word.
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Vm.HandlePredictionSelected("hello");

            probe.Vm.CurrentInput = "wo";
            probe.Window.UpdatePredictions();   // the render call triggers the LM inference
            probe.Content.UpdateLayout();
            Check.WithCustomMessage("The render call did not trigger LM inference - nothing to show.")
                .That(lm.PredictCalls).IsStrictlyGreaterThan(0);

            lm.Complete("wonderful");
            probe.Window.UpdatePredictions();   // the next keystroke's render

            Check.WithCustomMessage(
                    "The fresh matching LM completion never rendered: the poll that rendered "
                    + "either consumed it for a throwaway list or dropped it as stale. "
                    + $"[polls={lm.PollCount}, predictCalls={lm.PredictCalls}, "
                    + $"rendered={string.Join(",", RenderedWords(probe.Window))}]")
                .That(RenderedWords(probe.Window)).Contains("wonderful");
        }
        finally { probe.Dispose(); }
    }

    // ---- T4: prediction target geometry (rects, not circumscribed circles) ----

    /// <summary>Every prediction target currently registered with the detector, via reflection.</summary>
    private static List<DwellTarget> RegisteredTargets(DwellDetector detector) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<string, DwellTarget>)typeof(DwellDetector)
            .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(detector)!).Values.ToList();

    /// <summary>The cell's on-screen rectangle in physical pixels, both corners through PointToScreen.</summary>
    private static PhysicalRect CellRect(FrameworkElement cell)
    {
        var tl = cell.PointToScreen(new Point(0, 0));
        var br = cell.PointToScreen(new Point(cell.ActualWidth, cell.ActualHeight));
        return new PhysicalRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    private static bool HasPositiveExtents(DwellTarget t) =>
        t.Bounds is { } b ? b.Width > 0 && b.Height > 0 : t.Radius > 0;

    /// <summary>
    /// The hit area must be the cell's own rectangle. The shipped circle is circumscribed, so it
    /// overhangs every edge — the excess is the bug. This point sits 5 DIP right of the cell (well
    /// outside the rect) but inside the old circle, and in the bar row, so the key-area exclusion
    /// cannot be what keeps it silent: only rect geometry can.
    /// </summary>
    [StaFact]
    public void PredictionHitArea_IsTheCellRect_NotACircumscribedCircle()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var cell = (FrameworkElement)probe.Window.PredictionBar.Children[0];
            var rect = CellRect(cell);
            var overhang = new PhysicalPoint(rect.X + rect.Width + 5, rect.CenterY);

            Check.WithCustomMessage(
                    "The overhang probe is inside the key area, so the ticket-3 exclusion would "
                    + "silence it and this test would not measure the hit geometry at all.")
                .That(IsInsideKeyArea(probe.Window, overhang)).IsFalse();
            Check.WithCustomMessage(
                    "The overhang probe is outside the cell's circumscribed circle, so it cannot "
                    + "distinguish the circle from the rect — the probe is measuring nothing.")
                .That(IsInsidePredictionCircle(probe.Window, 0, overhang)).IsTrue();

            Feed(probe, overhang, 30_000, 540_000);
            Check.WithCustomMessage(
                    "A point 5 DIP OUTSIDE the suggestion cell completed its dwell: the hit area "
                    + "is the circumscribed circle, whose overhang (about 4x the cell's area) "
                    + "reaches into the margins and — for the shapes this bar renders — over the "
                    + "key grid.")
                .That(inserted).IsNull();

            // Positive guard: the rect must still cover the cell it represents.
            var centre = new PhysicalPoint(rect.CenterX, rect.CenterY);
            Feed(probe, centre, 570_000, 1_110_000);
            Check.WithCustomMessage(
                    "The cell's own centre no longer completes its dwell — the rect is wrong.")
                .That(inserted).IsEqualTo("hello");
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// Regression guard for the geometry swap itself, NOT a pre-fix defect probe: the shipped
    /// circle tracks the bar through <c>UpdateTargetCenter</c>, so this passes today. It exists
    /// because the naive circle→rect swap leaves <c>UpdateTargetCenter</c> in the per-frame path,
    /// which writes only <c>Center</c> and is a silent no-op on a rect target — the hit area then
    /// freezes where the bar first rendered, a brand-new "the suggestion won't dwell" bug. Only
    /// <c>RebaseTarget</c> moves a rect target's geometry.
    /// </summary>
    [StaFact]
    public void PredictionHitArea_TracksTheBar_MidDwell()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var cell = (FrameworkElement)probe.Window.PredictionBar.Children[0];
            var before = CellRect(cell);

            // Half the dwell on the cell at its ORIGINAL position...
            Feed(probe, new PhysicalPoint(before.CenterX, before.CenterY), 30_000, 180_000);

            // ...then move the bar under the live dwell. Translate with a layout PROPERTY, not a
            // transform: a translation inside LayoutTransform is ignored by the layout engine by
            // design (it honours scale, rotate and skew — the fixture's scale relies on that, so
            // scale composing and translation not composing are consistent rather than
            // contradictory). The shift guard below exists because a bar that never moved would
            // make the tracking assertion vacuous.
            probe.Window.PredictionBar.Margin = new Thickness(150, 0, 0, 6);
            probe.Content.UpdateLayout();

            var moved = CellRect((FrameworkElement)probe.Window.PredictionBar.Children[0]);
            Check.WithCustomMessage(
                    $"The bar did not actually move (before {before.X:F1},{before.Y:F1} -> after "
                    + $"{moved.X:F1},{moved.Y:F1}), so the tracking assertion below would be vacuous.")
                .That(Math.Abs(moved.X - before.X) + Math.Abs(moved.Y - before.Y)).IsStrictlyGreaterThan(10);

            // Continue the SAME dwell at the MOVED centre. The placement pass must carry the hit
            // area with the bar (RebaseTarget); a straight circle→rect swap that leaves
            // UpdateTargetCenter in this path keeps the rect where it was first published, this
            // continuation misses, and the dwell never completes — the frozen-hit-area regression.
            Feed(probe, new PhysicalPoint(moved.CenterX, moved.CenterY), 210_000, 540_000);

            Check.WithCustomMessage(
                    "The bar moved mid-dwell and the target did not follow: the placement path "
                    + "wrote only Center (a no-op on a rect target), freezing the hit area where "
                    + "the bar first rendered under a live dwell.")
                .That(inserted).IsEqualTo("hello");
        }
        finally { probe.Dispose(); }
    }

    // ---- T4 Part B: the placement latch ----

    /// <summary>
    /// An unarranged placement pass must publish nothing, and must not latch. Today the pass
    /// publishes a radius-0 target and sets the latch, permanently: the else-branch only ever
    /// updates centres, so the degenerate target survives every later pass. The recovery half
    /// proves the latch stayed clear — after layout, a later pass must place real geometry.
    /// </summary>
    [StaFact]
    public void UnarrangedPlacementPass_DoesNotPublishDegenerateTargets_AndRecovers()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            // Deliberately no UpdateLayout: the new cells have ActualWidth == 0.

            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            Check.WithCustomMessage(
                    "A degenerate prediction target was published from an unarranged pass "
                    + "(radius 0 or zero-extent rect). The else-branch never repairs geometry, so "
                    + "that target is permanently undwellable.")
                .That(RegisteredTargets(probe.PredictionDetector)
                    .Where(t => !HasPositiveExtents(t)).Select(t => t.Id).ToList())
                .IsEmpty();

            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);

            Check.WithCustomMessage(
                    "After layout, no valid prediction target exists — the earlier pass latched "
                    + "and the later one only updated centres, never repairing the geometry.")
                .That(RegisteredTargets(probe.PredictionDetector)).Not.IsEmpty();
            Check.WithCustomMessage(
                    "A target is still degenerate after layout, so the latch was set before "
                    + "placement succeeded and the degenerate geometry survived.")
                .That(RegisteredTargets(probe.PredictionDetector)
                    .Where(t => !HasPositiveExtents(t)).Select(t => t.Id).ToList())
                .IsEmpty();
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// Rendering must not register placeholder targets at screen (0,0). Today it registers one per
    /// suggestion, and a pass that hits every <c>continue</c> (no presentation source) latches the
    /// flag with those placeholders still live — five dwell targets stacked at the screen's
    /// top-left. After the fix targets are published only from a successfully placed, arranged
    /// cell, so this is empty until the first placement pass and cell-centred afterwards.
    /// </summary>
    [StaFact]
    public void RenderedBar_RegistersNoTargetAtTheTopLeftCorner()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();

            Check.WithCustomMessage(
                    "Rendering registered a prediction target at screen (0,0) — the placeholder. "
                    + "On a pass that cannot place any cell (unarranged, or no presentation "
                    + "source) the latch would keep those placeholders live forever.")
                .That(RegisteredTargets(probe.PredictionDetector)
                    .Where(t => t.Center.X == 0 && t.Center.Y == 0).Select(t => t.Id).ToList())
                .IsEmpty();

            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var cells = probe.Window.PredictionBar.Children.Cast<FrameworkElement>()
                .Take(RenderedWords(probe.Window).Count).ToList();
            var targets = RegisteredTargets(probe.PredictionDetector);
            Check.That(targets).HasSize(cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                var expected = CellRect(cells[i]);
                var actual = targets.Single(t => t.Id == $"pred_{i}");
                Check.WithCustomMessage(
                        $"Target pred_{i} does not sit on its cell: centre "
                        + $"({actual.Center.X},{actual.Center.Y}) vs ({expected.CenterX},{expected.CenterY}).")
                    .That(actual.Center.X).IsCloseTo(expected.CenterX, 0.5);
                Check.That(actual.Center.Y).IsCloseTo(expected.CenterY, 0.5);
            }
        }
        finally { probe.Dispose(); }
    }

    // ---- regression guard, already green pre-fix: the bar does not accumulate key dwell ----

    /// <summary>
    /// The other half of the acceptance — gaze on the bar never accumulates key dwell — <b>already
    /// held before the exclusion</b>: outside the KeyRows rect the gate passed to
    /// <c>ProcessKeyGaze</c> is false and it early-returns after nulling the latch. This test is a
    /// regression guard for that half, NOT a defect probe; its green is not a second bug fixed.
    /// It holds for longer than a key dwell so an accidental key accumulation would fire.
    /// </summary>
    [StaFact]
    public void BarGaze_DoesNotAccumulateKeyDwell()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            var keyActivations = 0;
            probe.Vm.KeyDwellCompleted += _ => keyActivations++;
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var barCentre = ScreenCentre((FrameworkElement)probe.Window.PredictionBar.Children[0]);

            // 480 ms > keyDwell 450 but < predictionDwell 500: a key accumulation would fire; a
            // prediction completion would not, so the assertions stay attributable.
            Feed(probe, barCentre, 30_000, 480_000);

            Check.WithCustomMessage(
                    "Gaze on the prediction bar accumulated key dwell — outside the key area the "
                    + "gate must be false for the key tracker.")
                .That(keyActivations).IsEqualTo(0);
            Check.That(inserted).IsNull();
        }
        finally { probe.Dispose(); }
    }

    // ---- the keyboard family's blink-credit floor reaches this surface ----

    [StaFact]
    public void PredictionDetector_RegistersTheKeyboardFamilyRange_SoTheAdaptiveFloorBinds()
    {
        // The bar rides the shared DwellDetector through DwellTimeRouter, whose map composes the
        // forgiving fill against the target's baseline and then clamps to the registration's range.
        // That range is what keeps the knob's 0.6x composition (180 ms at the 300 ms family minimum)
        // from reaching a stall-completable dwell, so the prediction surface must register the
        // keyboard family, not Standard (200 — the same 180 ms composition would be admitted and a
        // 149 ms credited silence would leave 31 ms of real gaze). The emoji registration is pinned
        // in DwellRoutingOwnerRegistrationTests; this is the prediction half, and the key grid's half
        // is behavioural in EyeCtrl.ViewModels.Tests.KeyboardEffectiveDwellTests.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var registry = new DwellDetectorRegistry();
        var window = new KeyboardWindow(dwellRegistry: registry);
        try
        {
            var entry = registry.Registrations.Single();
            Check.That(entry.Context).IsEqualTo(DwellContext.KeyboardPrediction);
            Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.KeyboardMinDwellMs);
            Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.KeyboardMaxDwellMs);
        }
        finally { window.Close(); }
    }
}
