namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// F17 (owner's option A): the prediction bar renders at most FOUR slots, each 1.6 key-heights
/// tall with 1.6x the label size, and the already-typed part of a completion is grey. The stored
/// setting keeps its 1-5 ceiling; only the renderer clamps.
///
/// <para>
/// The window is never shown: its content is lifted into an off-screen <c>WS_POPUP</c> HwndSource
/// at -30000 (never WS_VISIBLE), like the neighbouring prediction tests.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionBarLayoutTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int KeyDwellMs = 450;
    private const int PredictionDwellMs = 500;
    private const double BaseKeySize = 70;
    // The production scale (KeyboardWindow.SuggestionScale): 1.6 x the key height and 1.6 x the
    // label size. Restated here because the assertions must be arithmetic, not "whatever the
    // code did" (a code-vs-code comparison would pass against any scale).
    private const double SuggestionScale = 1.6;

    private sealed class InMemoryDictProvider(Dictionary<string, int> words) : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new(words);
        public void Save(Dictionary<string, int> w) { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static Probe Open(Dictionary<string, int> words, double keySizeMultiplier = 1.0,
        double scale = 1.0, Action<EyeCtrlSettings>? configure = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        configure?.Invoke(settings);

        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY")));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.SetSettings(settings);
        w.LoadLayout(ShippedQwerty(), keySizeMultiplier,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: 0);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-layout-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        // Pin the effective scale independent of the machine's DPI (the HwndSource pattern from
        // KeyboardPredictionBarStabilityTests, with the scale as a parameter so the DPI 1.0 and
        // 2.0 target-geometry halves run on one machine).
        var factor = scale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        Measure(content);

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw, the dwell targets would never "
                + "publish, and every assertion here would pass VACUOUSLY.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source);
    }

    private static void Measure(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    private static void Layout(Probe probe, string input)
    {
        probe.Vm.CurrentInput = input;
        probe.Window.UpdatePredictions();
        Measure(probe.Content);
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
    }

    private static List<Border> Cells(KeyboardWindow w) =>
        w.PredictionBar.Children.Cast<Border>().ToList();

    private static List<string> RenderedWords(KeyboardWindow w) =>
        Cells(w).Select(CellText).Where(text => text.Length > 0).ToList();

    /// <summary>The cell's label, unwrapping the DwellOutlineOverlay grid and the padding border.</summary>
    private static TextBlock? FindLabel(UIElement? element) => element switch
    {
        TextBlock tb => tb,
        Border border => FindLabel(border.Child),
        Panel panel => panel.Children.Cast<UIElement>().Select(FindLabel)
            .FirstOrDefault(label => label is not null),
        ContentControl content when content.Content is UIElement child => FindLabel(child),
        _ => null
    };

    private static TextBlock Label(Border cell)
    {
        var label = FindLabel(cell);
        Check.WithCustomMessage("the prediction cell carries no TextBlock label").That(label).IsNotNull();
        return label!;
    }

    // F17's labels are two Runs when the typed prefix is greyed; TextBlock.Text stays empty for
    // an inline-built label, so read the runs' text.
    private static string CellText(Border cell) =>
        string.Concat(FindLabel(cell)?.Inlines.OfType<Run>().Select(run => run.Text) ?? []);

    private static List<Run> Runs(Border cell) => Label(cell).Inlines.OfType<Run>().ToList();

    private static PhysicalRect CellRect(FrameworkElement cell)
    {
        var tl = cell.PointToScreen(new Point(0, 0));
        var br = cell.PointToScreen(new Point(cell.ActualWidth, cell.ActualHeight));
        return new PhysicalRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    private static List<DwellTarget> RegisteredTargets(DwellDetector detector) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<string, DwellTarget>)typeof(DwellDetector)
            .GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(detector)!).Values.ToList();

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, int> HelWords() => new()
    {
        ["hello"] = 1000, ["help"] = 900, ["helm"] = 800, ["helium"] = 700,
        ["helmet"] = 600, ["held"] = 500,
    };

    [StaFact]
    public void TheBarRendersAtMostFourSlots_EvenWithTheStoredFive()
    {
        using var probe = Open(HelWords(), configure: s => s.Keyboard.MaxSuggestions = 5);
        try
        {
            Layout(probe, "hel");

            Check.WithCustomMessage(
                    $"bar: {string.Join("|", RenderedWords(probe.Window))}; MaxSuggestions is 5, but "
                    + "the renderer must clamp to 4 slots.")
                .That(probe.Window.PredictionBar.Children.Count).IsEqualTo(4);
            Check.WithCustomMessage(
                    "all four rendered slots must hold a word (six candidates match 'hel')")
                .That(RenderedWords(probe.Window)).HasSize(4);
        }
        finally { probe.Dispose(); }
    }

    [StaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FewerSlots_WhenTheSettingIsLower(int maxSuggestions)
    {
        using var probe = Open(HelWords(), configure: s => s.Keyboard.MaxSuggestions = maxSuggestions);
        try
        {
            Layout(probe, "hel");

            Check.WithCustomMessage($"MaxSuggestions={maxSuggestions} must render that many slots")
                .That(probe.Window.PredictionBar.Children.Count).IsEqualTo(maxSuggestions);
        }
        finally { probe.Dispose(); }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(1.75)]
    public void CellHeightAndLabelFont_Are1_6TimesTheKey(double keySizeMultiplier)
    {
        using var probe = Open(HelWords(), keySizeMultiplier);
        try
        {
            Layout(probe, "hel");
            var keySize = BaseKeySize * keySizeMultiplier;
            var cells = Cells(probe.Window);

            foreach (var cell in cells)
            {
                Check.WithCustomMessage(
                        $"cell height {cell.Height} at {keySizeMultiplier}x: the bar's slots are "
                        + $"{SuggestionScale} x the {keySize} DIP key")
                    .That(cell.Height).IsCloseTo(keySize * SuggestionScale, 0.01);
            }

            // Every cell - filled and empty - is the same height, or the bar would change size
            // with the suggestion count.
            Check.That(cells.Select(c => c.Height).Distinct().ToList()).HasSize(1);

            var label = Label(cells[0]);
            var expectedFont = keySize * ContainerRatio.Small * SuggestionScale;
            Check.WithCustomMessage(
                    $"label font {label.FontSize} at {keySizeMultiplier}x: today's label is "
                    + $"keySize * {ContainerRatio.Small}, and F17 scales it by {SuggestionScale}")
                .That(label.FontSize).IsCloseTo(expectedFont, 0.01);
            Check.That(label.FontSize).IsGreaterOrEqualThan(Legibility.MinimumGlyphFontSize);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void TypedPrefix_IsGrey_AndTheRestKeepsTheTypeColour_CaseInsensitively()
    {
        // SmartCapitalization is on (the default): the display word is "Hello" while the typed
        // input is the lowercase "hel", so the split must be case-insensitive.
        using var probe = Open(HelWords());
        try
        {
            Layout(probe, "hel");
            var first = Cells(probe.Window)[0];
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(CellText(first)).IsEqualTo("Hello");

            var runs = Runs(first);
            Check.WithCustomMessage("a completion must split into the typed and the added part")
                .That(runs).HasSize(2);
            Check.WithCustomMessage("the typed run must be exactly the input's letters")
                .That(runs[0].Text).IsEqualTo("Hel");
            Check.WithCustomMessage("the rest of the word must follow the typed run")
                .That(runs[1].Text).IsEqualTo("lo");
            Check.WithCustomMessage(
                    "the typed run must use the theme's de-emphasised prediction-prefix brush")
                .That(runs[0].Foreground).IsSameReferenceAs(Tokens.BrushPredictionPrefix);
            Check.WithCustomMessage("the added part must keep the type's normal colour")
                .That(runs[1].Foreground).IsSameReferenceAs(Tokens.BrushTextPrimary);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ANextWordSuggestion_HasNoGreyRun()
    {
        using var probe = Open(new Dictionary<string, int>
        {
            ["ist"] = 100, ["steht"] = 90, ["leer"] = 80, ["hier"] = 70,
        });
        try
        {
            var orchestrator = (PredictionOrchestrator)typeof(KeyboardViewModel)
                .GetField("_orchestrator", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(probe.Vm)!;
            orchestrator.LearnPair("Haus", "ist");
            orchestrator.LearnPair("Haus", "ist");

            // A word boundary: nothing typed, so nothing may be greyed - even though the whole
            // next word trivially "starts with" the empty input.
            foreach (var letter in "Haus")
                probe.Vm.ActivateKey(letter.ToString(), null);
            probe.Vm.HandleSpace();
            probe.Window.UpdatePredictions();
            Measure(probe.Content);

            var next = Cells(probe.Window)[0];
            Check.WithCustomMessage($"bar: {string.Join("|", RenderedWords(probe.Window))}")
                .That(CellText(next)).IsEqualTo("ist");
            Check.WithCustomMessage("a next-word suggestion must be one run, all in its type colour")
                .That(Runs(next)).HasSize(1);
            Check.That(Runs(next)[0].Foreground).IsSameReferenceAs(Tokens.BrushAccentSuccess);
        }
        finally { probe.Dispose(); }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void DwellTargets_MatchTheCells_AtDpi1And2(double scale)
    {
        using var probe = Open(HelWords(), scale: scale);
        try
        {
            Layout(probe, "hel");
            var cells = Cells(probe.Window).Take(RenderedWords(probe.Window).Count).ToList();
            var detector = PredictionDetector(probe.Window);
            var before = CellRect(cells[0]);

            // First pass registers.
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 30_000);
            var registered = RegisteredTargets(detector);
            Check.That(registered).HasSize(cells.Count);

            // Move the bar before the second pass: registration alone cannot be what the
            // assertions below measure, because the cells and their targets must move together.
            // A placement path that writes only Center (UpdateTargetCenter) leaves a rect target's
            // Bounds frozen where it first published, which this catches on every DPI.
            probe.Window.PredictionBar.Margin = new Thickness(150, 0, 0, 6);
            Measure(probe.Content);
            var after = CellRect(cells[0]);
            Check.WithCustomMessage(
                    $"the bar did not actually move at scale {scale} (before {before.X:F1},{before.Y:F1} "
                    + $"-> after {after.X:F1},{after.Y:F1}), so the rebase assertion would be vacuous.")
                .That(Math.Abs(after.X - before.X) + Math.Abs(after.Y - before.Y)).IsStrictlyGreaterThan(10);

            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 60_000);
            var rebased = RegisteredTargets(detector);

            Check.That(rebased).HasSize(cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                var expected = CellRect(cells[i]);
                var target = rebased.Single(t => t.Id == $"pred_{i}");
                Check.WithCustomMessage($"pred_{i} at scale {scale}: target has no rect geometry")
                    .That(target.Bounds.HasValue).IsTrue();
                var actual = target.Bounds!.Value;
                Check.WithCustomMessage(
                        $"pred_{i} at scale {scale}: target {actual.Width}x{actual.Height} at "
                        + $"({actual.X},{actual.Y}) vs cell {expected.Width}x{expected.Height} at "
                        + $"({expected.X},{expected.Y})")
                    .That(actual).IsEqualTo(expected);
            }
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// The owner's screen is 3840x2400 at 200% (1920x1200 DIP) and the keyboard is docked Top and
    /// Bottom. At 1.75x the four-row letters page measures 736 DIP and the five-row Fn page
    /// 862.5 DIP (before F17 they were 662.5 / 789): both still fit, so the placement clamps are
    /// not what keeps the window on screen.
    /// </summary>
    [StaTheory]
    [InlineData("letters")]
    [InlineData("function")]
    public void TallerKeyboard_FitsTheOwnersScreen_At1_75x_BothDocks(string page)
    {
        using var probe = Open(HelWords(), keySizeMultiplier: 1.75);
        try
        {
            probe.Vm.SwitchSubLayout(page);
            Measure(probe.Content);
            var width = probe.Content.ActualWidth;
            var height = probe.Content.ActualHeight;
            var workArea = new Rect(0, 0, 1920, 1200);

            Check.WithCustomMessage($"{page} at 1.75x: width {width} over the 1920 DIP work area")
                .That(width).IsLessOrEqualThan(workArea.Width);
            Check.WithCustomMessage($"{page} at 1.75x: height {height} over the 1200 DIP work area")
                .That(height).IsLessOrEqualThan(workArea.Height);
            // The arithmetic the ticket states: chrome 22 + bar (196 + 6 margins + 6 grid gap) +
            // rows (4 * 126.5 or 5 * 126.5). Before F17 the bar was 134.5, so the window grew by
            // 0.6 * 122.5 = 73.5 DIP (662.5 -> 736 and 789 -> 862.5).
            var expectedHeight = page == "letters" ? 736.0 : 862.5;
            Check.WithCustomMessage(
                    $"{page} at 1.75x: measured {height} DIP, expected {expectedHeight} DIP")
                .That(height).IsCloseTo(expectedHeight, 0.5);

            foreach (var edge in new[] { "top", "bottom" })
            {
                probe.Window.SetScreenEdge(edge);
                probe.Window.PlaceForShow(workArea, width, height);

                Check.WithCustomMessage($"{page} at 1.75x/{edge}: Top {probe.Window.Top}")
                    .That(probe.Window.Top).IsGreaterOrEqualThan(workArea.Top - 0.01);
                Check.WithCustomMessage(
                        $"{page} at 1.75x/{edge}: bottom edge {probe.Window.Top + height} over the "
                        + "1200 DIP work area")
                    .That(probe.Window.Top + height).IsLessOrEqualThan(workArea.Bottom + 0.01);
                Check.WithCustomMessage($"{page} at 1.75x/{edge}: why did the edge not take?")
                    .That(probe.Window.Top)
                    .IsEqualTo(edge == "top" ? workArea.Top : workArea.Bottom - height);
            }
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// F17 grows the window by 0.6 key-heights (73.5 DIP at 1.75x), so the toolbar yield and the
    /// dock clamps must still keep it on screen. The yield is one toolbar extent toward centre;
    /// the taller window still has room for it on both docks.
    /// </summary>
    [StaTheory]
    [InlineData("top")]
    [InlineData("bottom")]
    public void TallerKeyboard_StillYieldsToTheToolbar_AndStaysOnScreen(string edge)
    {
        using var probe = Open(HelWords(), keySizeMultiplier: 1.75);
        try
        {
            var height = probe.Content.ActualHeight;
            var workArea = new Rect(0, 0, 1920, 1200);
            const double toolbarExtent = 60;

            probe.Window.SetToolbarOccupancyState(true, edge, toolbarExtent);
            probe.Window.SetScreenEdge(edge);
            probe.Window.PlaceForShow(workArea, probe.Content.ActualWidth, height);

            var undockedTop = edge == "top" ? 0 : workArea.Bottom - height;
            var expectedTop = edge == "top" ? toolbarExtent : undockedTop - toolbarExtent;
            Check.WithCustomMessage(
                    $"the {edge}-docked keyboard did not yield {toolbarExtent} DIP toward centre "
                    + $"(Top {probe.Window.Top}, undocked {undockedTop})")
                .That(probe.Window.Top).IsCloseTo(expectedTop, 0.5);
            Check.That(probe.Window.Top).IsGreaterOrEqualThan(workArea.Top - 0.01);
            Check.WithCustomMessage("the yielded taller keyboard ran off the bottom edge")
                .That(probe.Window.Top + height).IsLessOrEqualThan(workArea.Bottom + 0.01);
        }
        finally { probe.Dispose(); }
    }
}
