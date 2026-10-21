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
/// </summary>
[Collection("WpfApplication")]
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
        StringTable.Activate(GermanDraft.Table);
        var german = ChipTexts();

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
        var mid = new NotePlaceHereWindow(28);
        var large = new NotePlaceHereWindow(48);

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
}
