namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
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

    public static IEnumerable<object[]> Sides() => new[]
    {
        new object[] { NoteSide.Right, 700.0, 500.0 },
        new object[] { NoteSide.Left, 1500.0, 600.0 },
        new object[] { NoteSide.Above, 400.0, 1000.0 },
    };

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
    [MemberData(nameof(Sides))]
    public void R11_TheControlColumn_NeverOverlapsTheBodyText(NoteSide side, double ax, double ay)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            const int size = NoteSettings.DefaultTextSizeDip;
            var window = new NoteWindow("probe-note");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Display);
            window.SetTextSize(size);
            var card = new Size(NoteLayout.CardWidthDip(size), NoteLayout.ControlsColumnHeightDip);
            var placement = NoteLayout.Place(new PhysicalPoint(ax * 2, ay * 2), side, card,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!;
            Check.That(placement.Side).IsEqualTo(side);
            window.ApplyPlacement(placement);
            window.SetText(Sample);
            window.UpdateContent();

            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            var text = Descendants<TextBlock>(canvas).First(t => Math.Abs(t.FontSize - size) < 0.01);
            var textBounds = text.TransformToAncestor(canvas).TransformBounds(new Rect(text.RenderSize));

            foreach (var (suffix, disc) in window.ButtonsForTest())
            {
                if (disc.Visibility != Visibility.Visible) continue;
                var discBounds = new Rect(Canvas.GetLeft(disc), Canvas.GetTop(disc), disc.Width, disc.Height);
                Check.WithCustomMessage($"{side}: the visible '{suffix}' disc {discBounds} overlaps the body text {textBounds}")
                    .That(discBounds.IntersectsWith(textBounds)).IsFalse();
            }
            foreach (var slot in NoteLayout.SlotsFor(NoteState.Display))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement.Side, slot, placement.Card.Width);
                var cell = new Rect(cx - 40, cy - 40, 80, 80);
                Check.WithCustomMessage($"{side}: hit cell {slot} {cell} overlaps the body text {textBounds}")
                    .That(cell.IntersectsWith(textBounds)).IsFalse();
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
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

    // B1's shrink half: on every side the anchor paths reach, the shrunk chip keeps the Undo
    // cell at its pre-shrink centre. Below never arises from the pure cycle for a floor-height
    // card (Right or Left always fits first) - it shares the Right branch of the column-edge
    // anchor; R12 pins the Left branch through a T8 move.
    [StaTheory]
    [MemberData(nameof(Sides))]
    public void TheShrink_KeepsTheUndoCellFixed_OnEverySide(NoteSide side, double ax, double ay)
    {
        var (manager, _) = MakeManager();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(ax * 2, ay * 2));
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);
            Check.That(manager.PlacementForTest(id)!.Side).IsEqualTo(side);

            var undoBefore = NoteTestBench.CellCentre(manager, id, 0);
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            Check.WithCustomMessage($"{side}: the shrunk chip must keep the Undo cell at {undoBefore}")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); manager.Dispose(); }
    }
}
