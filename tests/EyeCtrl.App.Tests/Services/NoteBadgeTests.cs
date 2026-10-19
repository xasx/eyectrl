namespace EyeCtrl.App.Tests.Services;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T6 P5: the number badges. The numbering rules — the LOWEST free positive integer at
/// creation, kept through Edit/Display/Move/PendingUndo (Undo restores the same numeral),
/// released only when the note is finally gone (chip expiry, a chip yielded at capacity, an
/// empty new note discarded or Done); a chip still HOLDS its number. The visibility rule —
/// badges show on card notes only while TWO OR MORE notes are not chips; chips never show one
/// and never count. And the badge is pure chrome: never a dwell target, never chrome for
/// anybody else.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteBadgeTests
{
    private sealed class FakeClock
    {
        public long NowMs;
        public long Read() => NowMs;
    }

    private sealed class FakeUndoTimer : INoteUndoTimer
    {
        private Action? _tick;
        public bool IsRunning { get; private set; }
        public void Start(Action onTick) { _tick = onTick; IsRunning = true; }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
        public void RaiseTick() => _tick?.Invoke();
    }

    private static NoteManager MakeManager(out FakeClock clock, out FakeUndoTimer timer)
    {
        clock = new FakeClock();
        timer = new FakeUndoTimer();
        return new NoteManager(new WeakReferenceMessenger(),
            hitTester: InactiveNoteHitTester.Instance, clockMs: clock.Read, undoTimer: timer,
            workAreaDip: NoteTestBench.WorkAreaProvider);
    }

    private static void Sample(NoteManager manager, PhysicalPoint point, long tUs) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, tUs, IsInGap: false)));

    private static void ArmThenHold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            Sample(manager, new PhysicalPoint(centre.X + 10_000, centre.Y), t);
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            Sample(manager, centre, t);
    }

    /// <summary>Creates a note, types into it and commits it — a saved Display note with a real
    /// window (the same helper NoteUndoTests drives its table with).</summary>
    private static string SavedNote(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static PhysicalPoint CellCentre(NoteManager manager, string id, int slot) =>
        NoteTestBench.CellCentre(manager, id, slot);

    /// <summary>The drawn badge's numeral, read off the real canvas element.</summary>
    private static string? DrawnBadge(NoteWindow window)
    {
        var disc = window.BadgeDiscForTest;
        var label = window.BadgeLabelForTest;
        if (disc is null || label is null) return null;
        Check.That(disc.Visibility).IsEqualTo(Visibility.Visible);
        return label.Text;
    }

    /// <summary>Physical-rect equality within a rounding DIP (DpiHelper rounding at 2.0).</summary>
    private static bool SameRect(PhysicalRect a, PhysicalRect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    [StaFact]
    public void ANewNote_TakesTheLowestFreeNumber_AndReleaseReusesIt()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1200, 1000));
            var b = SavedNote(manager, "b", new PhysicalPoint(1200, 1000));
            Check.WithCustomMessage("the first two notes take 1 and 2")
                .That(manager.ActiveNotes.Select(n => n.Number)).ContainsExactly(1, 2);

            // A new note created and finished EMPTY is a final removal (Done on a new empty
            // draft) — it takes 3 on creation and releases it at removal.
            manager.CreateNoteAt(new PhysicalPoint(1200, 1000));
            var c = manager.ActiveNotes[^1].Id;
            Check.WithCustomMessage("the third creation takes 3").That(manager.ActiveNotes[^1].Number).IsEqualTo(3);
            manager.FinishEdit(c, commit: true);
            Check.That(manager.ActiveNotes).HasSize(2);

            var d = SavedNote(manager, "d", new PhysicalPoint(1200, 1000));
            Check.WithCustomMessage("the lowest FREE number is reused — 3 was released")
                .That(manager.ActiveNotes.First(n => n.Id == d).Number).IsEqualTo(3);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheNumberIsKeptAcrossRemoveUndo_AndAChipStillHoldsIt()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            SavedNote(manager, "b", new PhysicalPoint(1200, 100));
            SavedNote(manager, "c", new PhysicalPoint(1200, 100));
            Check.That(manager.ActiveNotes.Select(n => n.Number)).ContainsExactly(1, 2, 3);

            // ✕ A into the Removed chip: the chip HOLDS its number, so the next pin cannot take 1.
            ArmThenHold(manager, CellCentre(manager, a, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            var d = SavedNote(manager, "d", new PhysicalPoint(1200, 100));
            Check.WithCustomMessage("a note pinned over a chip must not take the chip's number")
                .That(manager.ActiveNotes.First(n => n.Id == d).Number).IsEqualTo(4);

            // Undo restores the SAME number.
            ArmThenHold(manager, CellCentre(manager, a, 0), startUs: 2_000_000);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("Undo must restore the note's own number")
                .That(manager.ActiveNotes[0].Number).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheNumberIsReleased_OnlyWhenTheNoteIsFinallyGone()
    {
        using var manager = MakeManager(out var clock, out var timer);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            SavedNote(manager, "b", new PhysicalPoint(1200, 100));

            ArmThenHold(manager, CellCentre(manager, a, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // Chip expiry: the injected clock walks the 8 s Undo window, a fresh sample per
            // tick (the countdown's condition 3), the sentinel tester exempting the own-window
            // conjunct — the same drive NoteUndoTests uses.
            long tUs = 2_000_000;
            for (int i = 0; i < 90 && manager.ActiveNotes[0].State == NoteState.PendingUndo; i++)
            {
                clock.NowMs += 100;
                Sample(manager, new PhysicalPoint(-50_000, -50_000), tUs += 100_000);
                timer.RaiseTick();
            }
            Check.WithCustomMessage("the chip must have expired to a final removal")
                .That(manager.ActiveNotes.Count).IsEqualTo(1);

            var c = SavedNote(manager, "c", new PhysicalPoint(1200, 100));
            Check.WithCustomMessage("expiry released 1 — the next note reuses it")
                .That(manager.ActiveNotes.First(n => n.Id == c).Number).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void BadgesShow_OnlyWhileTwoOrMoreNonChipNotesExist_AndChipsNeverShowOne()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            Check.WithCustomMessage("one non-chip note: no badge")
                .That(DrawnBadge(manager.WindowForTest(a)!)).IsNull();

            var b = SavedNote(manager, "b", new PhysicalPoint(1200, 100));
            Check.WithCustomMessage("two non-chip notes: both badges show")
                .That(new[] { DrawnBadge(manager.WindowForTest(a)!), DrawnBadge(manager.WindowForTest(b)!) })
                .ContainsExactly("1", "2");

            // ✕ B into the chip: the chip's own badge is GONE, and the count dropped to one
            // non-chip — A's badge is gone too.
            ArmThenHold(manager, CellCentre(manager, b, 3), startUs: 0);
            Check.That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("a chip never shows a badge")
                .That(DrawnBadge(manager.WindowForTest(b)!)).IsNull();
            Check.WithCustomMessage("one non-chip note left: its badge hides too")
                .That(DrawnBadge(manager.WindowForTest(a)!)).IsNull();

            // Undo: both badges come back with the same numerals.
            ArmThenHold(manager, CellCentre(manager, b, 0), startUs: 2_000_000);
            Check.That(new[] { DrawnBadge(manager.WindowForTest(a)!), DrawnBadge(manager.WindowForTest(b)!) })
                .ContainsExactly("1", "2");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [InlineData("Right", 1200.0, 1000.0)]
    [InlineData("Left", 3000.0, 1200.0)]
    [InlineData("Below", 1920.0, 200.0)]
    [InlineData("Above", 1920.0, 2000.0)]
    public void TheBadge_OverlapNothing_OnAnySide(string label, double px, double py)
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "note one", new PhysicalPoint(px, py));
            SavedNote(manager, "note two", new PhysicalPoint(1200, 100));
            var window = manager.WindowForTest(a)!;
            var placement = manager.PlacementForTest(a)!;

            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            var disc = window.BadgeDiscForTest!;
            Check.That(disc).IsNotNull();
            var badge = new Rect(Canvas.GetLeft(disc), Canvas.GetTop(disc), disc.Width, disc.Height);

            // The drawn text: the editor is a TextBlock in the content border's stack.
            var stack = (StackPanel)canvas.Children.OfType<Border>().First(b => b.Child is StackPanel).Child;
            var text = (TextBlock)stack.Children[0];
            var textRect = text.TransformToVisual(canvas).TransformBounds(
                new Rect(new Point(0, 0), new Point(text.ActualWidth, text.ActualHeight)));

            Check.WithCustomMessage($"{label}: the badge must not overlap the drawn text")
                .That(badge.IntersectsWith(textRect)).IsFalse();

            // The occupied hit cells only (the gap row is the badge's home: SlotsFor never
            // returns 2, so its fill stays Collapsed in every state).
            foreach (var slot in NoteLayout.SlotsFor(NoteState.Display))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement.Side, slot, placement.Card.Width);
                var cell = new Rect(cx - (LayoutConstants.NoteCellDip / 2.0), cy - (LayoutConstants.NoteCellDip / 2.0),
                    LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
                Check.WithCustomMessage($"{label}: the badge must not overlap hit cell {slot}")
                    .That(badge.IntersectsWith(cell)).IsFalse();
            }

            // The 30 DIP gap strip between the text column and the control column, full card
            // height — window-relative through the same card origin the cells derive from.
            var (originX, originY) = NoteLayout.CardOrigin(placement.Side);
            bool columnOnLeft = placement.Side == NoteSide.Left;
            double gapX = columnOnLeft
                ? originX + LayoutConstants.NoteColumnWidthDip
                : originX + NoteLayout.TextColumnWidthDip(NoteSettings.DefaultTextSizeDip);
            var gap = new Rect(gapX, originY, NoteLayout.TextColumnGapDip, placement.Card.Height);
            Check.WithCustomMessage($"{label}: the badge must not overlap the 30 DIP gap")
                .That(badge.IntersectsWith(gap)).IsFalse();

            // And the badge sits INSIDE the card.
            Check.WithCustomMessage($"{label}: the badge must sit on the card")
                .That(new Rect(placement.Card.X - placement.Window.X, placement.Card.Y - placement.Window.Y,
                    placement.Card.Width, placement.Card.Height).Contains(badge)).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheBadge_IsNeverADwellTargetOrChrome()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            SavedNote(manager, "b", new PhysicalPoint(1200, 100));
            Check.That(DrawnBadge(manager.WindowForTest(a)!)).IsEqualTo("1");

            var placement = manager.PlacementForTest(a)!;
            var (bx, by) = NoteLayout.BadgeCenter(placement.Side, placement.Card.Width);
            var badgeCentre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(placement.Window.Left + bx),
                DpiHelper.LogicalToPhysicalY(placement.Window.Top + by));

            Check.WithCustomMessage("the badge must never enter LiveTargetRects — it is not a dwell target")
                .That(manager.LiveTargetRects().Any(r =>
                    badgeCentre.X >= r.X && badgeCentre.X <= r.X + r.Width
                    && badgeCentre.Y >= r.Y && badgeCentre.Y <= r.Y + r.Height)).IsFalse();

            // The card rect IS chrome (T5's placement avoid uses it) and legitimately contains
            // the badge — the badge sits on the card. What must hold: no chrome rect OTHER than
            // the containing card covers the badge centre — the gap row is chrome for nobody
            // (P4 D3), so the badge adds no chrome of its own and never sits in a cell zone.
            var cardRect = new PhysicalRect(
                DpiHelper.LogicalToPhysicalX(placement.Card.X), DpiHelper.LogicalToPhysicalY(placement.Card.Y),
                DpiHelper.LogicalToPhysicalX(placement.Card.Width), DpiHelper.LogicalToPhysicalY(placement.Card.Height));
            Check.WithCustomMessage("the badge must never enter ChromeRects — the gap row is chrome "
                    + "for nobody; only the containing card rect may cover it")
                .That(manager.ChromeRects().Any(r =>
                    (!SameRect(r, cardRect))
                    && badgeCentre.X >= r.X && badgeCentre.X <= r.X + r.Width
                    && badgeCentre.Y >= r.Y && badgeCentre.Y <= r.Y + r.Height)).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
