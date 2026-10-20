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
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-01: the number TAG. The numbering rules — the LOWEST free positive integer at
/// creation, kept through Edit/Display/Move/PendingUndo (Undo restores the same numeral),
/// released only when the note is finally gone (chip expiry, a chip yielded at capacity, an
/// empty new note discarded or Done); a chip still HOLDS its number. The visibility rule —
/// badges show on card notes only while TWO OR MORE notes are not chips; chips never show one
/// and never count. And the tag is pure chrome drawn on the card's TOP edge at the TEXT side:
/// never a dwell target, never chrome for anybody else, never a control, and the card (and
/// every drawn cell) stays where the tag-less derivation put it.
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

    /// <summary>The drawn tag's numeral, read off the real canvas element.</summary>
    private static string? DrawnBadge(NoteWindow window)
    {
        var tag = window.BadgeTagForTest;
        var label = window.BadgeLabelForTest;
        if (tag is null || label is null) return null;
        Check.That(tag.Visibility).IsEqualTo(Visibility.Visible);
        return label.Text;
    }

    /// <summary>A hand-built note window under a deterministic placement: a Display card of the
    /// given size derived by the pure <see cref="NoteLayout.Place"/> and grown for the tag the
    /// same way the manager grows every placement, then arranged so the Canvas coordinates read
    /// back (the same construction NoteReadingRenderTests renders with).</summary>
    private static (NoteWindow Window, NotePlacement Placement, Canvas Canvas) ManualNoteWindow(
        string id, string text, double textSize, PhysicalPoint anchor, NoteSide side)
    {
        var window = new NoteWindow(id);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        window.SetState(NoteState.Display);
        window.SetTextSize(textSize);
        var card = new Size(
            NoteLayout.CardWidthDip(textSize),
            NoteLayout.CardHeightDip(textSize, window.MeasureTextHeightDip(text), NoteTestBench.WorkArea.Height));
        var placement = NoteLayout.WithTagClearance(
            NoteLayout.Place(anchor, side, card, NoteTestBench.WorkArea, Array.Empty<Rect>(), DpiHelper.ScaleX)!);
        window.ApplyPlacement(placement);
        window.SetText(text);
        window.UpdateContent();
        window.SetBadge(1);

        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
        return (window, placement, canvas);
    }

    /// <summary>The tag rect read off the arranged canvas.</summary>
    private static Rect DrawnTagRect(NoteWindow window)
    {
        var tag = window.BadgeTagForTest!;
        return new Rect(Canvas.GetLeft(tag), Canvas.GetTop(tag), tag.Width, tag.Height);
    }

    /// <summary>Drawn-tag vs pure-TagRect equality field by field.</summary>
    private static void SameTagRect(string because, Rect drawn, Rect expected)
    {
        Check.WithCustomMessage($"{because}: left").That(drawn.Left).IsCloseTo(expected.Left, 1e-6);
        Check.WithCustomMessage($"{because}: top").That(drawn.Top).IsCloseTo(expected.Top, 1e-6);
        Check.WithCustomMessage($"{because}: width").That(drawn.Width).IsCloseTo(expected.Width, 1e-6);
        Check.WithCustomMessage($"{because}: height").That(drawn.Height).IsCloseTo(expected.Height, 1e-6);
    }

    /// <summary>Physical-rect equality within a rounding DIP (DpiHelper rounding at 2.0).</summary>
    private static bool SameRect(PhysicalRect a, PhysicalRect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    /// <summary>The tag rect as a physical screen rect (the manager's inventories speak
    /// physical).</summary>
    private static PhysicalRect TagScreenRect(NotePlacement placement, Rect tagLocal)
    {
        var window = placement.Window;
        return new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(window.Left + tagLocal.Left),
            DpiHelper.LogicalToPhysicalY(window.Top + tagLocal.Top),
            DpiHelper.LogicalToPhysicalX(tagLocal.Width),
            DpiHelper.LogicalToPhysicalY(tagLocal.Height));
    }

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

    /// <summary>The drawn tag IS the pure TagRect of the placement it rides: same rect, numeral,
    /// straddle, text side. It must never overlap the drawn text, an occupied hit cell, the 30
    /// DIP gap strip or the tail polygon; a tag in the column or on the tail goes red here.</summary>
    [StaTheory]
    [InlineData("Right", 1200.0, 1000.0, 28.0)]
    [InlineData("Left", 3000.0, 1200.0, 28.0)]
    [InlineData("Below", 1920.0, 200.0, 28.0)]
    [InlineData("Above", 1920.0, 2000.0, 28.0)]
    [InlineData("Right", 1200.0, 1000.0, 48.0)]
    [InlineData("Left", 3000.0, 1200.0, 48.0)]
    [InlineData("Below", 1920.0, 200.0, 48.0)]
    [InlineData("Above", 1920.0, 2000.0, 48.0)]
    public void TheTag_StraddlesTheTopEdgeAtTheTextSide_AndOverlapNothing(string label, double px, double py, double textSize)
    {
        try
        {
            var (window, placement, canvas) = ManualNoteWindow(
                $"tag-{label}-{textSize}", $"Note one at {label}.", textSize, new PhysicalPoint(px, py), TagSide(label));
            Check.WithCustomMessage($"{label}/{textSize}: the placement must show the tail")
                .That(placement.TailVisible).IsTrue();

            var tag = DrawnTagRect(window);
            var expected = NoteLayout.TagRect(placement);
            SameTagRect($"{label}/{textSize}: the drawn tag must be the pure TagRect", tag, expected);

            var card = NoteLayout.RectLocal(placement);
            Check.WithCustomMessage($"{label}/{textSize}: the tag must straddle the card's top edge")
                .That(tag.Top).IsCloseTo(card.Top - NoteLayout.TagProtrusionDip, 1e-6);
            Check.WithCustomMessage($"{label}/{textSize}: the tag's height must straddle the edge")
                .That(tag.Height).IsCloseTo(NoteLayout.TagHeightDip, 1e-6);
            // AT the text-side end — inset from the corner the text side hugs (Left's text side
            // is the card's right corner; every other side the left corner). A tag parked
            // anywhere else along the top edge — e.g. over the text column's empty top band,
            // where no drawn thing lives — must fail here even though it hits no rect.
            double inset = NoteLayout.TagCornerInsetDip;
            bool textOnRight = placement.Side == NoteSide.Left;
            Check.WithCustomMessage($"{label}/{textSize}: the tag must sit {inset} DIP from the "
                    + "text-side corner")
                .That(textOnRight ? card.Right - tag.Right : tag.Left - card.Left).IsCloseTo(inset, 1e-6);

            // The drawn text: the editor is the TextBlock in the content border's stack.
            var stack = (StackPanel)canvas.Children.OfType<Border>().First(b => b.Child is StackPanel).Child;
            var text = (TextBlock)stack.Children[0];
            var textRect = text.TransformToVisual(canvas).TransformBounds(
                new Rect(new Point(0, 0), new Point(text.ActualWidth, text.ActualHeight)));
            Check.WithCustomMessage($"{label}/{textSize}: the tag must not overlap the drawn text")
                .That(tag.IntersectsWith(textRect)).IsFalse();

            // The occupied hit cells only, at the placement's own grown coordinates.
            foreach (var slot in NoteLayout.SlotsFor(NoteState.Display))
            {
                var (cx, cy) = NoteLayout.ButtonCenter(placement, slot);
                var cell = new Rect(cx - (LayoutConstants.NoteCellDip / 2.0), cy - (LayoutConstants.NoteCellDip / 2.0),
                    LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
                Check.WithCustomMessage($"{label}/{textSize}: the tag must not overlap hit cell {slot}")
                    .That(tag.IntersectsWith(cell)).IsFalse();
            }

            // The 30 DIP gap strip between the text column and the control column, full card
            // height — window-relative through the placement's own rect.
            var gap = placement.Side == NoteSide.Left
                ? new Rect(card.Right - LayoutConstants.NoteColumnWidthDip - NoteLayout.TextColumnGapDip, card.Top,
                    NoteLayout.TextColumnGapDip, card.Height)
                : new Rect(card.Left + NoteLayout.TextColumnWidthDip(textSize), card.Top,
                    NoteLayout.TextColumnGapDip, card.Height);
            Check.WithCustomMessage($"{label}/{textSize}: the tag must not overlap the 30 DIP gap")
                .That(tag.IntersectsWith(gap)).IsFalse();

            // And never the tail polygon — the tail is the look-at-me affordance; a
            // control-looking thing on it would promise an action on the anchor.
            Check.WithCustomMessage($"{label}/{textSize}: the tag must not intersect the tail")
                .That(NoteBadgeTestSupport.TriIntersectsRect(
                    NoteLayout.TailTriangle(placement)!, tag)).IsFalse();

            // The tag's numeral is the note's number.
            Check.That(window.BadgeLabelForTest!.Text).IsEqualTo("1");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static NoteSide TagSide(string label) => label switch
    {
        "Right" => NoteSide.Right,
        "Left" => NoteSide.Left,
        "Below" => NoteSide.Below,
        _ => NoteSide.Above,
    };

    /// <summary>The tail at the tag's corner — a Below tail whose base reaches past the tag's
    /// home spot — must push <see cref="NoteLayout.TagRect"/> to SLIDE along the top edge
    /// toward the column (never over it) until it clears the base's right end by the tail
    /// clearance; with the base beyond the tag's reach the tag stays at the corner inset.
    /// Mutant: a TagRect that ignores the tail, slides into the column, or slides the wrong
    /// edge, goes red. Pure geometry: the placement is built by the same record the window
    /// consumes; the DRAWN tag is the theory above's job.</summary>
    [StaTheory]
    [InlineData(30.0)]  // base [12, 48]: reaches the tag's home [12, 40] — must slide
    [InlineData(60.0)]  // base [42, 78]: reaches within the clearance band — must slide
    [InlineData(100.0)] // base [82, 118]: beyond the tag's reach + clearance — must NOT slide
    [InlineData(240.0)] // base [222, 258]: farther still — must NOT slide
    public void TheTag_SlidesOutOfTheTail_NeverIntoTheColumn(double tipCardRelX)
    {
        const double width = 270.0, height = 340.0;
        var window = new Rect(64.0, 766.0, width, height + NoteLayout.TagProtrusionDip);
        var card = new Rect(window.Left, window.Top + NoteLayout.CardOrigin(NoteSide.Below).Y + NoteLayout.TagProtrusionDip,
            width, height);
        var placement = new NotePlacement(
            NoteSide.Below, window, card,
            new Point(window.Left + tipCardRelX, window.Top + NoteLayout.Constants.TipGapDip));
        Check.That(NoteLayout.ExtraTopClearance(placement)).IsEqualTo(NoteLayout.TagProtrusionDip);

        var tagRect = NoteLayout.TagRect(placement);
        var tri = NoteLayout.TailTriangle(placement)!;
        var cardLocal = NoteLayout.RectLocal(placement);
        Check.WithCustomMessage($"tip {tipCardRelX}: the triangle's base must sit on the card's top edge")
            .That(tri[1].Y).IsCloseTo(cardLocal.Top, 1e-6);

        Check.WithCustomMessage($"tip {tipCardRelX}: the tag must not intersect the tail")
            .That(NoteBadgeTestSupport.TriIntersectsRect(tri, tagRect)).IsFalse();

        // The slide decision: the base juts into the tag's band exactly when the base's left
        // end passes the tag home's right edge minus the clearance.
        double home = cardLocal.Left + NoteLayout.TagCornerInsetDip;
        double homeRight = home + NoteLayout.TagWidthDip;
        double baseLeft = Math.Min(tri[1].X, tri[2].X);
        double baseRight = Math.Max(tri[1].X, tri[2].X);
        bool expectedSlid = baseLeft - NoteLayout.TagTailClearanceDip < homeRight;
        bool actuallySlid = tagRect.Left != home;
        Check.WithCustomMessage($"tip {tipCardRelX}: the slide decision must follow the tail")
            .That(actuallySlid).IsEqualTo(expectedSlid);

        if (expectedSlid)
            Check.WithCustomMessage($"tip {tipCardRelX}: the tag's left must clear the base's right end")
                .That(tagRect.Left).IsCloseTo(baseRight + NoteLayout.TagTailClearanceDip, 1e-6);
        else
            Check.WithCustomMessage($"tip {tipCardRelX}: an unthreatened tag stays at the corner inset")
                .That(tagRect.Left).IsCloseTo(home, 1e-6);

        // Never over the column, never off the card, never off the top edge.
        double limit = cardLocal.Right - LayoutConstants.NoteColumnWidthDip - NoteLayout.TagWidthDip;
        Check.WithCustomMessage($"tip {tipCardRelX}: the tag must stop short of the column strip")
            .That(tagRect.Right).IsStrictlyLessThan(limit);
        Check.WithCustomMessage($"tip {tipCardRelX}: the tag must stay within the card's x-range")
            .That(tagRect.Left >= cardLocal.Left && tagRect.Right <= cardLocal.Right).IsTrue();
        Check.WithCustomMessage($"tip {tipCardRelX}: the tag must straddle the top edge")
            .That(tagRect.Top).IsCloseTo(cardLocal.Top - NoteLayout.TagProtrusionDip, 1e-6);
    }

    /// <summary>The tag shows and hides WITHOUT moving anything: the drawn card rect, every
    /// drawn cell's rect and the window's own rect are identical with the badge shown, hidden
    /// and shown again (T6 fixup-01). Mutant: a growth that shifts the card, or a redraw that
    /// re-derives the cells from a different origin, goes red.</summary>
    [StaFact]
    public void TheCardAndCellRects_AreIdentical_WithTheBadgeShownAndHidden()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            SavedNote(manager, "b", new PhysicalPoint(1200, 100));
            var window = manager.WindowForTest(a)!;
            Check.WithCustomMessage("the card note's badge must show to start")
                .That(DrawnBadge(window)).IsEqualTo("1");

            (Rect CardDrawn, Rect[] CellRects, Rect WinRect) Snapshot(NoteWindow w)
            {
                var canvas = (Canvas)w.Content;
                var card = canvas.Children.OfType<Border>()
                    .Where(b => !ReferenceEquals(b, w.BadgeTagForTest))
                    .OrderByDescending(b => b.Width).First();
                var cardDrawn = new Rect(Canvas.GetLeft(card), Canvas.GetTop(card), card.Width, card.Height);
                var cells = canvas.Children.OfType<Rectangle>()
                    .Select(r => new Rect(Canvas.GetLeft(r), Canvas.GetTop(r), r.Width, r.Height))
                    .ToArray();
                return (cardDrawn, cells, new Rect(w.Left, w.Top, w.Width, w.Height));
            }

            var shown = Snapshot(window);
            window.SetBadge(null);
            Check.That(DrawnBadge(window)).IsNull();
            var hidden = Snapshot(window);
            window.SetBadge(1);
            Check.That(DrawnBadge(window)).IsEqualTo("1");
            var again = Snapshot(window);

            Check.WithCustomMessage("the drawn card rect must not change when the tag hides")
                .That(hidden.CardDrawn).IsEqualTo(shown.CardDrawn);
            Check.WithCustomMessage("the window rect must not change when the tag hides")
                .That(hidden.WinRect).IsEqualTo(shown.WinRect);
            Check.WithCustomMessage("the drawn cell rects must not change when the tag hides")
                .That(hidden.CellRects).IsEqualTo(shown.CellRects);
            Check.That(again.CardDrawn).IsEqualTo(shown.CardDrawn);
            Check.That(again.WinRect).IsEqualTo(shown.WinRect);
            Check.That(again.CellRects).IsEqualTo(shown.CellRects);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>The tag is chrome for nobody: it is NEVER in <see
    /// cref="NoteManager.LiveTargetRects"/> or the dwell gate (not a target), but the
    /// protruding half sits over the PAGE — and possibly over ANOTHER note's zone, the blind
    /// spot the rect exists for — so the own tag rect DOES enter <see
    /// cref="NoteManager.ChromeRects"/>, byte-for-byte and following the badge-visibility
    /// rule. The inner half legitimately rides the card (and the first column cell — the
    /// straddle is the point). Mutants: a tag drawn over the column's cells, a tag chrome
    /// entry for a hidden badge, or a tag registered as a dwell target, go red.</summary>
    [StaFact]
    public void TheTag_IsNeverADwellTargetOrChrome()
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
            var tagScreen = TagScreenRect(placement, NoteLayout.TagRect(placement));

            Check.WithCustomMessage("the tag must never enter LiveTargetRects — it is not a dwell target")
                .That(manager.LiveTargetRects().Any(r =>
                    r.X < tagScreen.X + tagScreen.Width && r.X + r.Width > tagScreen.X
                    && r.Y < tagScreen.Y + tagScreen.Height && r.Y + r.Height > tagScreen.Y)).IsFalse();

            // The tag rect rides ABOVE the card. The inner half legitimately rides the card AND
            // the first column cell (the straddle is the point) and the protruding half may
            // ride ANOTHER NOTE's zone when the notes sit close — precisely the blind spot the
            // tag rect fixes: without it, the offers would see only B's card here. So the gate
            // is exactly: the tag rect is reported, byte-equal, and the card straddles it.
            var cardRect = new PhysicalRect(
                DpiHelper.LogicalToPhysicalX(placement.Card.X), DpiHelper.LogicalToPhysicalY(placement.Card.Y),
                DpiHelper.LogicalToPhysicalX(placement.Card.Width), DpiHelper.LogicalToPhysicalY(placement.Card.Height));
            Check.WithCustomMessage("ChromeRects must report the tag's rect exactly — the offers "
                    + "and keep-clears must see the protruding half, not a washed-out version")
                .That(manager.ChromeRects().Any(r => SameRect(r, tagScreen))).IsTrue();
            Check.WithCustomMessage("the tag must straddle the card — the card rect overlaps it")
                .That(cardRect.X < tagScreen.X + tagScreen.Width
                    && cardRect.X + cardRect.Width > tagScreen.X
                    && cardRect.Y < tagScreen.Y + tagScreen.Height
                    && cardRect.Y + cardRect.Height > tagScreen.Y).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>The tag rect rides the VISIBILITY rule, not mere existence: a lone card note
    /// (its badge hidden — one non-chip note) carries NO tag rect in ChromeRects; a chip in a
    /// two-note set (the chip never badged) carries none either — the other note's card, cells
    /// and tag fill the list instead. Mutant: a ChromeRects that reports the tag whenever a
    /// placement exists goes red.</summary>
    [StaTheory]
    [InlineData(true)]  // a lone card note: its badge is hidden — no tag chrome
    [InlineData(false)] // a chip beside a card note: the chip shows no badge — no chip tag chrome
    public void ChromeRects_CarryTheTagRect_OnlyWhileABadgeShows(bool loneProse)
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "a", new PhysicalPoint(1600, 1000));
            var aPlacement = manager.PlacementForTest(a)!;
            var aTag = TagScreenRect(aPlacement, NoteLayout.TagRect(aPlacement));

            if (loneProse)
            {
                // Note A alone: badges hidden.
                Check.WithCustomMessage("one non-chip note: no badge on the real window")
                    .That(DrawnBadge(manager.WindowForTest(a)!)).IsNull();
            }
            else
            {
                var b = SavedNote(manager, "b", new PhysicalPoint(1200, 100));
                Check.WithCustomMessage("two notes: A's badge shows")
                    .That(DrawnBadge(manager.WindowForTest(a)!)).IsEqualTo("1");
                TagNoteAndChipIt(manager, b);
                Check.WithCustomMessage("B is a chip now: its badge is hidden")
                    .That(DrawnBadge(manager.WindowForTest(b)!)).IsNull();
                Check.WithCustomMessage("with B chipped only A is a non-chip — A's badge hides "
                        + "again, so A's tag chrome drops too")
                    .That(DrawnBadge(manager.WindowForTest(a)!)).IsNull();
                var bTag = TagScreenRect(manager.PlacementForTest(b)!, NoteLayout.TagRect(manager.PlacementForTest(b)!));
                Check.WithCustomMessage("no badged note remains: A's tag chrome must drop too")
                    .That(manager.ChromeRects().Any(r => SameRect(r, aTag))).IsFalse();
                Check.WithCustomMessage("the chip must add no tag chrome anywhere")
                    .That(manager.ChromeRects().Any(r => SameRect(r, bTag))).IsFalse();
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static void TagNoteAndChipIt(NoteManager manager, string id)
    {
        ArmThenHold(manager, CellCentre(manager, id, 3), startUs: 0);
        Check.That(manager.ActiveNotes.First(n => n.Id == id).State).IsEqualTo(NoteState.PendingUndo);
    }

    /// <summary>The window grows by EXACTLY the tag's protrusion above the raw derivation's
    /// window, and the CARD stays byte-for-byte where the raw derivation placed it (T6
    /// fixup-01: the tag may grow the window; it may never move the card). Mutant: a growth
    /// that shifts the card instead of the window top, or an over-growth, goes red.</summary>
    [StaFact]
    public void TheWindow_GrowsUpByTheProtrusion_AndTheCardNeverMoves()
    {
        using var manager = MakeManager(out _, out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1200, 1000);
            var a = SavedNote(manager, "growth", anchor);
            var placement = manager.PlacementForTest(a)!;

            // The RAW derivation, re-derived from the SAME inputs the manager used (the stored
            // placement's own card size keeps the measure out of the rebuild).
            var raw = NoteLayout.Place(
                anchor, placement.Side, new Size(placement.Card.Width, placement.Card.Height),
                NoteTestBench.WorkArea, Array.Empty<Rect>(), DpiHelper.ScaleX)!;

            Check.WithCustomMessage("the card rect must be byte-for-byte the raw derivation's")
                .That(placement.Card).IsEqualTo(raw.Card);
            Check.WithCustomMessage("the window must grow by the protrusion above the raw window")
                .That((placement.Window.Left, placement.Window.Top, placement.Window.Width, placement.Window.Height))
                .IsEqualTo((raw.Window.Left, raw.Window.Top - NoteLayout.TagProtrusionDip,
                    raw.Window.Width, raw.Window.Height + NoteLayout.TagProtrusionDip));
            Check.WithCustomMessage("the top clearance must ride exactly the protrusion")
                .That(NoteLayout.ExtraTopClearance(placement)).IsEqualTo(NoteLayout.TagProtrusionDip);
            Check.WithCustomMessage("the growth must be idempotent")
                .That(NoteLayout.WithTagClearance(placement)).IsEqualTo(placement);
            Check.WithCustomMessage("the grown window must stay inside the work area")
                .That(NoteTestBench.WorkArea.Contains(placement.Window)).IsTrue();

            var window = manager.WindowForTest(a)!;
            Check.WithCustomMessage("the drawn window must be the grown rect")
                .That((window.Width, window.Height, window.Left, window.Top))
                .IsEqualTo((placement.Window.Width, placement.Window.Height,
                    placement.Window.Left, placement.Window.Top));
            // The drawn tag straddling the grown card is proven on the arranged manual windows
            // (TheTag_StraddlesTheTopEdge...); a lone note keeps its badge hidden, so nothing
            // is drawn to read here.
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}

/// <summary>Triangle/rect overlap for the tag-vs-tail proofs: the separating-axis test over the
/// triangle's edge normals plus the rect's axes. Touching geometry does not separate an
/// intersection, so a base at exactly the tag's edge would NOT read as disjoint — the
/// production tag keeps a clearance instead.</summary>
internal static class NoteBadgeTestSupport
{
    /// <summary>The separating-axis test between a triangle and an axis-aligned rect.</summary>
    internal static bool TriIntersectsRect(Point[] tri, Rect r)
    {
        Point[] polygon = [tri[0], tri[1], tri[2]];

        static bool SeparatedBy(Point[] polygon, Rect r, double ax, double ay)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var p in polygon)
            {
                var proj = (p.X * ax) + (p.Y * ay);
                min = Math.Min(min, proj);
                max = Math.Max(max, proj);
            }

            double c0 = (r.Left * ax) + (r.Top * ay);
            double c1 = (r.Right * ax) + (r.Top * ay);
            double c2 = (r.Left * ax) + (r.Bottom * ay);
            double c3 = (r.Right * ax) + (r.Bottom * ay);
            double lo = Math.Min(Math.Min(c0, c1), Math.Min(c2, c3));
            double hi = Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
            return max < lo || min > hi;
        }

        for (int i = 0; i < polygon.Length; i++)
        {
            var p0 = polygon[i];
            var p1 = polygon[(i + 1) % polygon.Length];
            if (SeparatedBy(polygon, r, -(p1.Y - p0.Y), p1.X - p0.X)) return false;
        }

        return !(SeparatedBy(polygon, r, 1, 0) || SeparatedBy(polygon, r, 0, 1));
    }
}

