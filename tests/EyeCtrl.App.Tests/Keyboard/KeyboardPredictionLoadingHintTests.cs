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
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The prediction loading hint (lm-loading-hint ticket, ShowLMLoadingIndicator): one plain,
/// non-interactive text across the whole prediction bar while the FIRST prediction stack is
/// still building. Every condition of Behaviour 1–5 is pinned here at the real window — show,
/// hide per condition, never a dwell target, never in _renderedPredictions — plus the German
/// fit proof at the smallest keyboard width (the ticket's headless-measure option).
///
/// <para>Real window, never shown: the off-screen <c>WS_POPUP</c> HwndSource pattern of
/// <see cref="KeyboardPasswordBarTests"/>.</para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionLoadingHintTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class HelProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() =>
            new() { ["hello"] = 1000, ["help"] = 900, ["helmet"] = 800 };
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class Probe : IDisposable
    {
        public KeyboardViewModel Vm = null!;
        public KeyboardWindow Window = null!;
        public FakeSensitive Sensitive = new();
        public EyeCtrlSettings Settings = null!;
        public FrameworkElement Content = null!;
        private HwndSource _source = null!;

        public static Probe Open(double keySizeMultiplier = 1.0, bool withOrchestrator = false)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            var p = new Probe();
            var settings = new EyeCtrlSettings();
            settings.Keyboard.SmartCapitalizationEnabled = false;
            p.Settings = settings;
            p.Vm = new KeyboardViewModel(settings, new WeakReferenceMessenger(), sensitiveContext: p.Sensitive);
            if (withOrchestrator)
                p.Vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(new HelProvider(), "QWERTY")));
            p.Window = new KeyboardWindow();
            p.Window.SetViewModel(p.Vm);
            p.Window.LoadLayout(KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json")), keySizeMultiplier,
                keyDwellMs: 450, predictionDwellMs: 500, cooldownMs: 400);
            p.Window.SetSettings(settings);
            p.Window.SetPredictionBuildInFlight(true);
            p.Content = (FrameworkElement)p.Window.Content;
            p.Window.Content = null;
            p._source = new HwndSource(new HwndSourceParameters("lm-loading-hint-tests")
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

        public Border Hint() => (Border)Field("_predictionLoadingHint");
        public TextBlock HintText() => (TextBlock)Field("_predictionLoadingHintText");

        public object Field(string name) =>
            typeof(KeyboardWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Window)!;

        public List<string> Rendered() =>
            ((IEnumerable<PredictionResult>)Field("_renderedPredictions"))
            .Select(r => r.Word).ToList();

        public int DwellTargetCount() => ((DwellDetector)Field("_predictionDwellDetector")).Targets.Count;

        public void Gaze(long tUs) => Window.UpdateGaze(new PhysicalPoint(-5000, -5000), tUs);

        /// <summary>
        /// Re-measures the re-parented content and returns its size plus the first key row's
        /// position relative to the content — the measured-geometry pair the F4 test compares
        /// between the hint state and the bar state.
        /// </summary>
        public (double Width, double Height, double RowX, double RowY) MeasureContent()
        {
            Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Content.Arrange(new Rect(Content.DesiredSize));
            Content.UpdateLayout();
            var firstRow = (FrameworkElement)Window.KeyRows.Children[0];
            var pos = firstRow.TranslatePoint(new Point(0, 0), Content);
            return (Content.DesiredSize.Width, Content.DesiredSize.Height, pos.X, pos.Y);
        }

        public void Dispose() { _source.Dispose(); Window.Close(); }
    }

    [StaFact]
    public void WhileTheFirstBuildRuns_TheHintSpansTheBar_AndIsNeverATarget()
    {
        var p = Probe.Open();
        try
        {
            Check.WithCustomMessage("the hint must be visible while the first build runs")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);
            Check.WithCustomMessage("the bar must not show placeholder cells under the hint")
                .That(p.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.WithCustomMessage("the bar must stay empty of real cells while the hint owns it")
                .That(p.Window.PredictionBar.Children.Count).IsEqualTo(0);

            // E1 stays truthful: the hint is chrome, not a suggestion.
            Check.WithCustomMessage("the hint leaked into _renderedPredictions")
                .That(p.Rendered()).IsEmpty();
            Check.WithCustomMessage("the hint registered a dwell target")
                .That(p.DwellTargetCount()).IsEqualTo(0);

            // "Across the whole bar": the hint is exactly as wide as the key grid.
            var keyGridWidth = (double)p.Field("_keyGridWidth");
            Check.That(p.Hint().Width).IsEqualTo(keyGridWidth);
            Check.That(p.HintText().Text).IsEqualTo("Loading predictions…");
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheSettingTurnedOff_HidesTheHint_LiveAtRenderTime()
    {
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);

            p.Settings.Keyboard.ShowLMLoadingIndicator = false;
            p.Window.SetSettings(p.Settings); // the settings-apply path re-renders the bar

            Check.WithCustomMessage("ShowLMLoadingIndicator=false must hide the hint at render time")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(p.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void EmojiMode_HidesTheHint()
    {
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);

            p.Vm.IsEmojiMode = true;
            p.Window.UpdatePredictions();

            Check.WithCustomMessage("the hint must not show over the emoji panel")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);

            // And the direct path: EnterEmojiMode collapses it even without a re-render.
            p.Window.SetPredictionBuildInFlight(false);
            p.Vm.IsEmojiMode = false;
            p.Window.SetPredictionBuildInFlight(true);
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);
            typeof(KeyboardWindow).GetMethod("EnterEmojiMode",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p.Window, null);
            Check.WithCustomMessage("EnterEmojiMode must collapse the hint itself (no UpdatePredictions runs on that path)")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void ASensitiveField_HidesTheHint_TheBarStaysEmptyForPrivacy()
    {
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);

            p.Sensitive.IsSensitiveActive = true; // a password field takes focus
            p.Gaze(1_000_000);                    // the first sample rebuilds the bar

            Check.WithCustomMessage("a hint over a password field reads as suggestions loading for it")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(p.Rendered()).IsEmpty();
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void AStackAlreadyBound_NoHint_EvenWhileABuildRuns()
    {
        // Behaviour 4: later rebuilds keep the old stack serving — the bar is not "empty
        // because the stack is building", so no hint.
        var p = Probe.Open(withOrchestrator: true);
        try
        {
            Check.WithCustomMessage("a serving stack means the bar is not waiting: no hint")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(p.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheBuildEnding_HidesTheHint()
    {
        // The failed/cancelled/superseded arm: BuildInFlightChanged(false) is the only input
        // that moves, and the hint must go with it.
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);

            p.Window.SetPredictionBuildInFlight(false);

            Check.WithCustomMessage("a build that ended in any way must leave no hint")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void PredictionTurnedOff_NoHint_TheBarCollapses()
    {
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);

            p.Settings.Keyboard.PredictionEnabled = false;
            p.Window.SetSettings(p.Settings);

            Check.WithCustomMessage("prediction off collapses the bar; the hint may not outlive it")
                .That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(p.Window.PredictionBar.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void WithAStackServing_TheBuildTransitions_ReRenderNothing()
    {
        // Review 01 F2: with a stack bound the hint can never show, so a later rebuild's
        // start and end must NOT re-render — no ClearTargets, no LM query, no E1 render-clock
        // reset. The rendered list's IDENTITY is the no-render proof: every render assigns a
        // fresh list.
        var p = Probe.Open(withOrchestrator: true);
        try
        {
            p.Vm.CurrentInput = "hel";
            p.Window.UpdatePredictions();
            var rendered = p.Field("_renderedPredictions");
            Check.WithCustomMessage("arrange: the bar must be serving suggestions")
                .That(((IEnumerable<PredictionResult>)rendered).Any()).IsTrue();

            p.Window.SetPredictionBuildInFlight(true);   // a later rebuild starts
            Check.WithCustomMessage("a start with a stack serving re-rendered the bar")
                .That(p.Field("_renderedPredictions")).IsSameReferenceAs(rendered);

            p.Window.SetPredictionBuildInFlight(false);  // ... and ends
            Check.WithCustomMessage("an end with a stack serving re-rendered the bar")
                .That(p.Field("_renderedPredictions")).IsSameReferenceAs(rendered);

            // Control (the gate is not a no-op): drop the stack — the FIRST-build shape —
            // and the same start MUST render (the hint appears).
            p.Vm.SetOrchestrator(null);
            p.Window.SetPredictionBuildInFlight(false);  // back to the resting state first
            p.Window.SetPredictionBuildInFlight(true);
            Check.WithCustomMessage("control: the first build's start must still re-render")
                .That(p.Field("_renderedPredictions")).Not.IsSameReferenceAs(rendered);
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheHintStateAndTheBarState_KeepIdenticalContentGeometry()
    {
        // Review 01 F4: MEASURED, not arithmetic — the window content's size and the first
        // key row's position are identical with the hint up and with the (empty) bar up, so
        // the hint→suggestions transition can never resize the window or move a key under a
        // resting gaze, whatever the cells' margins become.
        var p = Probe.Open();
        try
        {
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Visible);
            var hint = p.MeasureContent();

            p.Window.SetPredictionBuildInFlight(false); // the bar state: empty cells, no hint
            Check.That(p.Hint().Visibility).IsEqualTo(Visibility.Collapsed);
            var bar = p.MeasureContent();

            Check.WithCustomMessage(
                    $"content width differs between the states (hint {hint.Width:F2}, bar {bar.Width:F2})")
                .That(bar.Width).IsCloseTo(hint.Width, 0.01);
            Check.WithCustomMessage(
                    $"content height differs between the states (hint {hint.Height:F2}, bar {bar.Height:F2})")
                .That(bar.Height).IsCloseTo(hint.Height, 0.01);
            Check.WithCustomMessage(
                    $"the key rows moved horizontally (hint {hint.RowX:F2}, bar {bar.RowX:F2})")
                .That(bar.RowX).IsCloseTo(hint.RowX, 0.01);
            Check.WithCustomMessage(
                    $"the key rows moved vertically (hint {hint.RowY:F2}, bar {bar.RowY:F2})")
                .That(bar.RowY).IsCloseTo(hint.RowY, 0.01);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void TheGermanHintFitsTheBar_AtTheSmallestKeyboardWidth()
    {
        // The ticket's fit proof: no fit class exists for a prediction cell, so the German
        // (the longer of the two shipped texts) is measured headlessly against the bar built
        // at KeySizeMultiplier 0.5 — the setting's validated floor.
        var german = StringTable.Load(
            EyeCtrl.Core.Enums.UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Localization"));
        StringTable.Activate(german);
        var p = Probe.Open(keySizeMultiplier: 0.5);
        try
        {
            Check.WithCustomMessage("arrange: the German table did not resolve the hint key")
                .That(p.HintText().Text).IsEqualTo("Vorhersagen werden geladen");

            // Unconstrained measure of the text against the explicit hint width.
            p.HintText().Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = p.HintText().DesiredSize.Width;
            var barWidth = (double)p.Field("_keyGridWidth");
            Check.WithCustomMessage(
                    $"the German hint ({textWidth:F1} DIP) must fit the smallest bar ({barWidth:F1} DIP) " +
                    "without trimming")
                .That(textWidth).IsStrictlyLessThan(barWidth);
            // The footprint itself is pinned MEASURED, against the bar state, in
            // TheHintStateAndTheBarState_KeepIdenticalContentGeometry (review 01 F4).
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            p.Dispose();
        }
    }
}
