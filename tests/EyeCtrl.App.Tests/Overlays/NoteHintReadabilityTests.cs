namespace EyeCtrl.App.Tests.Overlays;

using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-06 G1 — the hints the owner could not read on their real screen: the Undo chip's
/// text and the Move state's "Place here" label were 12 DIP, the size P4 removed from the note
/// body. Both now read at the NOTE's own text size, the chip's text column is MEASURED so every
/// chip string - in every shipped language - fits in at most two lines, and the "Place here"
/// window grows to fit its measured label.
/// <para>
/// The chip is size-DEPENDENT now, so its geometry is CAPTURED when the chip is created: a
/// text-size change while a chip is pending must not resize the card under a resting gaze
/// (R12) nor re-wrap a line the owner has already read.
/// </para>
/// <para>
/// T6 fixup-07: the class lives in the exclusive "StringTable Tests (App)" collection, because
/// it activates the German table - the table is process-global, and the German is reset in a
/// finally so it cannot leak into the rest of the run.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class NoteHintReadabilityTests
{
    /// <summary>The note text sizes the setting allows (18-48), and the three a test reads.</summary>
    public static TheoryData<double> TextSizes => new() { 18, 28, 48 };

    // ── the chip text ──

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void TheChipText_IsDrawnAtTheNoteTextSize(double textSize)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var texts = ChipTexts();
        var column = NoteLayout.ChipTextWidthDip(textSize, texts);

        var window = new NoteWindow("g1-chip-font");
        InvisibleTestWindow.Prepare(window);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(texts[0], textSize, column);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var block = window.ChipTextForTest;
        Check.WithCustomMessage("the chip must draw its text").That(block).IsNotNull();
        Check.WithCustomMessage("the chip text must read at the note's own size, not at 12 DIP")
            .That(block!.FontSize).IsEqualTo(textSize);
        Check.WithCustomMessage("the chip text must be drawn in the measured column")
            .That(block.Width).IsEqualTo(column);
    }

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void EveryChipString_FitsInTwoLines_InEnglishAndGerman(double textSize)
    {
        var english = ChipTexts();
        IReadOnlyList<string> german;
        StringTable.Activate(GermanDraft.Table);
        try
        {
            german = ChipTexts();
        }
        finally
        {
            // The string table is process-global: the German must not outlive this test.
            StringTable.ResetToEmbeddedEnglish();
        }

        Check.WithCustomMessage("the German table must really carry the chip's German")
            .That(german).IsNotEqualTo(english);

        foreach (var texts in new[] { english, german })
        {
            var column = NoteLayout.ChipTextWidthDip(textSize, texts);
            foreach (var text in texts)
            {
                var formatted = Format(text, textSize, column);
                Check.WithCustomMessage($"'{text}' must fit inside the {column} DIP column at {textSize} DIP")
                    .That(formatted.Width).IsLessOrEqualThan(column);
                Check.WithCustomMessage($"'{text}' must wrap into at most two lines at {textSize} DIP")
                    .That(LineCount(text, textSize, column)).IsLessOrEqualThan(2);
            }
        }
    }

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void TheChipCard_HoldsTheMeasuredColumn_PlusTheUndoColumn(double textSize)
    {
        var texts = ChipTexts();
        var column = NoteLayout.ChipTextWidthDip(textSize, texts);
        var card = NoteLayout.ChipCardSize(textSize, texts);

        Check.WithCustomMessage("the chip card is the text column plus the Undo column")
            .That(card.Width).IsEqualTo(column + LayoutConstants.NoteColumnWidthDip);
        Check.WithCustomMessage("the chip card is at least one hit-cell row tall")
            .That(card.Height).IsGreaterOrEqualThan(NoteLayout.ChipCellRowHeightDip);
        Check.WithCustomMessage("the chip card must hold the drawn text and its margin")
            .That(card.Height)
            .IsGreaterOrEqualThan(NoteLayout.ChipTextHeightDip(textSize, texts) + NoteLayout.ChipTextMarginDip);
    }

    [StaFact]
    public void TheChipCard_GrowsWithTheTextSize()
    {
        var texts = ChipTexts();
        var small = NoteLayout.ChipCardSize(18, texts);
        var mid = NoteLayout.ChipCardSize(28, texts);
        var large = NoteLayout.ChipCardSize(48, texts);

        Check.WithCustomMessage("the chip must grow with the note's text size")
            .That(mid.Width).IsStrictlyGreaterThan(small.Width);
        Check.That(large.Width).IsStrictlyGreaterThan(mid.Width);
    }

    [StaFact]
    public void ATextSizeChangeAndALaterRePlace_LeaveTheChipAtItsCapturedSize()
    {
        // T6 fixup-07 (G1d): the chip's size is CAPTURED when the chip is created, so neither a
        // text-size change nor a later re-place (the T6 work-area change) may resize it. Without
        // the capture the re-place would derive the chip's card at the NEW text size - a bigger
        // card under a resting gaze.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = ChipNote(manager);
            var before = manager.PlacementForTest(id)!;
            var undoBefore = NoteTestBench.CellCentre(manager, id, 0);

            settings.TextSizeDip = 40;
            manager.OnWorkAreaChangedForTest(); // the T6 re-place

            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the chip card must keep its captured size")
                .That((after.Card.Width, after.Card.Height))
                .IsEqualTo((before.Card.Width, before.Card.Height));
            Check.WithCustomMessage("the Undo cell must keep its centre")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void TheChipCard_HoldsItsDrawnText(double textSize)
    {
        // T6 fixup-07 (G1f): two lines at 48 DIP need about 130 DIP against a one-row card, so
        // the card must GROW to hold the text it draws - in every language.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var texts = ChipTexts();
        var column = NoteLayout.ChipTextWidthDip(textSize, texts);
        var card = NoteLayout.ChipCardSize(textSize, texts);

        var window = new NoteWindow("g1f-chip");
        InvisibleTestWindow.Prepare(window);
        window.SetTextSize(textSize);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(texts[0], textSize, column);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var block = window.ChipTextForTest;
        Check.That(block).IsNotNull();
        block!.Measure(new Size(column, double.PositiveInfinity));
        Check.WithCustomMessage("the chip text must be measured")
            .That(block.DesiredSize.Height).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage("the chip card must hold the drawn text and its margin")
            .That(block.DesiredSize.Height).IsLessOrEqualThan(card.Height);
        Check.WithCustomMessage("the chip card must hold the drawn text's width")
            .That(block.DesiredSize.Width).IsLessOrEqualThan(card.Width);
    }

    [StaFact]
    public void TheDiscardedGermanChip_KeepsItsDotOnTheFirstLine()
    {
        // T6 fixup-07: the no-break space before the middot, applied in code to both the drawn
        // text and the texts the column is measured from - the strings themselves are untouched.
        // With the no-break space the dot can never start a wrapped line (it would read like a
        // bullet), so the discarded line reads "Änderungen verworfen ·" / "Rückgängig".
        Check.WithCustomMessage("the helper must bind the dot to the word before it")
            .That(NoteLayout.NoBreakBeforeDot("Änderungen verworfen · Rückgängig"))
            .Contains("\u00A0·");

        // And the DRAWN chip text carries it - the manager applies the helper to the text it
        // hands the window.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        StringTable.Activate(GermanDraft.Table);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = ChipNote(manager);
            var drawn = manager.WindowForTest(id)!.ChipTextForTest!.Text;
            Check.WithCustomMessage("the drawn chip text must carry the no-break space")
                .That(drawn).Contains("\u00A0·");
            Check.WithCustomMessage("the dot must not start a line")
                .That(drawn).DoesNotContain(" ·");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void ATextSizeChange_WhileAChipIsPending_LeavesTheChipExactlyWhereItWas()
    {
        // The captured size (T6 fixup-06 G1): a pending Undo is a transient affordance the owner
        // may already be reaching for. Re-placing it on a text-size change would move the Undo
        // cell out from under a resting gaze (R12) and re-wrap a line already read.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = ChipNote(manager);
            var before = manager.PlacementForTest(id)!;
            var undoBefore = NoteTestBench.CellCentre(manager, id, 0);
            var window = manager.WindowForTest(id)!;
            var fontBefore = window.ChipTextForTest!.FontSize;

            settings.TextSizeDip = 40;

            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the chip must not move on a text-size change")
                .That(after.Window).IsEqualTo(before.Window);
            Check.WithCustomMessage("the Undo cell must not move on a text-size change")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoBefore);
            Check.WithCustomMessage("the drawn chip text keeps the size it was composed at")
                .That(window.ChipTextForTest!.FontSize).IsEqualTo(fontBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the Place-here label ──

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void ThePlaceHereLabel_ReadsAtTheNoteTextSize_AndFitsItsWindow(double textSize)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NotePlaceHereWindow(textSize);
        InvisibleTestWindow.Prepare(window);
        var label = ((Border)window.Content).Child as TextBlock;

        Check.WithCustomMessage("the Place-here window must carry a label").That(label).IsNotNull();
        Check.WithCustomMessage("the Place-here label must read at the note's own size, not at 12 DIP")
            .That(label!.FontSize).IsEqualTo(textSize);

        var formatted = Format(label.Text, textSize, window.SizeDip.Width);
        Check.WithCustomMessage("the label must fit inside the window's width")
            .That(formatted.Width).IsLessOrEqualThan(window.SizeDip.Width);
        Check.WithCustomMessage("the window must never be smaller than the old 150x44 box")
            .That(window.SizeDip.Width).IsGreaterOrEqualThan(LayoutConstants.NotePlaceHereWidthDip);
        Check.That(window.SizeDip.Height).IsGreaterOrEqualThan(LayoutConstants.NotePlaceHereHeightDip);
        Check.WithCustomMessage("the window's bounds must BE its measured size")
            .That((window.Width, window.Height)).IsEqualTo((window.SizeDip.Width, window.SizeDip.Height));
    }

    [StaFact]
    public void ThePlaceHereWindow_GrowsWithTheTextSize()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var small = new NotePlaceHereWindow(18);
        InvisibleTestWindow.Prepare(small);
        var mid = new NotePlaceHereWindow(28);
        InvisibleTestWindow.Prepare(mid);
        var large = new NotePlaceHereWindow(48);
        InvisibleTestWindow.Prepare(large);

        // The English label is short enough to sit on the 150 DIP floor at the two smaller
        // sizes, so only the largest must grow past it - the floor is the ticket's "never
        // smaller than today's 150x44".
        Check.WithCustomMessage("the Place-here window must grow with the note's text size")
            .That(large.SizeDip.Width).IsStrictlyGreaterThan(small.SizeDip.Width);
        Check.That(mid.SizeDip.Width).IsGreaterOrEqualThan(small.SizeDip.Width);
        Check.That(large.SizeDip.Height).IsGreaterOrEqualThan(small.SizeDip.Height);
    }

    [StaFact]
    public void ThePlaceHereDwellTarget_SitsOnTheRealWindow()
    {
        // The dwell target, the settle margin and the on-screen placement all read the REAL
        // window size - a target left on the old constant's centre would sit off the label.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);

            Check.That(manager.PlaceShownForTest).IsTrue();
            var rect = manager.PlaceWindowRectForTest;
            var window = manager.PlaceWindowForTest;
            Check.That(window).IsNotNull();
            Check.WithCustomMessage("the placement rect must BE the real window's size")
                .That((rect.Width, rect.Height)).IsEqualTo((window!.Width, window.Height));

            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(rect.X + (rect.Width / 2)),
                DpiHelper.LogicalToPhysicalY(rect.Y + (rect.Height / 2)));
            Check.WithCustomMessage("the place dwell target must sit on the window's centre")
                .That(manager.LiveTargetRects().Any(r => r.Contains(centre))).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ThePlaceHereDwellTarget_SitsOnTheRealWindow_EvenWhenTheLabelOutgrowsTheOldBox()
    {
        // T6 fixup-07 (G1e): the :place target is registered on the REAL window's rect, not on
        // the old 150x44 constant - so it must sit on the window's centre at a size where the
        // label outgrows that constant (the German label at 28 DIP is 200 DIP wide).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);

        StringTable.Activate(GermanDraft.Table);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);

            Check.That(manager.PlaceShownForTest).IsTrue();
            var rect = manager.PlaceWindowRectForTest;
            var window = manager.PlaceWindowForTest;
            Check.That(window).IsNotNull();
            Check.WithCustomMessage("the German label must outgrow the old 150 DIP box")
                .That(rect.Width).IsStrictlyGreaterThan(LayoutConstants.NotePlaceHereWidthDip);
            Check.WithCustomMessage("the placement rect must BE the real window's size")
                .That((rect.Width, rect.Height)).IsEqualTo((window!.Width, window.Height));

            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(rect.X + (rect.Width / 2)),
                DpiHelper.LogicalToPhysicalY(rect.Y + (rect.Height / 2)));
            // The target's own CENTRE, not "a rect that happens to cover the centre": the
            // target is a disc of 28 DIP, and a target built from the old 150x44 constant would
            // sit 25 DIP off the window's centre and still cover it.
            var target = manager.LiveTargetRects()
                .Select(r => new PhysicalPoint(r.X + (r.Width / 2), r.Y + (r.Height / 2)))
                .FirstOrDefault(p => p == centre);
            Check.WithCustomMessage("the place dwell target must sit exactly on the window's centre")
                .That(target).IsEqualTo(centre);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void ATextSizeChange_WhileThePlaceHereIsShown_ResizesItAndMovesTheDwellTarget()
    {
        // T6 fixup-07 (G1g): a T8 change while the window is SHOWN re-composes it and
        // re-registers the dwell target at the new rect.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var before = manager.PlaceWindowRectForTest;

            settings.TextSizeDip = 48;

            var after = manager.PlaceWindowRectForTest;
            Check.WithCustomMessage("the shown Place-here window must grow with the text size")
                .That(after.Width).IsStrictlyGreaterThan(before.Width);
            Check.WithCustomMessage("the window's own size must follow the rect")
                .That(manager.PlaceWindowForTest!.Width).IsEqualTo(after.Width);

            var centre = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(after.X + (after.Width / 2)),
                DpiHelper.LogicalToPhysicalY(after.Y + (after.Height / 2)));
            Check.WithCustomMessage("the dwell target must follow the new rect")
                .That(manager.LiveTargetRects().Any(r => r.Contains(centre))).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ATextSizeChange_WhileThePlaceHereIsHidden_IsPickedUpAtTheNextShow()
    {
        // T6 fixup-07 (F3): the window is created once and only hidden afterwards, so it can
        // carry a stale size from a change made while it was hidden. The next show must use the
        // CURRENT text size.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            long present = 0;
            var id = NewMovingNote(manager, new PhysicalPoint(1600, 1000), ref present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var first = manager.PlaceWindowRectForTest;

            // Cancel the move: the Place-here window is hidden, and stays hidden.
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
            Check.That(manager.PlaceShownForTest).IsFalse();

            settings.TextSizeDip = 48;

            // The next Move shows it again: the size must be the NEW one.
            present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
            present = HoldGaze(manager, new PhysicalPoint(800, 1800), present, 1200);
            Check.That(manager.PlaceShownForTest).IsTrue();
            var second = manager.PlaceWindowRectForTest;

            Check.WithCustomMessage("the next show must use the current text size")
                .That(second.Width).IsStrictlyGreaterThan(first.Width);
            Check.WithCustomMessage("the window's own size must follow")
                .That(manager.PlaceWindowForTest!.Width).IsEqualTo(second.Width);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void TheManagersPushedColumn_HoldsTheDrawnDiscardedChipText(double textSize)
    {
        // R3 (review-07): the column the manager PUSHES comes from the texts it MEASURES, so
        // those must carry the same no-break space the drawn text does. Without it the German
        // "Änderungen verworfen · Rückgängig" needs ~290 DIP against a pushed 285 at 28 and
        // wraps into a third row, out of the card.
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings { TextSizeDip = (int)textSize };
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.German })
            {
                StringTable.Activate(language == UiLanguage.German
                    ? GermanDraft.Table
                    : StringTable.EmbeddedEnglish);

                var id = DiscardedChip(manager);
                var block = manager.WindowForTest(id)!.ChipTextForTest!;
                int lines = LineCount(block.Text, textSize, block.Width);
                Check.WithCustomMessage(
                        $"[{language}] the pushed {block.Width} DIP column must hold '{block.Text}' "
                        + $"in at most two lines at {textSize} DIP (got {lines})")
                    .That(lines).IsLessOrEqualThan(2);
                manager.RemoveNote(id);
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // ── helpers ──

    /// <summary>The chip's two texts in the current table's language.</summary>
    private static IReadOnlyList<string> ChipTexts() =>
    [
        StringTable.Current.T("overlays.note.chip.removed"),
        StringTable.Current.T("overlays.note.chip.discarded"),
    ];

    /// <summary>The text laid out at the given width, with the real typeface.</summary>
    private static FormattedText Format(string text, double fontSize, double maxWidth)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), fontSize,
            Tokens.BrushTextPrimary, null, TextFormattingMode.Ideal, 1.0)
        { MaxTextWidth = maxWidth };
        return formatted;
    }

    /// <summary>How many lines the text wraps into at the given width and size.</summary>
    private static int LineCount(string text, double fontSize, double maxWidth)
    {
        var oneLine = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), fontSize,
            Tokens.BrushTextPrimary, null, TextFormattingMode.Ideal, 1.0)
        { MaxTextWidth = 100_000.0 };
        if (oneLine.Height <= 0) return 1;
        return (int)Math.Round(Format(text, fontSize, maxWidth).Height / oneLine.Height);
    }

    /// <summary>A note that is already a chip: a Display note whose removal dwell fired.</summary>
    private static string ChipNote(NoteManager manager)
    {
        manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend("saved text"));
        manager.FinishEdit(id, commit: true);
        Hold(manager, NoteTestBench.CellCentre(manager, id, 3), 0);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        return id;
    }

    private static void Hold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(centre.X + 10_000, centre.Y), t, IsInGap: false)));
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(centre, t, IsInGap: false)));
    }

    /// <summary>A Display note, already finished, ready to be picked up for a Move.</summary>
    private static string NewMovingNote(NoteManager manager, PhysicalPoint anchor, ref long present)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend("saved text"));
        manager.FinishEdit(id, commit: true);
        present = HoldGaze(manager, NoteTestBench.CellCentre(manager, id, 1), present);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Move);
        return id;
    }

    /// <summary>Holds the gaze on a point long enough to dwell or to settle: looks away past
    /// every control for 200 ms, then holds past the 600 ms dwell - the cadence the note
    /// manager's own dwells expect.</summary>
    private static long HoldGaze(NoteManager manager, PhysicalPoint point, long present, int ms = 1200)
    {
        var away = new PhysicalPoint(point.X + 10_000, point.Y);
        for (var t = present; t <= present + 220_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (var t = present + 240_000; t <= present + ms * 1000; t += 33_000)
            manager.OnGazeMoved(point, t);
        return present + ms * 1000 + 33_000;
    }
    /// <summary>A DISCARDED chip: the longest of the two chip lines ("Anderungen verworfen" /
    /// "Changes discarded"), which is the one the measured column must hold.</summary>
    private static string DiscardedChip(NoteManager manager)
    {
        manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend("draft text"));
        manager.FinishEdit(id, commit: false);
        Check.That(manager.ActiveNotes.First(n => n.Id == id).State).IsEqualTo(NoteState.PendingUndo);
        return id;
    }
}
