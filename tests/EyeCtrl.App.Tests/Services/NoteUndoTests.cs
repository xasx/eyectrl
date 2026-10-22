namespace EyeCtrl.App.Tests.Services;

using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T3 — PendingUndo, Discard, the chip's Undo and expiry, and the chip clock (P2 D5). Geometry
/// and dwell run at the user's DPI 2.0; the clock and the 100 ms timer are injected fakes the
/// test drives by hand, so every countdown is deterministic. Destructive dwells run at ✕/↩ (slot
/// 2, P2 D8) after an arm excursion (fixup B2); the chip's Undo disc sits in slot 0.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteUndoTests
{
    /// <summary>The injected monotonic clock, in milliseconds — the test moves it by hand.</summary>
    private sealed class FakeClock
    {
        public long NowMs;
        public long Read() => NowMs;
    }

    /// <summary>The injected 100 ms UI timer: the test raises ticks by hand and can observe
    /// whether the manager believes a countdown is running.</summary>
    private sealed class FakeUndoTimer : INoteUndoTimer
    {
        private Action? _tick;
        public bool IsRunning { get; private set; }
        public int Starts { get; private set; }

        public void Start(Action onTick)
        {
            _tick = onTick;
            IsRunning = true;
            Starts++;
        }

        public void Stop() => IsRunning = false;

        public void Dispose() => Stop();

        public void RaiseTick() => _tick?.Invoke();
    }

    private static (NoteManager Manager, FakeClock Clock, FakeUndoTimer Timer) MakeManager(
        out WeakReferenceMessenger messenger, Func<bool>? pausePoll = null, Func<bool>? suspend = null)
    {
        messenger = new WeakReferenceMessenger();
        var clock = new FakeClock();
        var timer = new FakeUndoTimer();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, undoTimer: timer);
        if (pausePoll != null) manager.SetPausePoll(pausePoll);
        if (suspend != null) manager.SetSuspendPredicate(suspend);
        return (manager, clock, timer);
    }

    /// <summary>One valid, non-gap sample at the manager's gate, timestamped in the detector's
    /// microseconds (independent of the injected clock's milliseconds).</summary>
    private static void Sample(NoteManager manager, PhysicalPoint point, long tUs) =>
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(point, tUs, IsInGap: false)));

    /// <summary>Arms a control (200 ms outside its circle, past the 150 ms release) and then
    /// holds it for 900 ms — enough for a fresh 600 ms dwell to complete.</summary>
    private static void ArmThenHold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            Sample(manager, new PhysicalPoint(centre.X + 10_000, centre.Y), t);
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            Sample(manager, centre, t);
    }

    /// <summary>Advances the injected clock by 100 ms and raises one timer tick. When
    /// <paramref name="withSample"/> is set, a valid sample lands first — the stamp stays fresh
    /// (P2 D5's condition 3), so the tick's other conditions decide. The sample position can be
    /// steered (the occluder and own-window facts park it on the chip).</summary>
    private static void Tick(NoteManager manager, FakeClock clock, FakeUndoTimer timer,
        ref long tUs, bool withSample = true, PhysicalPoint? sampleAt = null)
    {
        clock.NowMs += 100;
        if (withSample)
            Sample(manager, sampleAt ?? new PhysicalPoint(-50_000, -50_000), tUs += 100_000);
        timer.RaiseTick();
    }

    /// <summary>A dwell button's centre in physical px, through the SAME math the manager uses.</summary>
    private static PhysicalPoint ButtonCentrePhysical(PhysicalPoint anchor, int slot)
    {
        double winLeftLogical = DpiHelper.PhysicalToLogicalX(anchor.X) - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTopLogical = DpiHelper.PhysicalToLogicalY(anchor.Y) - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        var (lx, ly) = NoteLayout.ButtonCenter(slot);
        return new(
            DpiHelper.LogicalToPhysicalX(winLeftLogical + lx),
            DpiHelper.LogicalToPhysicalY(winTopLogical + ly));
    }

    /// <summary>The chip's Undo disc: the chip re-renders in place, so its ↺ sits in the note's
    /// slot 0 — the same NoteLayout math the manager registers the target with.</summary>
    private static PhysicalPoint UndoCentre(PhysicalPoint anchor) => ButtonCentrePhysical(anchor, 0);

    /// <summary>Creates a note, types into it and commits it — a saved Display note with a real
    /// window (StartEdit and the drawn chip need one).</summary>
    private static string SavedNote(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    // ── the D5 table: three kinds × Undo / expiry ──

    [StaFact]
    public void Removed_Undo_RestoresDisplayWithTheSavedText()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "saved text", anchor);
            var close = ButtonCentrePhysical(anchor, 2);

            ArmThenHold(manager, close, startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            ArmThenHold(manager, UndoCentre(anchor), startUs: 2_000_000);
            Check.WithCustomMessage("Undo of a Removed chip must restore the note to Display")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("Undo must restore the SAVED text")
                .That(manager.ActiveNotes[0].Text).IsEqualTo("saved text");
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
            Check.That(manager.ActiveNotes[0].UndoKind).IsNull();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Removed_Expiry_RemovesTheNote()
    {
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved text", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // 8 s of REACHABLE time: 79 ticks keep it, the 80th expires it (P2 D5).
            long tUs = 2_000_000;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("7.9 s of reachable time must not expire the chip")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("8.0 s of reachable time must expire a Removed chip: gone")
                .That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void DiscardedNew_Undo_RestoresEditWithTheDraft()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.CreateNoteAt(anchor);
            var id = manager.ActiveNotes[^1].Id;
            manager.Apply(new NoteAppend("draft text"));

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0); // ↩ Discard
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.DiscardedNew);

            ArmThenHold(manager, UndoCentre(anchor), startUs: 2_000_000);
            Check.WithCustomMessage("Undo of a discarded NEW draft must return to Edit")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(manager.IsEditing).IsTrue();
            Check.WithCustomMessage("Undo must restore the discarded draft, not the empty text")
                .That(manager.ActiveNotes[0].Draft).IsEqualTo("draft text");
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void DiscardedNew_Expiry_RemovesTheNeverSavedNote()
    {
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.CreateNoteAt(anchor);
            manager.Apply(new NoteAppend("draft text"));
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            long tUs = 2_000_000;
            for (int i = 0; i < 80; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("expiry of a discarded NEW draft: the note was never saved — gone")
                .That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void DiscardedExisting_Undo_RestoresEditWithTheDraft()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            manager.Apply(new NoteAppend(" changed"));

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0); // ↩ Discard
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.DiscardedExisting);

            ArmThenHold(manager, UndoCentre(anchor), startUs: 2_000_000);
            Check.WithCustomMessage("Undo of a discarded EXISTING draft must return to Edit")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(manager.IsEditing).IsTrue();
            Check.WithCustomMessage("Undo must restore the CHANGED draft")
                .That(manager.ActiveNotes[0].Draft).IsEqualTo("original changed");
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void DiscardedExisting_Expiry_RestoresDisplayWithThePreviousText()
    {
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            manager.Apply(new NoteAppend(" changed"));
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            long tUs = 2_000_000;
            for (int i = 0; i < 80; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("expiry of a discarded EXISTING draft returns to Display")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("the previous text must survive the expiry")
                .That(manager.ActiveNotes[0].Text).IsEqualTo("original");
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the Discard rows that do NOT chip (P2 D5's straight-back rows) ──

    [StaFact]
    public void Discard_OfAnUnchangedDraft_GoesStraightBackToDisplay()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            Check.WithCustomMessage("an unchanged draft has nothing to undo — straight back")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original");
            Check.That(manager.ActiveNotes[0].UndoKind).IsNull();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Discard_OfAnEmptiedExistingDraft_RestoresTheSavedText()
    {
        // The draft differs (emptied) but has NO text: only "a changed draft that has text" goes
        // through Undo (P2 D5's final rows as the ticket scopes them) — straight back, restored.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            for (int i = 0; i < 8; i++) manager.Apply(new NoteBackspace());
            Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("");

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Discard_OfANewEmptyDraft_RemovesTheNote()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.CreateNoteAt(anchor);

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Done_OnAnEmptiedExistingNote_EntersARemovedChip()
    {
        // The Done row P2 D5 replaces P1's interim silent restore with.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            for (int i = 0; i < 8; i++) manager.Apply(new NoteBackspace());

            ArmThenHold(manager, ButtonCentrePhysical(anchor, 0), startUs: 0); // ✓ Done

            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── no oscillation under a stationary gaze (leave-before-arm) ──

    [StaFact]
    public void AStationaryGazeOverCloseThenUndo_NeverOscillates()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            var close = ButtonCentrePhysical(anchor, 2);

            // Hold ✕ until the chip enters, then keep the eyes EXACTLY there for several seconds.
            ArmThenHold(manager, close, startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            for (long t = 2_000_000; t <= 6_000_000; t += 100_000)
                Sample(manager, close, t);
            Check.WithCustomMessage(
                    "a stationary gaze must not re-fire anything through the chip transition")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // Move up to the Undo disc: the dwell completes exactly once, and the restored card's
            // targets are born latched — the held Undo disc must not re-fire.
            ArmThenHold(manager, UndoCentre(anchor), startUs: 7_000_000);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            for (long t = 9_000_000; t <= 11_000_000; t += 100_000)
                Sample(manager, UndoCentre(anchor), t);
            Check.WithCustomMessage("the re-registered ✎/✕ column under the held gaze must stay put")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the chip stays dwellable while another note edits, and the real hand-over ──

    [StaFact]
    public void AChipStaysDwellable_WhileAnotherNoteEdits()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorA = new PhysicalPoint(1600, 1000);
            var anchorB = new PhysicalPoint(3000, 1000);
            var a = SavedNote(manager, "A", anchorA);
            ArmThenHold(manager, ButtonCentrePhysical(anchorA, 2), startUs: 0); // A → chip
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            manager.CreateNoteAt(anchorB); // B pins: the single-editor rule clears card targets,
                                           // the chip's own detector is NOT touched
            Check.That(manager.IsEditing).IsTrue();

            // The chip's Undo is still live: dwell it under the editing note B.
            ArmThenHold(manager, UndoCentre(anchorA), startUs: 2_000_000);

            Check.WithCustomMessage("a pending chip must stay dwellable while another note edits")
                .That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.Display);
            Check.That(manager.IsEditing).IsTrue(); // B is untouched by A's Undo (Removed)
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheHandOver_UndoOfADiscardedChip_WhileBEditsWithTheKeyboardUp()
    {
        // The one real keyboard hand-over (P1 D8's entry rule through Undo), with TEETH (T3
        // review-01 m3): the keyboard starts HIDDEN, so B's entry summons it and B OWNS it. Undo
        // on A then moves the claim to A without a hide or show, and A's later Done hides the
        // keyboard — proving A really inherited it.
        var messenger = new WeakReferenceMessenger();
        var clock = new FakeClock();
        var timer = new FakeUndoTimer();
        var probe = new NoteManagerTests.FakeKeyboardProbe { IsVisible = false }; // hidden at start
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, undoTimer: timer);
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorA = new PhysicalPoint(1600, 1000);
            var anchorB = new PhysicalPoint(3000, 1000);

            // A: saved, then edited with a changed draft — the DiscardedExisting chip's owner.
            var a = SavedNote(manager, "A text", anchorA);
            manager.StartEdit(a); // summons the hidden keyboard; A owns it
            Check.That(manager.EditingOwnsKeyboardForTest).IsTrue();
            manager.Apply(new NoteAppend(" changed"));
            ArmThenHold(manager, ButtonCentrePhysical(anchorA, 2), startUs: 0); // ↩ → chip
            Check.That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.PendingUndo);

            // B: pinned while A is chipped. The keyboard is hidden (A's discard released nothing
            // visible), so B's entry summons it and B owns it.
            manager.CreateNoteAt(anchorB);
            var b = manager.ActiveNotes.First(n => n.Id != a).Id;
            manager.Apply(new NoteAppend("B draft"));
            Check.WithCustomMessage("B's entry summoned the keyboard and took the claim")
                .That(manager.EditingOwnsKeyboardForTest).IsTrue();

            // The keyboard comes up for real (the fake's visibility is test-driven).
            probe.IsVisible = true;
            probe.RaiseChanged();

            // Counters start HERE: the hand-over under test must carry exactly one (true), no
            // hide and no show.
            int editingTrue = 0, editingFalse = 0, hideRequests = 0, showRequests = 0;
            messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) =>
            {
                if (m.Editing) editingTrue++; else editingFalse++;
            });
            messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hideRequests++);
            messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showRequests++);

            ArmThenHold(manager, UndoCentre(anchorA), startUs: 2_000_000); // Undo on A

            Check.WithCustomMessage("commit-first: B's draft must be saved before A takes the keyboard")
                .That(manager.ActiveNotes.First(n => n.Id == b).Text).IsEqualTo("B draft");
            Check.That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.Display);
            Check.WithCustomMessage("Undo of a Discarded chip must put A into Edit")
                .That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.Edit);
            Check.That(manager.IsEditing).IsTrue();
            Check.That(manager.ActiveNotes.First(n => n.Id == a).Draft).IsEqualTo("A text changed");
            Check.WithCustomMessage("the claim moved B → A through the hand-over")
                .That(manager.EditingOwnsKeyboardForTest).IsTrue();
            Check.WithCustomMessage("the hand-over sends NO hide request (P1 D8)")
                .That(hideRequests).IsEqualTo(0);
            Check.WithCustomMessage("the hand-over sends NO show request — the keyboard never bounced")
                .That(showRequests).IsEqualTo(0);
            Check.WithCustomMessage("exactly one NoteEditingChangedMessage(true), no (false) in between")
                .That(editingTrue).IsEqualTo(1);
            Check.That(editingFalse).IsEqualTo(0);

            // The proof of ownership: A's later Done HIDES the keyboard it inherited.
            var doneSlot = ButtonCentrePhysical(anchorA, 0);
            var away = new PhysicalPoint(doneSlot.X + 10_000, doneSlot.Y);
            for (long t = 4_000_000; t <= 4_200_000; t += 100_000) Sample(manager, away, t);
            ArmThenHold(manager, doneSlot, startUs: 4_300_000);
            Check.WithCustomMessage("A's Done must hide the keyboard — A really owns it")
                .That(hideRequests).IsEqualTo(1);
            Check.That(manager.IsEditing).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the chip clock: it counts while reachable, and stops under each condition ──

    [StaFact]
    public void TheChipClock_CountsWhileReachable_AndExpiresAtEightSeconds()
    {
        // The positive control (T2's last finding): with NOTHING blocking it, the clock counts —
        // the stop-condition facts below would otherwise pass for a guard that is always on.
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(timer.IsRunning).IsTrue();

            long tUs = 2_000_000;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
            Check.WithCustomMessage("the timer stops with the last chip (P2 D5)")
                .That(timer.IsRunning).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsUnderAGapOnlySampleStream()
    {
        // Condition 3: gap frames are NOT valid samples — looking away stops the clock.
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            long tUs = 2_000_000;
            for (int i = 0; i < 90; i++)
            {
                clock.NowMs += 100;
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(new PhysicalPoint(-50_000, -50_000), tUs += 100_000, IsInGap: true)));
                timer.RaiseTick();
            }
            Check.WithCustomMessage("a gap-only stream must stop the countdown — 9 s and still pending")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // Control: the stream heals and the same cadence expires the chip.
            for (int i = 0; i < 80; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhilePaused_IncludingAPauseOnlyThePollSees()
    {
        // Conditions 1: paused. The carried T2 review-01 m2 shape: the pause is entered from the
        // tray — the sample flag never carries it, NO samples flow afterwards, and the clock's
        // stamp is still within its 500 ms freshness for the first ticks. Only the POLLED pause
        // can stop the clock there; the freshness conjunct cannot (yet).
        bool paused = false;
        var (manager, clock, timer) = MakeManager(out _, pausePoll: () => paused);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            long tUs = 2_000_000;
            Sample(manager, new PhysicalPoint(-50_000, -50_000), tUs += 100_000); // the last sample before the pause
            paused = true; // the tray toggle: CurrentState == Paused, no sample carries it
            for (int i = 0; i < 4; i++)
            {
                clock.NowMs += 100; // the stamp is ≤ 400 ms old: condition 3 alone would count
                timer.RaiseTick();
            }
            Check.WithCustomMessage(
                    "a polled pause must stop the countdown while the sample stamp is still fresh")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // The pause lifts. The pause phase credited NOTHING, so the chip must survive exactly
            // 79 more seconds-tenths and expire on the 80th — a conjunct dropped above would have
            // credited the paused 400 ms and expired early.
            paused = false;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage(
                    "no time may be credited while paused: 7.9 s after the resume the chip is alive")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhileTheEyeCareBannerIsUp()
    {
        // Condition 2, production reason 1: the banner announces the inactive state — the
        // suspender the EventCoordinator wires into the predicate.
        bool suspended = false;
        var (manager, clock, timer) = MakeManager(out _, suspend: () => suspended);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            long tUs = 2_000_000;
            Sample(manager, new PhysicalPoint(-50_000, -50_000), tUs += 100_000); // the last sample before the suspender
            suspended = true;
            // The gate DROPS samples while a suspender holds, so the stamp goes stale regardless —
            // the suspender conjunct is only observable in the first 500 ms. Four silent ticks
            // probe exactly that window; nothing may be credited (the dropped 400 ms would expire
            // the chip 4 ticks early once the suspender lifts).
            for (int i = 0; i < 4; i++)
            {
                clock.NowMs += 100;
                timer.RaiseTick();
            }
            Check.WithCustomMessage("the banner's suspender must stop the countdown")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            suspended = false;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("no time may be credited while suspended: 7.9 s after it lifts the chip is alive")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhileTheRestartCardIsUp()
    {
        // Condition 2, production reason 2: the restart notice's card is a suspender.
        bool suspended = false;
        var (manager, clock, timer) = MakeManager(out _, suspend: () => suspended);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            long tUs = 2_000_000;
            Sample(manager, new PhysicalPoint(-50_000, -50_000), tUs += 100_000); // the last sample before the suspender
            suspended = true;
            // The gate DROPS samples while a suspender holds, so the stamp goes stale regardless —
            // the suspender conjunct is only observable in the first 500 ms. Four silent ticks
            // probe exactly that window; nothing may be credited (the dropped 400 ms would expire
            // the chip 4 ticks early once the suspender lifts).
            for (int i = 0; i < 4; i++)
            {
                clock.NowMs += 100;
                timer.RaiseTick();
            }
            Check.WithCustomMessage("the restart card's suspender must stop the countdown")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            suspended = false;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("no time may be credited while suspended: 7.9 s after it lifts the chip is alive")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhileTheActionMenuIsOpen()
    {
        // Condition 2, production reason 3: ActionMenuVisible is one of the suspending states.
        bool suspended = false;
        var (manager, clock, timer) = MakeManager(out _, suspend: () => suspended);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            long tUs = 2_000_000;
            Sample(manager, new PhysicalPoint(-50_000, -50_000), tUs += 100_000); // the last sample before the suspender
            suspended = true;
            // The gate DROPS samples while a suspender holds, so the stamp goes stale regardless —
            // the suspender conjunct is only observable in the first 500 ms. Four silent ticks
            // probe exactly that window; nothing may be credited (the dropped 400 ms would expire
            // the chip 4 ticks early once the suspender lifts).
            for (int i = 0; i < 4; i++)
            {
                clock.NowMs += 100;
                timer.RaiseTick();
            }
            Check.WithCustomMessage("the action menu's suspender must stop the countdown")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            suspended = false;
            for (int i = 0; i < 79; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.WithCustomMessage("no time may be credited while suspended: 7.9 s after it lifts the chip is alive")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhileTheChipIsOccluded()
    {
        // Condition 4's occluder half: a live occluder over the chip's Undo target stops the
        // countdown — the owner could not reach Undo there.
        var (manager, clock, timer) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            SavedNote(manager, "saved", anchor);
            var undo = UndoCentre(anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);

            manager.SetOccluderSources(driftNoticeBounds: () =>
                new PhysicalRect(undo.X - 300, undo.Y - 300, 600, 600));
            long tUs = 2_000_000;
            for (int i = 0; i < 90; i++)
                Tick(manager, clock, timer, ref tUs, sampleAt: undo);
            Check.WithCustomMessage("an occluded chip must not count down")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            // Control: the notice is gone and the gaze is off every target, so ONLY the clock
            // runs — the same ticks expire the chip.
            manager.SetOccluderSources(driftNoticeBounds: () => null);
            for (int i = 0; i < 80; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes).IsEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheChipClock_StopsWhenAnotherNoteWindowCoversTheChip()
    {
        // Condition 4's own-window half (review-03 n-7): the chip's centre must resolve to ITS
        // OWN window — a chip covered by another note window does not count down.
        var messenger = new WeakReferenceMessenger();
        var clock = new FakeClock();
        var timer = new FakeUndoTimer();
        var tester = new FakeHitTester();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: null,
            hitTester: tester, clockMs: clock.Read, undoTimer: timer);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorA = new PhysicalPoint(1600, 1000);
            var anchorB = new PhysicalPoint(1640, 1000);
            var a = SavedNote(manager, "A", anchorA);
            SavedNote(manager, "B", anchorB);
            var handleA = new WindowInteropHelper(manager.WindowForTest(a)!).EnsureHandle();

            // Chip A while the hit tester answers B's window: A's chip is covered by B.
            tester.Answer = handleA; // the ✕ dwell needs A's own window
            ArmThenHold(manager, ButtonCentrePhysical(anchorA, 2), startUs: 0);
            Check.That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.PendingUndo);

            long tUs = 2_000_000;
            tester.Answer = new WindowInteropHelper(
                manager.WindowForTest(manager.ActiveNotes.First(n => n.Id != a).Id)!).EnsureHandle();
            for (int i = 0; i < 90; i++)
                Tick(manager, clock, timer, ref tUs, sampleAt: UndoCentre(anchorA));
            Check.WithCustomMessage("a chip under a foreign topmost window must not count down")
                .That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.PendingUndo);

            // Control: the covering window is gone — the centre resolves to the chip again. The
            // gaze parks off every target, so only the clock runs and the expiry lands.
            tester.Answer = handleA;
            for (int i = 0; i < 80; i++)
                Tick(manager, clock, timer, ref tUs);
            Check.That(manager.ActiveNotes.Select(n => n.Id)).Not.Contains(a);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── the D7 inventory and the D9 lifecycle ──

    [Fact]
    public void LiveTargetRects_IncludesTheChipSUndoTarget()
    {
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddNoteTargetsForTest(anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.LiveTargetRects()).HasSize(1); // only the chip's ↺ remains live

            var undo = ButtonCentrePhysical(anchor, 0);
            Check.That(manager.LiveTargetRects()[0]).IsEqualTo(
                new PhysicalRect(undo.X - 60, undo.Y - 60, 120, 120));
            Check.That(id).IsNotEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [Fact]
    public void Dispose_StopsTheTimer_AndIgnoresLateTicksAndDwells()
    {
        var messenger = new WeakReferenceMessenger();
        var clock = new FakeClock();
        var timer = new FakeUndoTimer();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            clockMs: clock.Read, undoTimer: timer);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0);
            Check.That(timer.IsRunning).IsTrue();

            manager.Dispose();

            Check.WithCustomMessage("P2 D9: the chip timer must stop with the manager")
                .That(timer.IsRunning).IsFalse();
            Check.ThatCode(() =>
            {
                timer.RaiseTick(); // a late tick must neither throw nor resurrect anything
                Sample(manager, ButtonCentrePhysical(anchor, 0), 99_000_000);
            }).DoesNotThrow();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── capacity (P2 D5): IsAtCapacity, the yieldable rule, commit-first before capacity ──

    [StaFact]
    public void AtCapacity_ADiscardedExistingChipCounts_APinStaysSilent()
    {
        // 7 Display notes + 1 Discarded-existing chip: the chip does NOT yield (the saved text
        // still exists behind it) — at capacity, and the pin creates no 9th window.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(400, 400);
            for (int i = 0; i < 7; i++)
                SavedNote(manager, $"note {i}", new PhysicalPoint(400 + i * 600, 400));
            var chipped = SavedNote(manager, "chipped", new PhysicalPoint(4600, 400));
            manager.StartEdit(chipped);
            manager.Apply(new NoteAppend(" changed"));
            ArmThenHold(manager, ButtonCentrePhysical(new PhysicalPoint(4600, 400), 2), startUs: 0); // ↩ → chip

            Check.That(manager.ActiveNotes).HasSize(8);
            Check.That(manager.ActiveNotes.First(n => n.Id == chipped).State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("a DiscardedExisting chip counts as a live note (P2 D5)")
                .That(manager.IsAtCapacity).IsTrue();

            manager.CreateNoteAt(new PhysicalPoint(8000, 400));

            Check.WithCustomMessage("at capacity the pin stays silent — no 9th window is created")
                .That(manager.ActiveNotes).HasSize(8);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AtEightWindows_ARemovedChipYields_TheOldestIsFinalizedAndThePinCreates()
    {
        // 7 Display notes + 1 Removed chip: 8 windows but only 7 live notes. The pin finalizes
        // the chip (as on its expiry) and creates — the total never reaches 9.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            for (int i = 0; i < 7; i++)
                SavedNote(manager, $"note {i}", new PhysicalPoint(400 + i * 600, 400));
            var chipped = SavedNote(manager, "chipped", new PhysicalPoint(4600, 400));
            ArmThenHold(manager, ButtonCentrePhysical(new PhysicalPoint(4600, 400), 2), startUs: 0); // ✕ → chip

            Check.That(manager.ActiveNotes).HasSize(8);
            Check.WithCustomMessage("a Removed chip yields its slot — not at capacity")
                .That(manager.IsAtCapacity).IsFalse();

            manager.CreateNoteAt(new PhysicalPoint(8000, 400));

            Check.WithCustomMessage("the oldest yieldable chip is finalized (gone, as on expiry)")
                .That(manager.ActiveNotes.Select(n => n.Id)).Not.Contains(chipped);
            Check.That(manager.ActiveNotes).HasSize(8);
            Check.WithCustomMessage("the new note exists")
                .That(manager.ActiveNotes.Count(n => n.State == NoteState.Edit)).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AtEightWindows_TheFinalizedChipIsTheOldestYIELDABLEOne()
    {
        // The yieldable rule's finalize half: with a non-yieldable DiscardedExisting chip AND a
        // yieldable Removed chip outstanding at 8 windows, the pin finalizes the OLDEST YIELDABLE
        // one — the DiscardedExisting chip (older, but holding saved text) must survive.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            for (int i = 0; i < 6; i++)
                SavedNote(manager, $"note {i}", new PhysicalPoint(400 + i * 600, 400));
            var kept = SavedNote(manager, "kept", new PhysicalPoint(4000, 400));
            manager.StartEdit(kept);
            manager.Apply(new NoteAppend(" changed"));
            ArmThenHold(manager, ButtonCentrePhysical(new PhysicalPoint(4000, 400), 2), startUs: 0); // ↩ → DiscardedExisting chip (older)

            var yielded = SavedNote(manager, "yielded", new PhysicalPoint(4600, 400));
            ArmThenHold(manager, ButtonCentrePhysical(new PhysicalPoint(4600, 400), 2), startUs: 2_000_000); // ✕ → Removed chip (younger)

            Check.That(manager.ActiveNotes).HasSize(8);
            Check.That(manager.IsAtCapacity).IsFalse(); // 6 Display + the non-yieldable chip = 7 live

            manager.CreateNoteAt(new PhysicalPoint(8000, 400));

            Check.WithCustomMessage("the pin must finalize the OLDEST YIELDABLE chip — the Removed one")
                .That(manager.ActiveNotes.Select(n => n.Id)).Not.Contains(yielded);
            Check.WithCustomMessage("the older non-yieldable chip must survive the pin")
                .That(manager.ActiveNotes.First(n => n.Id == kept).State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes).HasSize(8);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PinNote_AtCapacityWithBEditingAnEmptiedExistingNote_CommitsBFirst_ThenFindsRoom()
    {
        // P2 D5's pin order: commit-first runs BEFORE the capacity evaluation. B's commit turns
        // the emptied existing note into a yieldable Removed chip, so the pin finds room (the
        // oldest yieldable chip — B's — is finalized) and creates.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            for (int i = 0; i < 7; i++)
                SavedNote(manager, $"note {i}", new PhysicalPoint(400 + i * 600, 400));
            var b = SavedNote(manager, "B original", new PhysicalPoint(4600, 400)); // 8th note
            manager.StartEdit(b);
            for (int i = 0; i < 10; i++) manager.Apply(new NoteBackspace());
            Check.That(manager.ActiveNotes.First(n => n.Id == b).Draft).IsEqualTo("");
            // 7 Display + B editing = 8 live notes: at capacity while B still edits.
            Check.That(manager.IsAtCapacity).IsTrue();

            manager.CreateNoteAt(new PhysicalPoint(8000, 400)); // the pin

            // The commit-first order is observable in what the pin found room FOR: B's commit
            // ran (emptying turned it into a Removed chip), then the pin finalized the oldest
            // yieldable chip — B's own — and created. B is gone; its Undo ended early, the
            // accepted cost P2 D5 records.
            Check.WithCustomMessage("commit-first must run BEFORE the capacity check: a capacity-"
                    + "first order would silently keep B editing and create nothing")
                .That(manager.ActiveNotes.Select(n => n.Id)).Not.Contains(b);
            Check.WithCustomMessage("the pin finds room and creates — never more than 8 windows")
                .That(manager.ActiveNotes).HasSize(8);
            Check.That(manager.ActiveNotes.Count(n => n.State == NoteState.Edit)).IsEqualTo(1);
            Check.That(manager.IsEditing).IsTrue();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── M1 (T3 review-01): StartEdit/StartMove on a chip are a no-op — no ghost editor ──

    [StaFact]
    public void StartEdit_OnARemovedChip_IsANoOp_NoGhostEditor()
    {
        // Reviewer probe R8: the old finalize-then-continue deleted the note, then carried on —
        // IsEditing stayed true with 0 notes and the keyboard was captured (T1's failure class).
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            for (int i = 0; i < 8; i++) manager.Apply(new NoteBackspace());
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 0), startUs: 0); // Done on the emptied note → chip
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            manager.StartEdit(id);

            Check.WithCustomMessage("StartEdit on a chip must be a no-op — no ghost editor")
                .That(manager.IsEditing).IsFalse();
            Check.WithCustomMessage("the note count is unchanged")
                .That(manager.ActiveNotes).HasSize(1);
            Check.WithCustomMessage("the chip stays pending, untouched")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void StartMove_OnARemovedChip_IsANoOp_TheOtherNotesControlsStayLive()
    {
        // M1's StartMove twin: the old path cleared the WHOLE main detector (every other note's
        // controls died) and registered a ghost :place that follows the gaze forever.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchorA = new PhysicalPoint(1600, 1000);
            var anchorB = new PhysicalPoint(3000, 1000);
            var a = SavedNote(manager, "A", anchorA);
            var b = SavedNote(manager, "B", anchorB);
            ArmThenHold(manager, ButtonCentrePhysical(anchorA, 2), startUs: 0); // ✕ → A chipped
            Check.That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.PendingUndo);
            var rectsBefore = manager.LiveTargetRects().Count; // A's ↺ + B's three cards

            manager.StartMove(a);

            Check.WithCustomMessage("StartMove on a chip must be a no-op — the chip stays")
                .That(manager.ActiveNotes.First(n => n.Id == a).State).IsEqualTo(NoteState.PendingUndo);
            Check.WithCustomMessage("no :place target, no detector clear — the live set is unchanged")
                .That(manager.LiveTargetRects().Count).IsEqualTo(rectsBefore);
            Check.That(manager.ActiveNotes).HasSize(2);
            Check.That(b).IsNotEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── m1 (T3 review-01): the chip-entry latch has teeth on the real path ──

    [StaFact]
    public void Done_OnAnEmptiedNote_ChipsTheUndoUnderTheHeldGaze_ItDoesNotFireUntilALeave()
    {
        // The one path where ↺ is born under a held gaze: Done (slot 0) on an EMPTIED existing
        // note leaves a Removed chip whose ↺ sits in slot 0, right where the eyes are. Without
        // the chip-entry latch (mutant U6) ↺ fires 600 ms later and silently restores the note
        // the owner just deleted.
        var (manager, _, _) = MakeManager(out _);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = SavedNote(manager, "original", anchor);
            manager.StartEdit(id);
            for (int i = 0; i < 8; i++) manager.Apply(new NoteBackspace());
            var slot0 = ButtonCentrePhysical(anchor, 0);
            var away = new PhysicalPoint(slot0.X + 10_000, slot0.Y);

            ArmThenHold(manager, slot0, startUs: 0); // Done fires → chip; ↺ born under the gaze
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            for (long t = 2_000_000; t <= 3_500_000; t += 100_000) Sample(manager, slot0, t);
            Check.WithCustomMessage("the ↺ born under the held gaze must stay latched")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            for (long t = 3_600_000; t <= 3_800_000; t += 100_000) Sample(manager, away, t); // 200 ms leave
            for (long t = 3_900_000; t <= 4_800_000; t += 100_000) Sample(manager, slot0, t);
            Check.WithCustomMessage("after a ≥150 ms leave and a return, ↺ fires (positive control)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original");
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ── m2 (T3 review-01): the chip's ↺ dwell is fixed at 600 ms, never adapted ──

    [Fact]
    public void ALengtheningMap_LeavesTheUndoDwellAtTheFixedSixHundredMs()
    {
        // Only the CARD detector is adaptation-registered (P2 D5). Under a lengthening map
        // (1200 ms) the chip's ↺ still fires at its fixed 600 ms — U1 (the map routed into the
        // undo detector too) must go RED. The second half proves the map IS live on the cards.
        var messenger = new WeakReferenceMessenger();
        var registry = new DwellDetectorRegistry();
        var clock = new FakeClock();
        var timer = new FakeUndoTimer();
        using var manager = new NoteManager(messenger, sink: null, dwellRegistry: registry,
            hitTester: InactiveNoteHitTester.Instance, clockMs: clock.Read, undoTimer: timer);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.AddNoteTargetsForTest(anchor);
            ArmThenHold(manager, ButtonCentrePhysical(anchor, 2), startUs: 0); // ✕ at 600 ms → chip
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);

            registry.Registrations[0].Apply(_ => 1200); // the lengthening map hits the cards only

            var slot0 = ButtonCentrePhysical(anchor, 0);
            ArmThenHold(manager, slot0, startUs: 2_000_000);
            Check.WithCustomMessage("the chip's ↺ dwell is fixed at 600 ms — a lengthening card map must not move it")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // The map is live on the cards: the restored ✕ (slot 2) does not fire at 900 ms —
            // 600 ms unadapted would — but does past the lengthened 1200 ms.
            var close = ButtonCentrePhysical(anchor, 2);
            ArmThenHold(manager, close, startUs: 4_900_000); // hold 900 ms < 1200: must NOT fire
            Check.WithCustomMessage("the lengthening map must actually hold the card target (900 ms < 1200)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            for (long t = 6_200_000; t <= 7_400_000; t += 100_000) Sample(manager, close, t);
            Check.WithCustomMessage("the card target fires past the lengthened dwell (the map was real)")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Recording <see cref="INoteHitTester"/> fake: the test decides which HWND the gaze
    /// is over, per sample or per tick.</summary>
    private sealed class FakeHitTester : INoteHitTester
    {
        public IntPtr Answer { get; set; }
        public IntPtr RootWindowAt(PhysicalPoint point) => Answer;
    }
}
