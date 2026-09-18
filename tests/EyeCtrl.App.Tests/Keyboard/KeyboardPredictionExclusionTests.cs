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

    /// <summary>The words the bar currently displays, in cell order.</summary>
    private static List<string> RenderedWords(KeyboardWindow w) =>
        w.PredictionBar.Children.Cast<Border>()
            .Select(b => b.Child switch
            {
                TextBlock tb => tb.Text,
                StackPanel sp => sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                _ => ""
            }).ToList();

    /// <summary>
    /// A laid-out keyboard at effective scale 1.0 with a working prediction orchestrator, hosted
    /// in an HwndSource that is never made visible.
    /// </summary>
    private static Probe Open(Dictionary<string, int> words)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;

        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY")));

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
            Feed(probe, barCentre, 30_000, 200_000);
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
            Feed(probe, overlap, 230_000, 1_830_000);

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

            Feed(probe, centre, 30_000, 1_830_000);

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
}
