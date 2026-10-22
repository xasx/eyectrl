namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// T5 review-01 B1: the control column hugs the card edge AWAY from the tail, its X derived
/// from the placement's CARD width - so no visible disc and no hit cell ever overlaps the body
/// text, on any side (R11). The chip shrink anchors on that column edge, so the Undo cell stays
/// exactly under a resting gaze on every side the anchor paths reach.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteColumnPlacementTests
{
    internal const string Sample = "Meds at 8:00. Call Anna after lunch.";

    public static IEnumerable<object[]> SidesAndStates()
    {
        var sides = new[]
        {
            (NoteSide.Right, 600.0, 500.0), (NoteSide.Left, 1500.0, 600.0),
            (NoteSide.Below, 960.0, 200.0), (NoteSide.Above, 960.0, 1000.0),
        };
        foreach (var (side, x, y) in sides)
            foreach (var state in new[] { NoteState.Display, NoteState.Edit })
                foreach (var size in new[] { 18, 28, 48 })
                    yield return new object[] { side, x, y, state, size };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : class
    {
        if (root is T match) yield return match;
        switch (root)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    foreach (var descendant in Descendants<T>(child))
                        yield return descendant;
                break;
            case Border { Child: { } borderChild }:
                foreach (var descendant in Descendants<T>(borderChild))
                    yield return descendant;
                break;
        }
    }

    // R11 (adopted): on every side, no visible control disc (and no hit cell) overlaps the body
    // text block. The card width is the ONE source - drawn cells, dwell targets and this probe
    // all read the same placement.
    [StaTheory]
    [MemberData(nameof(SidesAndStates))]
    public void R11_TheControlColumn_NeverOverlapsTheBodyText(NoteSide side, double ax, double ay, NoteState state, int size)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("probe-note");
            InvisibleTestWindow.Prepare(window);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(state);
            window.SetTextSize(size);
            var card = new Size(NoteLayout.CardWidthDip(size), NoteLayout.ControlsColumnHeightDip);
            var placement = NoteLayout.Place(new PhysicalPoint(ax * 2, ay * 2), side, card,
                new Rect(0, 0, 1920, 1200), Array.Empty<Rect>(), 2.0)!;
            Check.That(placement.Side).IsEqualTo(side);
            window.ApplyPlacement(placement);
            window.SetText(Sample);
            window.UpdateContent();

            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            var text = Descendants<TextBlock>(canvas).First(t => Math.Abs(t.FontSize - size) < 0.01);
            var textBounds = text.TransformToAncestor(canvas).TransformBounds(new Rect(text.RenderSize));
            var cardLocal = NoteLayout.RectLocal(placement);
            Check.WithCustomMessage($"{side}/{state}/{size}: text {textBounds} must lie inside the card {cardLocal}")
                .That(cardLocal.Contains(textBounds)).IsTrue();

            var visible = 0;
            foreach (var (suffix, disc) in window.ButtonsForTest())
            {
                if (disc.Visibility != Visibility.Visible) continue;
                visible++;
                var discBounds = new Rect(Canvas.GetLeft(disc), Canvas.GetTop(disc), disc.Width, disc.Height);
                Check.WithCustomMessage($"{side}/{state}/{size}: {suffix} disc {discBounds} overlaps the text {textBounds}")
                    .That(discBounds.IntersectsWith(textBounds)).IsFalse();
            }
            Check.That(visible).IsStrictlyGreaterThan(0);
            foreach (var slot in NoteLayout.SlotsFor(state))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement, slot);
                var cell = new Rect(cx - 40, cy - 40, 80, 80);
                Check.WithCustomMessage($"{side}/{state}/{size}: cell {slot} {cell} overlaps the text {textBounds}")
                    .That(cell.IntersectsWith(textBounds)).IsFalse();
                Check.WithCustomMessage($"{side}/{state}/{size}: cell {slot} {cell} must lie inside the card {cardLocal}")
                    .That(cardLocal.Contains(cell)).IsTrue();
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // T5 review-02 F2 + fixup-03 G1: the disc glyph is a GAZE-sized INK area. The Solo-size
    // half pins the glyph against ContainerRatio.Small (a mutant shrinking it to Small goes
    // RED); the centring half now measures the INK - fixup-03 G1 deliberately lays the glyph
    // BOX off-centre (the box top-left carries the ink centre into the disc centre), so the
    // old box-centre formula fails exactly when the fix is in. Mutants for the ink centring
    // itself live in NoteGlyphInkTests, which renders and pixel-diffs the REAL ink.
    [StaFact]
    public void TheDiscGlyph_IsSoloSized_AndCentredInTheDisc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("glyph-pin");
        InvisibleTestWindow.Prepare(window);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        window.SetState(NoteState.Display);

        double disc = LayoutConstants.NoteButtonRadius * 2;
        double expected = disc * ContainerRatio.Solo;
        foreach (var (suffix, label) in window.LabelsForTest())
        {
            Check.WithCustomMessage($"the {suffix} glyph must be Solo-sized (want >= {expected:0.#}, got {label.FontSize:0.#})")
                .That(label.FontSize).IsGreaterOrEqualThan(expected);
            var centre = window.ButtonsForTest()[suffix]; // the label key IS the full id
            // The ink relative to the label box's top-left, from the SAME face the label
            // renders (its own FontFamily), so the pin cannot drift from the shape on screen.
            var formatted = new FormattedText(label.Text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(label.FontFamily.Source), label.FontSize,
                Brushes.Black, null, TextFormattingMode.Ideal, 1.0);
            Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            double dx = Math.Abs((Canvas.GetLeft(label) + ink.Left + (ink.Width / 2)) - (Canvas.GetLeft(centre) + window.ButtonRadiusForTest));
            double dy = Math.Abs((Canvas.GetTop(label) + ink.Top + (ink.Height / 2)) - (Canvas.GetTop(centre) + window.ButtonRadiusForTest));
            Check.WithCustomMessage($"the {suffix} glyph ink must sit centred in its disc (dx {dx:0.##}, dy {dy:0.##})")
                .That(dx < 0.01 && dy < 0.01).IsTrue();
        }
        Check.That(window.LabelsForTest().Count).IsStrictlyGreaterThan(0);
    }

    // The Left half of the chip-shrink anchor (review-03 G2): with the full card on the Left
    // side the column hugs the WINDOW LEFT, so the shrunk chip must keep the window left too,
    // and the Undo cell keeps its pre-shrink centre. A mutant that anchors the Left chip on
    // its window right (the other sides' branch) slides the Undo cell sideways - RED here.
    [StaFact]
    public void R16b_TheShrink_KeepsTheUndoCell_OnTheLeftSide()
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(3600, 1200)); // DIP (1800, 600): Right cannot fit, the cycle falls to Left
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);
            Check.WithCustomMessage("the probe must start from a Left full card, or the Left anchor path never runs")
                .That(manager.PlacementForTest(id)!.Side).IsEqualTo(NoteSide.Left);
            var before = NoteTestBench.CellCentre(manager, id, 0);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage($"the Left shrink must keep the Undo cell at {before}")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(before);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }

    private static (NoteManager Manager, WeakReferenceMessenger Messenger) MakeManager()
    {
        var messenger = new WeakReferenceMessenger();
        return (new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider), messenger);
    }

    private static void Hold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(centre.X + 10_000, centre.Y), t, IsInGap: false)));
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(centre, t, IsInGap: false)));
    }

    // R16's shrink half: the shrunk chip keeps the Undo cell at its pre-shrink centre.
    // Below is the odd-man side here: it shares the Right window-right branch of the
    // column-edge anchor.
    [StaFact]
    public void R16_TheShrink_KeepsTheUndoCell_OnTheBelowSide()
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1920, 200)); // DIP (960, 100): Below
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);
            Check.That(manager.PlacementForTest(id)!.Side).IsEqualTo(NoteSide.Below);
            var before = NoteTestBench.CellCentre(manager, id, 0);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage($"the Below shrink must keep the Undo cell at {before}")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(before);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }
}
