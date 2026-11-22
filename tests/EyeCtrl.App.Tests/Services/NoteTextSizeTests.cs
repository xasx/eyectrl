namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System.Windows;
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
/// T8 (P3 D3, P4 D1): the note text-size setting is LIVE - a change re-renders every window at
/// the new size, re-derives every card (the placements resize), and rebuilds the registration.
/// A pending chip derives from the size-independent chip card, so it does not move.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteTextSizeTests
{
    private sealed class CountingProbe
    {
        public int TargetsChanged;
        public CountingProbe(WeakReferenceMessenger messenger)
        {
            messenger.Register<CountingProbe, NoteTargetsChangedMessage>(this, (_, _) => TargetsChanged++);
        }
    }

    private static void Hold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        // D: a Display note's row cell is live only after a look reveals the row.
        var resting = manager.ActiveNotes.FirstOrDefault(n => n.State == NoteState.Display
            && !manager.IsRevealedForTest(n.Id)
            && (NoteTestBench.CellCentre(manager, n.Id, 0) == centre
                || NoteTestBench.CellCentre(manager, n.Id, 1) == centre
                || NoteTestBench.CellCentre(manager, n.Id, 3) == centre));
        if (resting is not null)
            startUs = NoteTestBench.Reveal(manager, resting.Id, startUs);

        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(centre.X + 10_000, centre.Y), t, IsInGap: false)));
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(centre, t, IsInGap: false)));
    }

    [StaFact]
    public void ATextSizeChange_ReRendersEveryWindow_AndRePlacesEveryCard()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);
        var probe = new CountingProbe(messenger);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);

            var before = manager.PlacementForTest(id)!;
            var window = manager.WindowForTest(id)!;
            // D2: the Display placement uses the RESERVED card: max(measured, four cells) + the
            // paddings. "hello" is short, so the four-cell floor (4 x 112 + 2 x 10 = 468) sets it.
            Check.That(before.Card.Width).IsEqualTo(NoteLayout.DisplayReservedWidthDip(
                window.MeasureTextWidthDip("hello", NoteSettings.DefaultTextSizeDip)));
            Check.That(window.TextSizeForTest).IsEqualTo(NoteSettings.DefaultTextSizeDip);

            settings.TextSizeDip = 40;

            var after = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the card re-derives at the new size")
                .That(after.Card.Width).IsEqualTo(NoteLayout.DisplayReservedWidthDip(
                    window.MeasureTextWidthDip("hello", 40)));
            Check.WithCustomMessage("the text line grows with the size, so the card is taller")
                .That(after.Card.Height).IsStrictlyGreaterThan(before.Card.Height);
            Check.WithCustomMessage("the drawn text size follows")
                .That(window.TextSizeForTest).IsEqualTo(40);
            Check.WithCustomMessage("T8 re-registers (the cells moved with the card)")
                .That(probe.TargetsChanged).IsStrictlyGreaterThan(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AChipShrunkInPlace_DoesNotMove_WhenTheTextSizeChanges()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new NoteSettings();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider, noteSettings: settings);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.CreateNoteAt(anchor);
            var id = manager.ActiveNotes[0].Id;
            manager.Apply(new NoteAppend("hello"));
            manager.FinishEdit(id, commit: true);

            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            var chipBefore = manager.PlacementForTest(id)!;
            // D6: the chip's ↺ is read AFTER it exists (it anchors at the reserved text corner,
            // not at the Display row's ✎).
            var undoCellBefore = NoteTestBench.CellCentre(manager, id, 0);

            settings.TextSizeDip = 48;

            var chipAfter = manager.PlacementForTest(id)!;
            Check.WithCustomMessage("the chip card keeps its captured size - the chip survives T8 in place")
                .That((chipAfter.Card.Width, chipAfter.Card.Height))
                .IsEqualTo((chipBefore.Card.Width, chipBefore.Card.Height));
            Check.WithCustomMessage("the Undo cell keeps its centre through a T8 raise")
                .That(NoteTestBench.CellCentre(manager, id, 0)).IsEqualTo(undoCellBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
