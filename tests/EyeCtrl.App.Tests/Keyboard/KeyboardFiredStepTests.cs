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
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Wave D ticket F18: a fired key (or a picked suggestion slot) makes one brief weight step on its
/// OWN dwell outline — the C1 language, a hard step, wider, never recoloured — and then returns to
/// rest. The step exists because the click can be late or missing: without it a fired key and an
/// abandoned dwell both just lose their outline.
/// <para>
/// The window is never shown here: the shipped layout is hosted in an off-screen
/// <c>WS_POPUP</c> HwndSource at -30000 (never WS_VISIBLE), like the neighbouring prediction
/// tests. The one hide/show test shows a DWM-CLOAKED, click-through, off-screen window and SKIPS
/// if DWM cannot cloak it — never an uncloaked show.
/// </para>
/// <para>
/// <see cref="DwellOutlineOverlayTests"/> owns the outline's thickness rule; this file owns which
/// target gets the step, when it ends, and that nothing else changes.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardFiredStepTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 500;
    private const string TextKey = "key_0_1";   // Q on the shipped qwerty letters page

    private sealed class InMemoryDictProvider(Dictionary<string, int> words) : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new(words);
        public void Save(Dictionary<string, int> w) { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content,
        HwndSource Source, PredictionOrchestrator Orchestrator)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    /// <summary>
    /// A laid-out keyboard at effective scale 1.0 with a working prediction stack, hosted in an
    /// HwndSource that is never made visible.
    /// </summary>
    private static Probe Open(Dictionary<string, int>? words = null, double scale = 1.0,
        KeyboardSoundService? sound = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;

        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        var orchestrator = new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words ?? new Dictionary<string, int>()), "QWERTY"));
        vm.SetOrchestrator(orchestrator);

        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        if (sound is not null) window.SetSoundService(sound);
        window.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: 0);

        var content = (FrameworkElement)window.Content;
        window.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-fired-step-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = scale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        Measure(content);

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw, no key centre would publish, "
                + "and every fire assertion would pass VACUOUSLY.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
        Check.That(Centres(window)).Not.IsEmpty();

        return new Probe(window, vm, content, source, orchestrator);
    }

    private static void Measure(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    /// <summary>Feeds a step grid; the endpoint must lie on it or the last sample is dropped.</summary>
    private static void Feed(KeyboardWindow window, PhysicalPoint at, long fromUs, long toUs,
        long stepUs = 30_000)
    {
        Check.WithCustomMessage(
                $"Feed({fromUs}..{toUs}) step {stepUs}: the endpoint is not reachable on the step "
                + "grid, so the final sample would be dropped and a fire at the endpoint would "
                + "silently never happen.")
            .That((toUs - fromUs) % stepUs).IsEqualTo(0);

        for (var t = fromUs; t <= toUs; t += stepUs)
            window.UpdateGaze(at, t);
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, (Border Container, TextBlock? Label)> Visuals(KeyboardWindow w) =>
        (Dictionary<string, (Border Container, TextBlock? Label)>)typeof(KeyboardWindow)
            .GetField("_keyVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, DwellOutlineOverlay> Outlines(KeyboardWindow w) =>
        (Dictionary<string, DwellOutlineOverlay>)typeof(KeyboardWindow)
            .GetField("_keyOutlines", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static List<DwellOutlineOverlay?> PredictionOutlines(KeyboardWindow w) =>
        (List<DwellOutlineOverlay?>)typeof(KeyboardWindow)
            .GetField("_predictionOutlines", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>
    /// A DispatcherTimer's priority. It has no public getter, so this reads WPF's private field;
    /// a missing field fails loudly rather than reading a default.
    /// </summary>
    private static DispatcherPriority TimerPriority(DispatcherTimer timer)
    {
        var field = typeof(DispatcherTimer).GetField(
            "_priority", BindingFlags.NonPublic | BindingFlags.Instance);
        Check.WithCustomMessage("DispatcherTimer._priority is gone; find another way to read the timer's priority.")
            .That(field).IsNotNull();
        return (DispatcherPriority)field!.GetValue(timer)!;
    }

    /// <summary>Pumps the dispatcher for <paramref name="duration"/> of REAL time.</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var wake = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = duration,
        };
        wake.Tick += (_, _) =>
        {
            wake.Stop();
            frame.Continue = false;
        };
        wake.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// The per-key rest look, taken before the fire and compared after the step ends: every
    /// property the step could touch, plus the fill and label it must never touch. Brush
    /// identity, not colour: the outline's own brushes are the polarity rule.
    /// </summary>
    private sealed record RestLook(
        Brush AccentStroke, Brush HaloStroke, Brush AccentFill, Brush HaloFill,
        double AccentThickness, double HaloThickness, double OutlineOpacity,
        Brush KeyFill, Color KeyFillColour, Brush? LabelForeground);

    private static RestLook Capture(DwellOutlineOverlay outline, Border key, TextBlock? label) =>
        new(outline.Outline.Stroke, outline.Halo.Stroke, outline.Outline.Fill, outline.Halo.Fill,
            outline.Outline.StrokeThickness, outline.Halo.StrokeThickness, outline.Outline.Opacity,
            key.Background, ((SolidColorBrush)key.Background).Color, label?.Foreground);

    private static void AssertSameRestLook(RestLook rest, DwellOutlineOverlay outline,
        Border key, TextBlock? label, string when)
    {
        Check.WithCustomMessage($"{when}: the accent was recoloured.")
            .That(outline.Outline.Stroke).IsSameReferenceAs(rest.AccentStroke);
        Check.WithCustomMessage($"{when}: the halo was recoloured.")
            .That(outline.Halo.Stroke).IsSameReferenceAs(rest.HaloStroke);
        Check.WithCustomMessage($"{when}: the accent's fill changed (the step must never become a wash).")
            .That(outline.Outline.Fill).IsSameReferenceAs(rest.AccentFill);
        Check.WithCustomMessage($"{when}: the halo's fill changed.")
            .That(outline.Halo.Fill).IsSameReferenceAs(rest.HaloFill);
        Check.WithCustomMessage($"{when}: the rest outline did not come back.")
            .That((outline.Outline.StrokeThickness, outline.Halo.StrokeThickness))
            .IsEqualTo((rest.AccentThickness, rest.HaloThickness));
        Check.WithCustomMessage($"{when}: the outline's opacity changed.")
            .That(outline.Outline.Opacity).IsEqualTo(rest.OutlineOpacity);
        Check.WithCustomMessage($"{when}: the key's fill brush was replaced.")
            .That(key.Background).IsSameReferenceAs(rest.KeyFill);
        Check.WithCustomMessage($"{when}: the key's fill colour changed.")
            .That(((SolidColorBrush)key.Background).Color).IsEqualTo(rest.KeyFillColour);
        Check.WithCustomMessage($"{when}: the label's brush changed.")
            .That(label?.Foreground).IsSameReferenceAs(rest.LabelForeground);
    }

    [StaFact]
    public void ATextKeyFire_StepsThatKeyOnly_ThenEndRestoresTheRestLookExactly()
    {
        // The core F18 rule: on the fire event the fired key's own outline is stepped; no other
        // key is; End returns the same two rectangles, brushes and fill the key had at rest.
        // Mutations: step a different/first key → RED; step every key → RED; recolour on step →
        // the peak brush assertions RED; End that zeroes without restoring → RED (the Controls
        // test owns that half); never starting the timer → IsEndScheduled RED.
        var probe = Open();
        try
        {
            var centres = Centres(probe.Window);
            var outlines = Outlines(probe.Window);
            var visuals = Visuals(probe.Window);
            Check.WithCustomMessage($"The probe has no '{TextKey}' key centre.")
                .That(centres.ContainsKey(TextKey)).IsTrue();
            Check.WithCustomMessage($"The probe has no '{TextKey}' outline.")
                .That(outlines.ContainsKey(TextKey)).IsTrue();

            var outline = outlines[TextKey];
            var rest = Capture(outline, visuals[TextKey].Container, visuals[TextKey].Label);

            Feed(probe.Window, centres[TextKey], 1_000_000, 1_480_000);

            Check.WithCustomMessage("The key fire did not light the fired step.")
                .That(probe.Window.IsKeyFiredStepLit).IsTrue();
            Check.WithCustomMessage("The step landed on a different key than the one that fired.")
                .That(probe.Window.KeyFiredStepOutline).IsSameReferenceAs(outline);
            Check.WithCustomMessage("The fired outline is not at FiredThickness.")
                .That(outline.Outline.StrokeThickness).IsEqualTo(DwellOutlineOverlay.FiredThickness);
            Check.WithCustomMessage("The fired halo did not stay outside the accent.")
                .That(outline.Halo.StrokeThickness).IsStrictlyGreaterThan(outline.Outline.StrokeThickness);
            Check.That(outline.IsFired).IsTrue();
            Check.That(probe.Window.KeyFiredStepInterval)
                .IsEqualTo(TimeSpan.FromMilliseconds(FiredFlash.DurationMs));
            Check.WithCustomMessage("The whole step must end within 150 ms (the C1 bound).")
                .That(probe.Window.KeyFiredStepInterval.TotalMilliseconds)
                .IsStrictlyGreaterThan(0).And.IsLessOrEqualThan(150);
            Check.WithCustomMessage("The end timer is not scheduled at all.")
                .That(probe.Window.KeyFiredStepTimer.IsEnabled).IsTrue();

            // The step writes the outline only: the key's fill and label are untouched at the peak.
            Check.WithCustomMessage("The step recoloured the accent at the peak.")
                .That(outline.Outline.Stroke).IsSameReferenceAs(rest.AccentStroke);
            Check.WithCustomMessage("The step recoloured the halo at the peak.")
                .That(outline.Halo.Stroke).IsSameReferenceAs(rest.HaloStroke);
            Check.WithCustomMessage("The step changed the key fill at the peak.")
                .That(visuals[TextKey].Container.Background).IsSameReferenceAs(rest.KeyFill);
            Check.WithCustomMessage("The step changed the label brush at the peak.")
                .That(visuals[TextKey].Label?.Foreground).IsSameReferenceAs(rest.LabelForeground);

            foreach (var (id, other) in outlines)
            {
                if (id == TextKey) continue;
                Check.WithCustomMessage($"{id} was stepped by {TextKey}'s fire — one key per fire.")
                    .That((other.Outline.StrokeThickness, other.Halo.StrokeThickness))
                    .IsEqualTo((0.0, 0.0));
                Check.That(other.IsFired).IsFalse();
            }

            probe.Window.EndKeyFiredStep();

            Check.That(probe.Window.IsKeyFiredStepLit).IsFalse();
            Check.That(probe.Window.KeyFiredStepTimer.IsEnabled).IsFalse();
            Check.That(outline.IsFired).IsFalse();
            AssertSameRestLook(rest, outline, visuals[TextKey].Container, visuals[TextKey].Label,
                "after End");
        }
        finally { probe.Dispose(); }
    }

    [StaTheory]
    [InlineData("backspace")]
    [InlineData("enter")]
    [InlineData("modifier:shift")]
    public void ASpecialKeyFire_StepsThatSpecialKey(string keyId)
    {
        // Mutations: start the step only for text keys (the pre-F18 shape of OnKeyDwellCompleted's
        // early return) → RED for all three; step the wrong key → the identity assertion RED.
        var probe = Open();
        try
        {
            var centres = Centres(probe.Window);
            var outlines = Outlines(probe.Window);
            Check.WithCustomMessage($"The probe has no '{keyId}' key centre.")
                .That(centres.ContainsKey(keyId)).IsTrue();

            var fired = new List<string>();
            probe.Vm.KeyDwellCompleted += fired.Add;

            // 660 ms covers the app-escaping floor (600 ms for Enter/Tab) as well as the 450 ms
            // ordinary dwell; the key latches on its fire, so the extra time types nothing.
            Feed(probe.Window, centres[keyId], 1_000_000, 1_660_000);

            Check.WithCustomMessage($"Dwelling '{keyId}' never completed, so the step assertion would be vacuous.")
                .That(fired).Contains(keyId);
            Check.WithCustomMessage($"A fire on '{keyId}' did not step its outline.")
                .That(probe.Window.KeyFiredStepOutline).IsSameReferenceAs(outlines[keyId]);
            Check.That(probe.Window.IsKeyFiredStepLit).IsTrue();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void APageSwitchFire_RebuildsTheGrid_AndLeavesNothingOfTheOldGridStepped()
    {
        // A switchSub: action fires, starts the step, and rebuilds the grid in the same call. The
        // rebuild must END the step: the discarded old outline must not stay lit, and the new grid
        // must come up at rest. Mutation: drop ClearKeyState's End → the old outline stays lit and
        // IsKeyFiredStepLit stays true → RED.
        var probe = Open();
        try
        {
            var oldOutlines = Outlines(probe.Window).Values.ToList();
            Check.That(oldOutlines).Not.IsEmpty();
            var centres = Centres(probe.Window);
            Check.That(centres.ContainsKey("switchSub:numbers")).IsTrue();

            Feed(probe.Window, centres["switchSub:numbers"], 1_000_000, 1_480_000);

            Check.WithCustomMessage("The page switch never happened, so the rebuild leg is untested.")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("numbers");
            Check.WithCustomMessage("The step survived the grid rebuild.")
                .That(probe.Window.IsKeyFiredStepLit).IsFalse();
            foreach (var old in oldOutlines)
            {
                Check.WithCustomMessage(
                        "An element of the OLD grid is still stepped after the page switch built the new grid.")
                    .That((old.Outline.StrokeThickness, old.Halo.StrokeThickness)).IsEqualTo((0.0, 0.0));
                Check.That(old.IsFired).IsFalse();
            }
            foreach (var (id, fresh) in Outlines(probe.Window))
            {
                Check.WithCustomMessage($"{id} on the NEW grid came up stepped.")
                    .That((fresh.Outline.StrokeThickness, fresh.Halo.StrokeThickness))
                    .IsEqualTo((0.0, 0.0));
            }
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ASecondFireWithinTheStep_TakesTheStep_FromTheFirstKey()
    {
        // "At most one key is lit per fire": the step is one target at a time. A second key fires
        // while the first step is still lit (a pumpless test never ticks the 120 ms timer), so the
        // handover is exactly what a fast second fire does. Mutation: Start without End-ing the
        // previous outline → both keys stay lit → RED.
        var probe = Open();
        try
        {
            var centres = Centres(probe.Window);
            var outlines = Outlines(probe.Window);
            var first = TextKey;
            var second = "key_0_2";      // W on the same row, a different key in one look

            Feed(probe.Window, centres[first], 1_000_000, 1_480_000);
            Check.That(probe.Window.KeyFiredStepOutline).IsSameReferenceAs(outlines[first]);
            Check.That(outlines[first].IsFired).IsTrue();

            Feed(probe.Window, centres[second], 1_500_000, 1_980_000);

            Check.WithCustomMessage("The second fire did not take the step.")
                .That(probe.Window.KeyFiredStepOutline).IsSameReferenceAs(outlines[second]);
            Check.WithCustomMessage("The first key's outline stayed lit under the second fire.")
                .That(outlines[first].IsFired).IsFalse();
            Check.WithCustomMessage("The first key's outline did not return to rest at the handover.")
                .That((outlines[first].Outline.StrokeThickness, outlines[first].Halo.StrokeThickness))
                .IsEqualTo((0.0, 0.0));
            foreach (var (id, other) in outlines)
            {
                if (id == second) continue;
                Check.WithCustomMessage($"{id} is stepped besides the newest fire's key.")
                    .That(other.IsFired).IsFalse();
            }
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AKeyLongLookRepeat_StepsAgain_OnTheSameKey()
    {
        // F8's long look: a second dwell of KeyboardLongLook.RepeatDwellUs on the fired key fires
        // it again, and each typed letter needs its own step. The first step is ended by the test
        // (the seam stands in for the 120 ms tick); the repeat fire must light it again.
        // Mutation: step only on the first fire (e.g. if (!_keyFiredStep.IsLit) Start) → the
        // repeat leaves it dark → RED.
        var probe = Open();
        try
        {
            var centres = Centres(probe.Window);
            var outlines = Outlines(probe.Window);
            var completed = new List<string>();
            probe.Vm.KeyDwellCompleted += completed.Add;

            Feed(probe.Window, centres[TextKey], 1_000_000, 1_480_000);
            Check.WithCustomMessage("The first dwell never fired.")
                .That(completed).Contains(TextKey);
            probe.Window.EndKeyFiredStep();
            Check.That(probe.Window.IsKeyFiredStepLit).IsFalse();

            // Cooldown is 0, so the repeat clock starts after the fire and needs the shared 1 s.
            Feed(probe.Window, centres[TextKey], 1_510_000, 2_800_000);

            Check.WithCustomMessage(
                    $"The long look fired {completed.Count} time(s): [{string.Join(", ", completed)}] — "
                    + "a second dwell did not re-fire the key, so this test cannot see a repeat step.")
                .That(completed.Count(id => id == TextKey)).IsEqualTo(2);
            Check.WithCustomMessage("The repeat fire did not step the key again.")
                .That(probe.Window.IsKeyFiredStepLit).IsTrue();
            Check.That(probe.Window.KeyFiredStepOutline).IsSameReferenceAs(outlines[TextKey]);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void APredictionPick_StepsThePickedSlot_OnItsFreshCell_AndTheSlotRectIsUnchanged()
    {
        // The pick re-renders the bar, so the pre-pick cell is discarded; the NEW cell at the
        // picked slot carries the step (the word that has just been put there). The step writes
        // the outline only: the slot's cell geometry and its dwell rect are unchanged.
        // Mutations: start the step on the pre-pick overlay (detached, invisible) → not lit →
        // RED; step every slot → a neighbour is lit → RED; skip the slot step → not lit → RED.
        var probe = Open(new Dictionary<string, int>
        {
            ["hello"] = 1000, ["world"] = 900, ["there"] = 800, ["again"] = 700,
        });
        try
        {
            // A next word for after the pick: picking "hello" makes it the context, and "world"
            // is its learned pair, so the rebuilt bar still has a word in slot 0 to step. Two
            // learns clear BigramModel's MinFrequency of 2 (a single occurrence is noise).
            probe.Orchestrator.LearnPair("hello", "world");
            probe.Orchestrator.LearnPair("hello", "world");

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            Measure(probe.Content);
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            var cells = probe.Window.PredictionBar.Children.Cast<Border>().ToList();
            Check.WithCustomMessage("The bar rendered no cells, so there is no slot to pick.")
                .That(cells).Not.IsEmpty();
            var oldCell = cells[0];
            var oldRect = CellRect(oldCell);
            var oldOutline = PredictionOutlines(probe.Window)[0];
            Check.WithCustomMessage("Slot 0 has no outline, so the step assertion would be vacuous.")
                .That(oldOutline).IsNotNull();

            probe.Window.OnPredictionDwelled("pred_0");
            Measure(probe.Content);

            var freshCells = probe.Window.PredictionBar.Children.Cast<Border>().ToList();
            Check.WithCustomMessage("The pick did not re-render the bar.")
                .That(freshCells).Not.IsEmpty();
            var freshOutline = PredictionOutlines(probe.Window)[0];
            Check.WithCustomMessage("The rebuilt slot 0 has no cell/outline to carry the step.")
                .That(freshOutline).IsNotNull();

            Check.WithCustomMessage("The suggestion pick did not step its slot.")
                .That(probe.Window.IsPredictionFiredStepLit).IsTrue();
            Check.WithCustomMessage("The step landed on a different slot.")
                .That(probe.Window.PredictionFiredStepOutline).IsSameReferenceAs(freshOutline);
            Check.WithCustomMessage("The step landed on the PRE-PICK (discarded) overlay.")
                .That(ReferenceEquals(freshOutline, oldOutline)).IsFalse();
            Check.That(freshOutline!.Outline.StrokeThickness).IsEqualTo(DwellOutlineOverlay.FiredThickness);
            foreach (var (index, other) in PredictionOutlines(probe.Window).Index())
            {
                if (index == 0 || other is null) continue;
                Check.WithCustomMessage($"Slot {index} was stepped by the pick on slot 0.")
                    .That(other.IsFired).IsFalse();
            }

            Check.WithCustomMessage("The step changed the picked slot's cell geometry.")
                .That(CellRect(freshCells[0])).IsEqualTo(oldRect);

            // The dwell target re-registers on the next sample: it must still be the cell's own
            // rect — a step may never move or grow the target under the gaze.
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 1_000_000);
            var target = PredictionTarget(probe.Window, "pred_0");
            Check.WithCustomMessage("The picked slot's dwell rect changed under the step.")
                .That(target.Bounds!.Value).IsEqualTo(oldRect);

            probe.Window.EndPredictionFiredStep();
            Check.That(probe.Window.IsPredictionFiredStepLit).IsFalse();
            Check.That(freshOutline.IsFired).IsFalse();
            Check.That((freshOutline.Outline.StrokeThickness, freshOutline.Halo.StrokeThickness))
                .IsEqualTo((0.0, 0.0));
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ARenderedBarDuringTheSlotStep_EndsIt_SoNoDiscardedOutlineCanOutliveItsCell()
    {
        // Any render discards every slot outline. A live step must end with it, or the helper
        // would hold an outline that left the tree. Mutation: drop UpdatePredictions' End → the
        // step stays lit on the detached overlay → RED.
        var probe = Open(new Dictionary<string, int>
        {
            ["hello"] = 1000, ["world"] = 900, ["there"] = 800, ["again"] = 700,
        });
        try
        {
            probe.Orchestrator.LearnPair("hello", "world");
            probe.Orchestrator.LearnPair("hello", "world");
            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            Measure(probe.Content);
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);

            probe.Window.OnPredictionDwelled("pred_0");
            Check.That(probe.Window.IsPredictionFiredStepLit).IsTrue();

            probe.Window.UpdatePredictions();

            Check.WithCustomMessage("A bar re-render left the old slot's step lit.")
                .That(probe.Window.IsPredictionFiredStepLit).IsFalse();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TheClick_IsStillPlayedExactlyOncePerKeyFire()
    {
        // The step confirms the fire in addition to the click, never instead of it (S1's click
        // contract). Mutation: drop PlayKeyClick from OnKeyDwellCompleted → RED.
        var sound = new RecordingSoundService();
        var probe = Open(sound: sound);
        try
        {
            var centres = Centres(probe.Window);

            Feed(probe.Window, centres[TextKey], 1_000_000, 1_480_000);
            Check.WithCustomMessage("A text-key fire no longer plays the click.")
                .That(sound.Clicks).IsEqualTo(1);

            Feed(probe.Window, centres["switchSub:numbers"], 1_500_000, 1_980_000);
            Check.WithCustomMessage("A special-key fire no longer plays exactly one click.")
                .That(sound.Clicks).IsEqualTo(2);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TheFiredStepEndTimer_RunsAtRenderPriority()
    {
        // C1's end mechanism, reused: at Render the end tick stays behind the render the stepped
        // thickness queued, so a UI stall cannot collapse the step before a frame drew it.
        // Mutation: build the timer at Normal → RED.
        var probe = Open();
        try
        {
            Check.That(TimerPriority(probe.Window.KeyFiredStepTimer))
                .IsEqualTo(DispatcherPriority.Render);
            Check.That(probe.Window.KeyFiredStepInterval.TotalMilliseconds)
                .IsEqualTo(FiredFlash.DurationMs);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void AFiredKeyStep_EndsByItself()
    {
        // The real DispatcherTimer, pumped for real time: the outline returns to rest on its own.
        // Mutation: never start the end timer → still lit after 400 ms → RED.
        var probe = Open();
        try
        {
            var outline = Outlines(probe.Window)[TextKey];
            Feed(probe.Window, Centres(probe.Window)[TextKey], 1_000_000, 1_480_000);
            Check.That(probe.Window.IsKeyFiredStepLit).IsTrue();

            PumpFor(TimeSpan.FromMilliseconds(400));

            Check.WithCustomMessage("The fired step did not end by itself within 400 ms.")
                .That(probe.Window.IsKeyFiredStepLit).IsFalse();
            Check.That((outline.Outline.StrokeThickness, outline.Halo.StrokeThickness))
                .IsEqualTo((0.0, 0.0));
        }
        finally { probe.Dispose(); }
    }

    private sealed class RecordingSoundService : KeyboardSoundService
    {
        public int Clicks;
        // Base ctor points at a nonexistent wav → null player → the override is the only sound path.
        public RecordingSoundService() : base(new KeyboardSettings(), "no-such-file.wav") { }
        public override void PlayKeyClick() => Clicks++;
    }

    private static PhysicalRect CellRect(FrameworkElement cell)
    {
        var topLeft = cell.PointToScreen(new Point(0, 0));
        var bottomRight = cell.PointToScreen(new Point(cell.ActualWidth, cell.ActualHeight));
        return new PhysicalRect(topLeft.X, topLeft.Y,
            bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static DwellTarget PredictionTarget(KeyboardWindow w, string id) =>
        ((DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!).Targets.Single(t => t.Id == id);
}
