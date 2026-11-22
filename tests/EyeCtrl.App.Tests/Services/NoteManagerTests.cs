namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Tests.TestSupport;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NoteManagerTests
{
    public NoteManagerTests()
    {
        // The collection's ambient scale is 1.0; every geometry and dwell test below switches to
        // the user's 2.0 explicitly (and restores in a finally) — DPI bugs hide at 1.0, where
        // logical and physical coincide.
        DpiHelper.Initialize(1.0, 1.0);
    }

    [StaFact]
    public void CreateNote_AddsToActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        manager.CreateNoteAt(new PhysicalPoint(100, 200));

        Check.That(manager.ActiveNotes).HasSize(1);
        Check.That(manager.ActiveNotes[0].Anchor.X).IsEqualTo(100);
        Check.That(manager.ActiveNotes[0].Anchor.Y).IsEqualTo(200);
    }

    [StaFact]
    public void CreateNote_DefaultsToEditState_AndIsTheSingleEditor()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
        Check.That(manager.IsEditing).IsTrue();
    }

    [StaFact]
    public void RemoveNote_RemovesFromActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.RemoveNote(id);

        Check.That(manager.ActiveNotes).IsEmpty();
        Check.That(manager.IsEditing).IsFalse();
    }

    [StaFact]
    public void IsAtCapacity_ReturnsTrue_WhenMaxReached()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        // Each note carries text: a commit-first pin on an EMPTY new note would make that note
        // disappear again (P1 D4's Done table), silently undoing the capacity fill.
        for (int i = 0; i < 8; i++)
            TypeIntoNewDraftNote(manager, $"note {i}", new PhysicalPoint(i * 50, 0));

        Check.That(manager.IsAtCapacity).IsTrue();
    }

    [StaFact]
    public void CreateNote_DoesNotAdd_WhenAtCapacity()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        for (int i = 0; i < 8; i++)
            TypeIntoNewDraftNote(manager, $"note {i}", new PhysicalPoint(i * 50, 0));
        manager.CreateNoteAt(new PhysicalPoint(999, 999));

        Check.That(manager.ActiveNotes).HasSize(8);
    }

    [StaFact]
    public void StartEdit_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "test text");

        manager.StartEdit(id);

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("test text");
    }

    [StaFact]
    public void FinishEdit_CommitsTheDraft()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "hello world");

        manager.FinishEdit(id, commit: true);

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello world");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        Check.That(manager.IsEditing).IsFalse();
    }

    [StaFact]
    public void StartMove_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "test");

        manager.StartMove(id);

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Move);
    }

    [StaFact]
    public void FinishMove_UpdatesAnchor()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "test");
        manager.StartMove(id);

        manager.FinishMove(id, new PhysicalPoint(500, 600));

        Check.That(manager.ActiveNotes[0].Anchor.X).IsEqualTo(500);
        Check.That(manager.ActiveNotes[0].Anchor.Y).IsEqualTo(600);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void OnGazeMoved_UpdatesLastGazePoint_ForPlaceTarget()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "test");
        manager.StartMove(id);

        manager.OnGazeMoved(new PhysicalPoint(300, 400), 1000000);
        manager.FinishMove(id, new PhysicalPoint(300, 400));

        Check.That(manager.ActiveNotes[0].Anchor.X).IsEqualTo(300);
        Check.That(manager.ActiveNotes[0].Anchor.Y).IsEqualTo(400);
    }

    [StaFact]
    public void MoveOneNote_KeepsOtherNotesControls()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id1 = TypeIntoNewDraftNote(manager, "note1");
        manager.CreateNoteAt(new PhysicalPoint(300, 0));
        var id2 = manager.ActiveNotes[1].Id;
        manager.Apply(new NoteAppend("note2"));
        manager.FinishEdit(id2, commit: true);

        manager.StartMove(id1);
        manager.FinishMove(id1, new PhysicalPoint(100, 0));

        // Both notes should be in Display state and have their targets
        Check.That(manager.ActiveNotes).HasSize(2);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        Check.That(manager.ActiveNotes[1].State).IsEqualTo(NoteState.Display);

        // A ClearTargets anywhere in note 1's move flow would wipe note 2's targets: at rest each
        // Display note carries exactly ONE target — its reveal target (D1).
        Check.WithCustomMessage(
                "moving note 1 must not unregister note 2's reveal target — " +
                "a ClearTargets in the move flow wiped them")
            .That(manager.LiveTargetRects()).HasSize(2);

        // And functionally: after a look reveals note 2, dwelling its ✎ row cell still enters
        // Edit on NOTE 2 — the re-registered target really belongs to it, not a stale rect.
        long t0 = NoteTestBench.Reveal(manager, id2, 0);
        var edit2 = NoteTestBench.CellCentre(manager, id2, 0);
        var away = new PhysicalPoint(edit2.X + 10_000, edit2.Y);
        for (long t = t0; t <= t0 + 200_000; t += 33_000)
            manager.OnGazeMoved(away, t);
        for (long t = t0 + 300_000; t <= t0 + 1_200_000; t += 33_000)
            manager.OnGazeMoved(edit2, t);

        Check.WithCustomMessage("note 2's edit target no longer dwells after note 1's move")
            .That(manager.ActiveNotes.Single(n => n.Id == id2).State).IsEqualTo(NoteState.Edit);
        Check.That(manager.IsEditing).IsTrue();
    }

    [StaFact]
    public void StartEdit_DoesNotTriggerEditTwice()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "original");

        // Enter edit, re-save, re-enter — should work without retrigger
        manager.StartEdit(id);
        manager.FinishEdit(id, commit: true); // re-save unchanged
        manager.StartEdit(id);
        manager.FinishEdit(id, commit: true);

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void StartEdit_OnTheEditingNote_IsANoOp_NotASelfCommit()
    {
        // P1 D8: the single editor. A second StartEdit on the editing note must not run the
        // commit-first exit on itself — an empty new note would silently disappear.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.Apply(new NoteAppend("draft"));

        manager.StartEdit(id);

        Check.That(manager.IsEditing).IsTrue();
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("draft");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
    }

    [StaFact]
    public void CreateNote_ShowsWindow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        manager.CreateNoteAt(new PhysicalPoint(400, 300));

        // Regression guard: the original code only called Show() inside the window's own Loaded
        // handler, which never fires, so the note was never displayed. The window must be visible.
        Check.That(manager.AllWindowsVisible).IsTrue();
    }

    // ── the P1 Done table (P1 D4): no P1 path loses text ──

    [StaFact]
    public void Done_NewNoteWithText_SavesAndDisplays()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "hello");

        manager.FinishEdit(id, commit: true);

        Check.That(manager.ActiveNotes).HasSize(1);
        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void Done_NewNoteWithEmptyDraft_RemovesTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        int editingFalse = 0;
        messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) => { if (!m.Editing) editingFalse++; });
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.FinishEdit(id, commit: true);

        // Nothing was ever saved, so nothing remains — and the keyboard is still told that
        // editing ended (fixup M2): otherwise "→ Note" sticks to a keyboard typing into the OS.
        Check.That(manager.ActiveNotes).IsEmpty();
        Check.That(editingFalse).IsEqualTo(1);
    }

    [StaFact]
    public void Done_TrimBlankEdgeLines_BeforeSaving()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "\n \nhello\n\n  ");

        manager.FinishEdit(id, commit: true);

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello");
    }

    [StaFact]
    public void Done_ExistingNoteWithText_SavesAndDisplays()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "original");

        manager.StartEdit(id);
        manager.Apply(new NoteAppend(" changed"));
        manager.FinishEdit(id, commit: true);

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original changed");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void Done_ExistingNoteEmptied_EntersRemovedChip_AndUndoRestoresPreviousText()
    {
        // P2 D5 replaced P1's interim silent restore: Done on an emptied existing note leaves a
        // Removed chip, and Undo brings the saved text back (expiry would remove it instead).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var id = TypeIntoNewDraftNote(manager, "original");
        manager.FinishEdit(id, commit: true); // a real SAVED note

        manager.StartEdit(id);
        for (int i = 0; i < 8; i++)
            manager.Apply(new NoteBackspace());
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("");
        manager.FinishEdit(id, commit: true);

        Check.WithCustomMessage("the emptied note must chip, not silently restore (P2 D5)")
            .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);

        manager.UndoChipForTest(id);

        Check.That(manager.ActiveNotes).HasSize(1);
        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("original");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void Apply_AppendsBackspacesAndDeletesWord_InTheDraft_AndRerendersTheWindow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var note = manager.ActiveNotes[0];

        manager.Apply(new NoteAppend("thank you "));
        manager.Apply(new NoteDeleteWord());
        Check.That(note.Draft).IsEqualTo("thank ");

        manager.Apply(new NoteAppend("😀ab"));
        Check.That(note.Draft).IsEqualTo("thank 😀ab");
        manager.Apply(new NoteBackspace());
        Check.That(note.Draft).IsEqualTo("thank 😀a");
        manager.Apply(new NoteBackspace());
        Check.That(note.Draft).IsEqualTo("thank 😀");
        // An emoji is two UTF-16 units: Backspace removes the WHOLE text element, never half.
        manager.Apply(new NoteBackspace());
        Check.That(note.Draft).IsEqualTo("thank ");

        manager.Apply(new NoteNewline());
        Check.That(note.Draft).IsEqualTo("thank \n");

        // A prediction whose UTF-16 delete count would split an element snaps to the boundary.
        manager.Apply(new NoteAppend("😀x"));
        manager.Apply(new NotePrediction(3, "word "));
        // The raw cut of 3 units starts inside 😀, so the boundary snap deletes from 😀's start:
        // "😀x" goes whole (fixup m3 — the old comment wrongly claimed a snap was involved).
        Check.That(note.Draft).IsEqualTo("thank \nword ");
    }

    [StaFact]
    public void Apply_IsIgnored_WhenNoNoteIsEditing()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        // Defensive: the dispatchers only route while IsEditing, but Apply is public surface.
        Check.ThatCode(() => manager.Apply(new NoteAppend("x"))).DoesNotThrow();
        Check.That(manager.ActiveNotes).IsEmpty();
    }

    // ── keyboard summon and release (P1 D5) ──

    [StaFact]
    public void CreateNote_WithKeyboardHidden_SummonsTheKeyboard_AndAnnouncesEditing()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        int showRequests = 0, editingTrue = 0;
        messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showRequests++);
        messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) => { if (m.Editing) editingTrue++; });

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Check.That(showRequests).IsEqualTo(1);
        Check.That(editingTrue).IsEqualTo(1);
        Check.That(manager.EditingOwnsKeyboardForTest).IsTrue();
    }

    [StaFact]
    public void Done_OnAnOwningEditor_WithKeyboardVisible_HidesIt()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        probe.IsVisible = true; // hand-over in, as if the keyboard was up
        probe.RaiseChanged();
        int hideRequests = 0;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hideRequests++);

        manager.FinishEdit(id, commit: true);

        Check.That(hideRequests).IsEqualTo(1);
    }

    [StaFact]
    public void Done_WithKeyboardAlreadyHidden_SendsNoHide()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        int hideRequests = 0;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hideRequests++);

        manager.FinishEdit(id, commit: true);

        Check.That(hideRequests).IsEqualTo(0);
    }

    [StaFact]
    public void Done_WithoutOwnership_SendsNoHide_EvenWhenVisible()
    {
        // A foreign hide (edge-bar Close) cleared the claim; a later Done must not "hide" a
        // keyboard the note does not own — another owner may be holding it (P1 D5).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        probe.IsVisible = true;
        probe.RaiseChanged();
        probe.IsVisible = false;
        probe.RaiseChanged(); // foreign: the note did not request this hide
        Check.That(manager.EditingOwnsKeyboardForTest).IsFalse();
        probe.IsVisible = true;
        probe.RaiseChanged();
        int hideRequests = 0;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hideRequests++);

        manager.FinishEdit(id, commit: true);

        Check.That(hideRequests).IsEqualTo(0);
    }

    [StaFact]
    public void AnyVisibleToHiddenTransition_ClearsTheOwnershipClaim()
    {
        // Fixup m1 (execution deviations): there is no own-vs-foreign hide flag — the note's own
        // hide always runs after the ownership clear, so the rule is simply that ANY visible→
        // hidden transition clears the claim. Here the hide is foreign (edge-bar Close, toolbar
        // toggle) while the note stays in Edit.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        // Entry into Edit summoned the keyboard (it was hidden), so the note holds the claim.
        Check.That(manager.EditingOwnsKeyboardForTest).IsTrue();
        probe.IsVisible = true;
        probe.RaiseChanged();
        Check.That(manager.EditingOwnsKeyboardForTest).IsTrue();

        // The keyboard disappears without the note asking: the claim clears, so a later Done
        // cannot hide a keyboard the note does not own.
        probe.IsVisible = false;
        probe.RaiseChanged();
        Check.That(manager.EditingOwnsKeyboardForTest).IsFalse();
        Check.That(manager.IsEditing).IsTrue(); // the note stays in Edit; ⌨ re-registers
        Check.That(id).IsNotEmpty();
    }

    // ── the ⌨ Show-keyboard control (P1 D7/D8) ──

    [StaFact]
    public void KeyboardTarget_RegistersOnlyWhileTheKeyboardIsHidden()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        var id = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));

        // Hidden: Done (slot 0) + ⌨ (slot 1) + ↩ Discard (slot 2).
        Check.That(manager.LiveTargetRects()).HasSize(3);

        probe.IsVisible = true;
        probe.RaiseChanged();
        Check.WithCustomMessage("the ⌨ target must drain when the keyboard shows").That(manager.LiveTargetRects()).HasSize(2);

        probe.IsVisible = false;
        probe.RaiseChanged();
        Check.That(manager.LiveTargetRects()).HasSize(3);
        Check.That(id).IsNotEmpty();
    }

    [StaFact]
    public void KeyboardTargetDwell_ReSummonsTheKeyboard_AndTakesOwnership()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        int showRequests = 0;
        messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showRequests++);

        // Registration, dwell and assertions all at the user's 2.0: the centre math must be the
        // SAME scale the targets were registered at.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddEditingNoteTargetsForTest(anchor);
            int initial = showRequests; // the entry-into-Edit summon

            // Gaze on slot 1 (the ⌨ centre) past the 600 ms dwell.
            var centre = NoteTestBench.CellCentre(manager, id, 1);
            for (long t = 0; t <= 800_000; t += 33_000)
                manager.OnGazeMoved(centre, t);

            Check.That(showRequests).IsEqualTo(initial + 1);
            Check.That(manager.EditingOwnsKeyboardForTest).IsTrue();
            Check.That(id).IsNotEmpty();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // ── the keyboard gate (P1 D5): Done never fires under the keyboard ──

    [StaFact]
    public void KeyboardGate_GazeInsideTheKeyboardRect_NeverFiresDone()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            var done = NoteTestBench.CellCentre(manager, id, 0);

            // The keyboard covers the Done button: every sample is gated, so the dwell never
            // completes no matter how long the gaze rests.
            probe.RectsProvider = () => new[] { new PhysicalRect(done.X - 200, done.Y - 200, 400, 400) };
            probe.IsVisible = true;
            probe.RaiseChanged();
            for (long t = 0; t <= 2_000_000; t += 33_000)
                manager.OnGazeMoved(done, t);
            Check.WithCustomMessage("a gaze resting on Done through the keyboard must never fire Done")
                .That(manager.IsEditing).IsTrue();

            // Positive control: the gate follows the probe's rects. With the keyboard's rect far
            // away the same stream completes Done and commits the draft. The probe flip's
            // re-registration latches Done under the held gaze (P2 D4), so the control leaves the
            // circle first — the same arming every post-T2 dwell needs.
            probe.RectsProvider = () => new[] { new PhysicalRect(0, 0, 10, 10) };
            probe.RaiseChanged();
            for (long t = 2_100_000; t <= 2_400_000; t += 100_000)
                manager.OnGazeMoved(new PhysicalPoint(done.X + 10_000, done.Y), t);
            for (long t = 2_500_000; t <= 3_300_000; t += 100_000)
                manager.OnGazeMoved(done, t);
            Check.That(manager.IsEditing).IsFalse();
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello");
            Check.That(id).IsNotEmpty();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void KeyboardGate_KeepsDisplayControls_DeadUnderTheKeyboard()
    {
        // The gate is not Edit-specific: a Display note's ✕ sitting under the keyboard or its
        // edge bar must not accumulate dwell either (P1 D5 gates "note targets").
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddNoteTargetsForTest(anchor); // the seam's note is REVEALED
            const long t0 = 0;
            var close = NoteTestBench.CellCentre(manager, id, 3);

            probe.RectsProvider = () => new[] { new PhysicalRect(close.X - 200, close.Y - 200, 400, 400) };
            probe.IsVisible = true;
            for (long t = t0; t <= t0 + 2_000_000; t += 33_000)
                manager.OnGazeMoved(close, t);
            Check.WithCustomMessage("a Display note's close must not dwell under the keyboard rect")
                .That(manager.ActiveNotes).HasSize(1);

            probe.RectsProvider = () => Array.Empty<PhysicalRect>();
            for (long t = t0 + 3_000_000; t <= t0 + 3_800_000; t += 33_000)
                manager.OnGazeMoved(close, t);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(id).IsNotEmpty();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void DoneMovesTheControlsFromEditRowToTheRevealedRow_TheOldDoneCellIsInert_AndTheDisplayControlWorksAfterALeave()
    {
        // Fixup B2's positive control for R2, adapted to K2a and D: the Edit card's ✓ Done is in
        // its centred ROW under the text; after Done the note is AT REST (no control row at all),
        // so the old Done cell is empty space — a lingering held gaze there fires nothing. The
        // ✎ appears only after a look reveals the row; a leave and a return arm it and the dwell
        // re-enters Edit.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            // A REAL window: the liveness half dwells ✎, and StartEdit drives the note's window.
            var id = TypeIntoNewDraftNote(manager, "hello", anchor);
            var doneSlot = NoteTestBench.CellCentre(manager, id, 0); // ✓ in the Edit row

            for (long t = 0; t <= 700_000; t += 33_000) manager.OnGazeMoved(doneSlot, t);
            Check.That(manager.IsEditing).IsFalse();
            Check.WithCustomMessage("Done must have committed the draft")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // The old Done spot is inert on the at-rest card: a full second of gaze there fires
            // nothing (it may reveal the row, but a reveal is not an action).
            for (long t = 800_000; t <= 1_800_000; t += 33_000) manager.OnGazeMoved(doneSlot, t);
            Check.WithCustomMessage("the old Done row cell must fire nothing on the Display card")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // D: reveal the row, then leave past the release and dwell the Display ✎ row cell.
            long t0 = NoteTestBench.Reveal(manager, id, 2_000_000);
            var editSlot = NoteTestBench.CellCentre(manager, id, 0); // ✎ in the revealed row
            var away = new PhysicalPoint(editSlot.X + 10_000, editSlot.Y);
            for (long t = t0; t <= t0 + 200_000; t += 33_000) manager.OnGazeMoved(away, t);
            for (long t = t0 + 300_000; t <= t0 + 1_200_000; t += 33_000) manager.OnGazeMoved(editSlot, t);
            Check.WithCustomMessage("after the gaze left and returned, ✎ must work")
                .That(manager.IsEditing).IsTrue();
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void TheKeyboardGate_HoldsAfterAReveal_GatedDwellFiresNothing()
    {
        // Fixup F4's subject, re-based on D: after a look reveals the row, the ↕ cell is a live
        // target. A gated sample (gaze inside the keyboard's rect) must not complete its dwell;
        // the same held gaze fires the moment the gate opens. (The old "✎ still latched" shape
        // is gone: at rest there is no ✎ target, and a reveal latches the row it registers.)
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "hello", anchor); // a real window: ↕ must be able to move
            var slot0 = NoteTestBench.CellCentre(manager, id, 0); // ✓ in the Edit row

            for (long t = 0; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse(); // Done fired; the note is at rest
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // Reveal the row, then hold the ↕ cell under the keyboard's rect.
            long t0 = NoteTestBench.Reveal(manager, id, 800_000);
            var slot1 = NoteTestBench.CellCentre(manager, id, 1);

            probe.RectsProvider = () => new[] { new PhysicalRect(slot1.X - 300, slot1.Y - 300, 600, 600) };
            probe.IsVisible = true;
            probe.RaiseChanged();
            for (long t = t0; t <= t0 + 800_000; t += 33_000) manager.OnGazeMoved(slot1, t);
            Check.WithCustomMessage("a gated dwell must not fire the armed ↕")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);

            // The gate moves away: the same held gaze now fires the ↕ — the hold was the gate.
            probe.RectsProvider = () => Array.Empty<PhysicalRect>();
            probe.RaiseChanged();
            for (long t = t0 + 900_000; t <= t0 + 1_700_000; t += 33_000) manager.OnGazeMoved(slot1, t);
            Check.WithCustomMessage("once ungated, the armed ↕ dwell must complete")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Move);
            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(id).IsNotEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void GatedExcursions_CountForNothing_TowardALatchedTargetsLeave()
    {
        // Fixup F1's release rule, gated half: a sample under the keyboard rect is "no
        // information" — it neither advances a latched target's leave timer nor resets it.
        // A gated excursion past the circle must not satisfy the 150 ms leave.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddEditingNoteTargetsForTest(anchor);
            manager.Apply(new NoteAppend("hello"));
            var slot0 = NoteTestBench.CellCentre(manager, id, 0);
            var away = new PhysicalPoint(slot0.X + 10_000, slot0.Y);

            for (long t = 0; t <= 700_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.That(manager.IsEditing).IsFalse(); // Done fired; the ✎ re-registered under the held gaze is latched

            // The keyboard rect covers the away point: 500 ms of gated, outside-circle samples.
            probe.RectsProvider = () => new[] { new PhysicalRect(away.X - 5_000, away.Y - 5_000, 10_000, 10_000) };
            probe.IsVisible = true;
            probe.RaiseChanged();
            for (long t = 800_000; t <= 1_300_000; t += 33_000) manager.OnGazeMoved(away, t);

            // Gate gone, gaze back on the same spot: ✎ must still be latched.
            probe.RectsProvider = () => Array.Empty<PhysicalRect>();
            probe.RaiseChanged();
            for (long t = 1_400_000; t <= 2_300_000; t += 33_000) manager.OnGazeMoved(slot0, t);
            Check.WithCustomMessage("gated samples must not count toward the latch's 150 ms leave")
                .That(manager.IsEditing).IsFalse();
            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(id).IsNotEmpty();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void KeyboardTarget_DrawnVisibility_MatchesItsRegisteredTarget_AfterAProbeFlip()
    {
        // Review 01 mutant A: every manager test was windowless, so the drawn ⌨ could disagree
        // with the registered ⌨ target after a mid-Edit hide/show and nothing noticed. A real
        // window, a real probe flip, both halves asserted.
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        manager.CreateNoteAt(new PhysicalPoint(100, 100)); // shows a real window (STA)
        var window = manager.WindowForTest(manager.ActiveNotes[0].Id);
        Check.That(window).IsNotNull();
        var keyboardLabel = KeyboardGlyphLabel(window!);
        Check.That(keyboardLabel).IsNotNull();

        // Keyboard hidden: ⌨ drawn AND its target registered (plus Done and ↩ Discard).
        Check.That(keyboardLabel!.Visibility).IsEqualTo(System.Windows.Visibility.Visible);
        Check.That(manager.LiveTargetRects()).HasSize(3);

        probe.IsVisible = true;
        probe.RaiseChanged();
        Check.WithCustomMessage("the drawn ⌨ must drain with its target when the keyboard shows")
            .That(keyboardLabel.Visibility).IsEqualTo(System.Windows.Visibility.Collapsed);
        Check.That(manager.LiveTargetRects()).HasSize(2);

        probe.IsVisible = false;
        probe.RaiseChanged();
        Check.That(keyboardLabel.Visibility).IsEqualTo(System.Windows.Visibility.Visible);
        Check.That(manager.LiveTargetRects()).HasSize(3);
    }

    /// <summary>The ⌨ glyph's TextBlock in a note window's drawn tree, or null.</summary>
    private static System.Windows.Controls.TextBlock? KeyboardGlyphLabel(EyeCtrl.App.Overlays.NoteWindow window)
    {
        if (window.Content is not System.Windows.Controls.Canvas canvas) return null;
        foreach (var child in canvas.Children)
            if (child is System.Windows.Controls.TextBlock { Text: "⌨" } label)
                return label;
        return null;
    }

    // ── commit-first and the editor switch (P1 D8/D6) ──

    [StaFact]
    public void PinNote_WhileEditing_CommitsTheEditor_AndSendsExactlyOneEditingTrue()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false }; // the hand-over P1 can really produce
        manager.SetKeyboardProbe(probe);
        var a = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        manager.Apply(new NoteAppend("hello"));

        int editingTrue = 0, editingFalse = 0;
        messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) =>
        {
            if (m.Editing) editingTrue++; else editingFalse++;
        });

        manager.CreateNoteAt(new PhysicalPoint(300, 300));

        // A committed, B editing, and the switch carried exactly one (true) with no (false).
        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        Check.That(manager.IsEditing).IsTrue();
        Check.That(manager.ActiveNotes[1].Id).IsNotEqualTo(a);
        Check.WithCustomMessage("an editor switch sends exactly one (true) and no (false) (P1 D6)")
            .That(editingTrue).IsEqualTo(1);
        Check.That(editingFalse).IsEqualTo(0);
    }

    [StaFact]
    public void StartEdit_WhileAnotherNoteEdits_CommitsTheFirst()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var b = TypeIntoNewDraftNote(manager, "second"); // B: a real Display note with its window
        manager.FinishEdit(b, commit: true);
        var a = manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0)); // A: the editor
        manager.Apply(new NoteAppend("first draft"));

        manager.StartEdit(b);

        // A committed first; B is the new editor.
        Check.That(manager.IsEditing).IsTrue();
        Check.That(manager.ActiveNotes[0].Id).IsEqualTo(b);
        Check.That(manager.ActiveNotes[1].Id).IsEqualTo(a);
        Check.WithCustomMessage("commit-first: the outgoing editor keeps its draft as saved text")
            .That(manager.ActiveNotes[1].Text).IsEqualTo("first draft");
        Check.That(manager.ActiveNotes[1].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void HandOver_WithKeyboardVisible_SendsNeitherHideNorShow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);
        manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        int hideRequests = 0, showRequests = 0;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, _) => hideRequests++);
        messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) => showRequests++);

        manager.CreateNoteAt(new PhysicalPoint(300, 300));

        // The keyboard is up: ownership does NOT move (fixup M1 — the entry that found the
        // keyboard up claimed nothing, and a hand-over transfers nothing), and no hide or show
        // is sent (P1 D8), so fixation suppression is never released in between.
        Check.That(hideRequests).IsEqualTo(0);
        Check.That(showRequests).IsEqualTo(0);
        Check.That(manager.EditingOwnsKeyboardForTest).IsFalse();
    }

    [StaFact]
    public void Dispose_MidEdit_SendsEditingFalse()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        int editingFalse = 0;
        messenger.Register<object, NoteEditingChangedMessage>(this, (_, m) => { if (!m.Editing) editingFalse++; });

        manager.Dispose();

        Check.That(editingFalse).IsEqualTo(1);
    }

    [StaFact]
    public void Dispose_DetachesTheKeyboardProbe()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);
        manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));

        manager.Dispose();

        // The plan's recorded risk: a probe Changed raised after Dispose must be ignored, not
        // resurrect targets or throw.
        Check.ThatCode(() => probe.RaiseChanged()).DoesNotThrow();
        Check.That(manager.LiveTargetRects()).IsEmpty();
    }

    // ── review F1: the D17 inventory's note source — the DETECTOR's live targets as rects ──
    // Geometry and dwell run at the user's DPI 2.0, with every centre computed through the
    // production math (DpiHelper + NoteLayout), never hand-derived logical numbers.

    [StaFact]
    public void LiveTargetRects_EditState_IsDoneKeyboardAndDiscardWhenHidden_DrainsOnRemove()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        // Everything — registration included — at the user's 2.0.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            manager.CreateNoteAt(anchor); // Edit state: Done + ⌨ (keyboard hidden) + ↩ Discard
            var id = manager.ActiveNotes[0].Id;

            var done = NoteTestBench.CellCentre(manager, id, 0);
            var keyboard = NoteTestBench.CellCentre(manager, id, 1);
            // P4 D3 + K2a: in Edit the three controls are one ROW under the text, so their
            // cells share a Y and are ordered left → right: ✓, ⌨, ↩ (the discard control is
            // slot 3 of the control table but the row's third cell).
            var discard = NoteTestBench.CellCentre(manager, id, 3);
            var rects = manager.LiveTargetRects().OrderBy(r => r.X).ToList();

            Check.That(rects).HasSize(3);
            Check.That(rects[0]).IsEqualTo(CellRect(done));
            Check.That(rects[1]).IsEqualTo(CellRect(keyboard));
            Check.That(rects[2]).IsEqualTo(CellRect(discard));

            manager.RemoveNote(manager.ActiveNotes[0].Id);
            Check.That(manager.LiveTargetRects()).IsEmpty();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void LiveTargetRects_EditState_WithKeyboardVisible_IsDoneAndDiscard()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = true };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            manager.CreateNoteAt(new PhysicalPoint(1600, 1000));
            Check.That(manager.LiveTargetRects()).HasSize(2); // ✓ Done + ↩ Discard
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void LiveTargetRects_DisplayState_IsOnlyTheRevealTarget_UntilTheLookRevealsTheRow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var probe = new FakeKeyboardProbe { IsVisible = false };
        manager.SetKeyboardProbe(probe);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.FinishEdit(id, commit: true); // Display at rest

            // D1: exactly ONE target at rest — the reveal target — and it is the at-rest card
            // rect enlarged to at least 112x112 with its bottom edge on the card's bottom.
            var rest = manager.DrawnCardForTest(id);
            var target = manager.RevealTargetForTest(id);
            Check.That(manager.LiveTargetRects()).HasSize(1);
            Check.That(target).IsNotNull();
            var revealRect = target!.Value;
            Check.WithCustomMessage("the reveal target is at least one hit cell on each axis")
                .That(revealRect.Width >= LayoutConstants.NoteCellDip
                    && revealRect.Height >= LayoutConstants.NoteCellDip).IsTrue();
            Check.WithCustomMessage("the reveal target's bottom edge is the at-rest card's bottom")
                .That(revealRect.Bottom).IsEqualTo(rest.Bottom);
            Check.WithCustomMessage("it grows upward only (top at or above the card top)")
                .That(revealRect.Top <= rest.Top).IsTrue();

            // D4: reveal it — the row's three cells replace the reveal target.
            NoteTestBench.Reveal(manager, id, 0);
            var rects = manager.LiveTargetRects().OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
            var first = NoteTestBench.CellCentre(manager, id, 0);
            var second = NoteTestBench.CellCentre(manager, id, 1);
            // P4 D3: ✕ sits in the fourth row cell (slot 3).
            var fourth = NoteTestBench.CellCentre(manager, id, 3);
            Check.That(rects).HasSize(3);
            Check.That(rects).ContainsExactly(
                CellRect(first),
                CellRect(second),
                CellRect(fourth));
            Check.WithCustomMessage("✓/↕/✕ share one row under the text")
                .That(Math.Abs(first.Y - second.Y) < 1e-6 && Math.Abs(second.Y - fourth.Y) < 1e-6)
                .IsTrue();
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void LiveTargetRects_MoveState_IsThePlacementSquareAroundTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.StartMove(id); // the Move move-set: ↕ Other side (slot 0) + Cancel (slot 1)

            // The rect set equals the two column targets the re-derived placement registered —
            // each the dwell circle of its column button centre (NoteButtonRadius).
            var otherside = NoteTestBench.CellCentre(manager, id, 0);
            var cancel = NoteTestBench.CellCentre(manager, id, 1);
            double half = DpiHelper.LogicalToPhysicalX(EyeCtrl.Core.LayoutConstants.NoteCellDip / 2);
            // Order follows the detector's enumeration, not the registration order — sort both
            // sides by the row's X (D: the Move controls share one row, so Y no longer separates
            // them) and pin the two squares.
            Check.That(manager.LiveTargetRects().OrderBy(r => r.X).ToList()).ContainsExactly(
                new[] { otherside, cancel }.OrderBy(c => c.X).Select(c =>
                    new PhysicalRect(c.X - half, c.Y - half, half * 2, half * 2)).ToList());
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [StaFact]
    public void NoteLifecycle_PublishesNoteTargetsChanged_AtEveryStructuralMutation()
    {
        // The inventory's re-check trigger: every swap of the detector's target set must
        // broadcast NoteTargetsChangedMessage (create, display, re-edit, display, move, display,
        // remove) — a note appearing under a mid-read panel without the raise would leave the
        // panel up over fed dwell buttons (the R4-H1 harm).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var count = 0;
        var probe = new NoteTargetsProbe(() => count++);
        messenger.Register(probe);
        var anchor = new PhysicalPoint(0, 0);
        manager.CreateNoteAt(anchor);          // 1: Done + ⌨ registered
        var id = manager.ActiveNotes[0].Id;
        manager.Apply(new NoteAppend("x"));    // give the note text: an empty commit would remove it
        manager.FinishEdit(id, true);          // 2: close/edit/move
        manager.StartEdit(id);                 // 3: Done + ⌨ again
        manager.FinishEdit(id, true);          // 4: close/edit/move
        manager.StartMove(id);                 // 5: :place only
        manager.FinishMove(id, anchor);        // 6: all three again
        manager.RemoveNote(id);                // 7: drained

        Check.That(count).IsEqualTo(7);
    }

    /// <summary>Counting recipient for <see cref="NoteTargetsChangedMessage"/> (the CueProbe shape).</summary>
    internal sealed record NoteTargetsProbe(Action OnReceived) : IRecipient<NoteTargetsChangedMessage>
    {
        public void Receive(NoteTargetsChangedMessage message) => OnReceived();
    }

    [StaFact]
    public void CloseButtonDwell_AtVisiblePosition_EntersPendingUndo()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        // Regression guard for the dwell-target/visual-button mismatch: the buggy code placed the
        // target ~150px right of where the button renders. Everything at the user's 2.0, centres
        // through the production math.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.FinishEdit(id, commit: true); // -> Display at rest: only the reveal target
            // D (sitting 11): a Display note has no controls until a look reveals the row.
            long t0 = NoteTestBench.Reveal(manager, id, 0);
            var closeCenter = NoteTestBench.CellCentre(manager, id, 3);

            // Fixup B2: ✕ re-arms only after the gaze has LEFT its circle — a sample stream that
            // arrives at ✕ without a prior outside sample is exactly the held-Done hazard. Look
            // away first, then hold the close button.
            for (long t = t0; t <= t0 + 200_000; t += 33_000)
                manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);

            // Hold gaze on the close button past the 600ms dwell threshold.
            for (long t = t0 + 300_000; t <= t0 + 1_100_000; t += 33_000)
                manager.OnGazeMoved(closeCenter, t);

            // P2 D5: the dwell no longer deletes — the window re-renders in place as a chip.
            Check.That(manager.ActiveNotes).HasSize(1);
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].Id).IsEqualTo(id);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>
    /// Counts <see cref="DwellActivationMessage"/> arrivals on an isolated messenger — the
    /// dwell-completion cue's trigger. A record with a mutable counter so the facts can assert
    /// "exactly once" rather than merely "subscribed" (same probe shape as the Task-7 cue tests).
    /// <paramref name="effectRanAtSend"/> is captured at RECEIVE time to pin the D8 order: the
    /// note action must already have run when the cue fires.
    /// </summary>
    internal sealed record CueProbe(Func<bool>? EffectRanAtSend = null)
        : IRecipient<DwellActivationMessage>
    {
        public int Count { get; private set; }
        public bool? EffectAtSend { get; private set; }

        public void Receive(DwellActivationMessage message)
        {
            Count++;
            EffectAtSend = EffectRanAtSend?.Invoke();
        }
    }

    // The note overlay's straggler publish (spec D8): a real close-dwell fires the cue exactly
    // once, after the note action itself has run — asserted by reading ActiveNotes AT SEND
    // (external-review mutation teeth: a publish-before-effect reorder otherwise stays green).
    //
    // A malformed id ("note1:zzz") would still publish too: its parts.Length != 2 early return
    // is a no-op and that no-op-cue trade-off is ACCEPTED (spec §Pause). Recorded here as a
    // comment, deliberately NOT as an assertion (G4): a future handled-gate must not be blocked
    // by a pinned trade-off — the assertion below covers only the real-action case.
    [StaFact]
    public void CloseButtonDwell_PublishesDwellActivation()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.FinishEdit(id, commit: true); // -> Display at rest: only the reveal target
            long t0 = NoteTestBench.Reveal(manager, id, 0);
            // The ✕ effect (P2 D5) is the chip entry; the cue must fire AFTER the note is pending.
            var probe = new CueProbe(EffectRanAtSend: () =>
                manager.ActiveNotes.Count == 1 && manager.ActiveNotes[0].State == NoteState.PendingUndo);
            messenger.Register(probe);
            var closeCenter = NoteTestBench.CellCentre(manager, id, 3);

            // Fixup B2: arm ✕ by leaving its circle first (see CloseButtonDwell_AtVisiblePosition).
            for (long t = t0; t <= t0 + 200_000; t += 33_000)
                manager.OnGazeMoved(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t);

            for (long t = t0 + 300_000; t <= t0 + 1_100_000; t += 33_000)
                manager.OnGazeMoved(closeCenter, t);

            // The real action ran (nothing vacuous about the count) and the cue fired exactly once,
            // AFTER the note was already pending.
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(probe.Count).IsEqualTo(1);
            Check.WithCustomMessage(
                "The chip entry must land BEFORE the cue publishes (spec D8).")
                .That(probe.EffectAtSend).IsEqualTo(true);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // Same stream, delivered as the message the adapter actually sends, but gap-held: closing a note
    // is destructive and no-confirm, so a blink that freezes the gaze over the close button must not
    // delete it.
    [StaFact]
    public void HeldSamples_DoNotDwellCloseANote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.FinishEdit(id, commit: true);
            var closeCenter = NoteTestBench.CellCentre(manager, id, 3);

            for (long t = 0; t <= 800_000; t += 33_000)
                messenger.Send(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: true)));

            Check.That(manager.ActiveNotes).HasSize(1);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // W5(d) (build plan C15): the suspend predicate on note dwell, plus the resume reset. The seam
    // registers a Display note's dwell targets without a window (CreateNoteAt would show and focus
    // one). The detector's clock is timestamp-based, so while the gate drops samples its clock
    // pauses — and without the reset the first post-gate sample on the target the note left would
    // credit the whole pause, completing a close dwell the user never held over it (a note close is
    // destructive and no-confirm, review 03 F5). Mutation: drop the ResetAllState call on resume →
    // the stale close dwell completes at the first resumed sample (600 ms on the detector's clock)
    // and the note is silently removed → red. T2 (P2 D1) adds the arm-latch on recovery: the first
    // clear sample latches every target under the held gaze, so the negative control needs a leave
    // (≥ LeaveBeforeArmMs) before the ✕ can fire again.
    [Fact]
    public void ProbeGate_SuspendsNoteDwell_AndResetsOnResume()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        var modal = new EyeCtrl.App.Services.Coordinators.ModalInteractionState();
        manager.SetSuspendPredicate(() => modal.ProbeOpen);

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = manager.AddNoteTargetsForTest(anchor); // the seam's note is REVEALED
            const long t0 = 0;
            var closeCenter = NoteTestBench.CellCentre(manager, id, 3);

            // Pre-probe: 500 ms of a 600 ms close dwell accrued on the button. Every delivered sample
            // is 100 ms after the previous one, below DwellDetector's 150 ms silence clamp.
            for (long t = t0; t <= t0 + 500_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));

            // The gate holds for 500 ms on the SAME timeline (every sample dropped — under no gate
            // the 600 ms dwell would complete at t0+700 ms), so the destructive target cannot
            // complete underneath the probe's window.
            modal.ProbeOpen = true;
            for (long t = t0 + 600_000; t <= t0 + 1_000_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage("while the probe is open the close dwell must not advance")
                .That(manager.ActiveNotes).HasSize(1);

            // Resume. The recovery resets the detector and arm-latches ✕ under the held gaze
            // (P2 D1), so the held samples cannot fire it either.
            modal.ProbeOpen = false;
            for (long t = t0 + 1_100_000; t <= t0 + 1_600_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.WithCustomMessage(
                    "the probe resume must reset the detector and latch the target under the held gaze")
                .That(manager.ActiveNotes).HasSize(1);

            // Negative control: leave ✕'s circle for 200 ms (past the 150 ms latch release), return,
            // and the target is live and completes from the reset anchor — into the chip (P2 D5).
            for (long t = t0 + 1_700_000; t <= t0 + 1_900_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(
                    new GazeFrame(new PhysicalPoint(closeCenter.X + 10_000, closeCenter.Y), t, IsInGap: false)));
            for (long t = t0 + 2_000_000; t <= t0 + 2_800_000; t += 100_000)
                manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    // ── helpers ──

    /// <summary>Fixup 01 m4: entering Edit via a ✎ dwell registers ⌨ (keyboard hidden) in the
    /// SAME slot the gaze is holding — the RegisterSoleEditorTargets latch must hold it until a
    /// leave. Without it, ⌨ fires 600 ms later and sends a duplicate
    /// KeyboardShowRequestedMessage; T3's Undo-of-Discarded re-entry rides this exact path.
    /// Mutant: drop the latch call in RegisterSoleEditorTargets → the held gaze re-summons → red.</summary>
    [StaFact]
    public void EnteringEditViaADwell_LatchesTheShowKeyboardTargetUnderTheHeldGaze()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance, workAreaDip: NoteTestBench.WorkAreaProvider);
        // StartEdit needs the note's live window (it drives SetState on it), and the note must be
        // in DISPLAY so ✎ exists to dwell — this file's TypeIntoNewDraftNote leaves the note EDITING
        // (its CreateNoteAt already summoned the keyboard once), so the test exits that Edit
        // first. The counter then counts only what happens from the ✎ entry under test on.
        bool counting = false;
        int showRequests = 0;
        messenger.Register<object, KeyboardShowRequestedMessage>(this, (_, _) =>
        {
            if (counting) showRequests++;
        });

        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var anchor = new PhysicalPoint(1600, 1000);
            var id = TypeIntoNewDraftNote(manager, "x", anchor);
            manager.FinishEdit(id, commit: true);
            Check.That(manager.IsEditing).IsFalse();
            counting = true;
            // D: the Display note is at rest; reveal its row first, then the ✎ dwell is the look
            // the latch test is about.
            long t0 = NoteTestBench.Reveal(manager, id, 0);
            var editSlot = NoteTestBench.CellCentre(manager, id, 0); // ✎ in the revealed row
            var away = new PhysicalPoint(editSlot.X + 10_000, editSlot.Y);

            // Arm ✎ (200 ms outside), then dwell it: the note enters Edit, whose ✓ Done sits in
            // the ROW under the text (K2a), not in the Display row cell the gaze is holding. The
            // entry itself summons the keyboard exactly once.
            for (long t = t0; t <= t0 + 200_000; t += 100_000) manager.OnGazeMoved(away, t);
            for (long t = t0 + 300_000; t <= t0 + 1_100_000; t += 100_000) manager.OnGazeMoved(editSlot, t);
            Check.That(manager.IsEditing).IsTrue();
            Check.That(showRequests).IsEqualTo(1);

            // The held gaze must not re-summon: the Display row point is empty on the Edit
            // card, and the Edit targets were registered under a gaze that is not on any of them.
            for (long t = t0 + 1_200_000; t <= t0 + 2_400_000; t += 100_000) manager.OnGazeMoved(editSlot, t);
            Check.WithCustomMessage(
                    "the Edit-entry latch must hold the held slot — a duplicate show " +
                    "request is exactly the regression T3's Undo re-entry would ride")
                .That(showRequests).IsEqualTo(1);

            // Liveness: leave past the 150 ms release, then dwell ⌨ — the arm is gone, and ⌨
            // re-summons. Its cell exists only now: the Edit card's row is laid out by the flip.
            var keyboardSlot = NoteTestBench.CellCentre(manager, id, 1);
            for (long t = t0 + 2_500_000; t <= t0 + 2_700_000; t += 100_000) manager.OnGazeMoved(away, t);
            for (long t = t0 + 2_800_000; t <= t0 + 3_600_000; t += 100_000) manager.OnGazeMoved(keyboardSlot, t);
            Check.That(showRequests).IsEqualTo(2);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Creates a note and types <paramref name="text"/> into it through the routed
    /// <see cref="NoteManager.Apply"/> seam — the only way text enters a note in P1. Leaves the
    /// note IN EDIT (the draft holds <paramref name="text"/>; nothing is saved). T2 review-04's
    /// helper split: the name says the state it leaves, so a test never assumes the wrong one.</summary>
    private static string TypeIntoNewDraftNote(NoteManager manager, string text, PhysicalPoint? point = null)
    {
        manager.CreateNoteAt(point ?? new PhysicalPoint(0, 0));
        manager.Apply(new NoteAppend(text));
        return manager.ActiveNotes[0].Id;
    }

    /// <summary>Recording <see cref="IKeyboardProbe"/> fake: tests set IsVisible, choose the rects
    /// the gate reads, and raise <see cref="IKeyboardProbe.Changed"/> synchronously (the real
    /// probe's dispatcher hop is the probe's own concern).</summary>
    /// <summary>The 80x80 DIP dwell cell (P4 D3) around a column centre, in physical px at the
    /// live scale - exactly what DwellTargetFor registers and LiveTargetRects reports.</summary>
    private static PhysicalRect CellRect(PhysicalPoint centre)
    {
        double half = DpiHelper.LogicalToPhysicalX(EyeCtrl.Core.LayoutConstants.NoteCellDip / 2);
        return new PhysicalRect(centre.X - half, centre.Y - half, half * 2, half * 2);
    }

    internal sealed class FakeKeyboardProbe : IKeyboardProbe
    {
        public bool IsVisible { get; set; }
        public Func<IReadOnlyList<PhysicalRect>> RectsProvider { get; set; } = () => Array.Empty<PhysicalRect>();

        public IReadOnlyList<PhysicalRect> Rects() => RectsProvider();

        public event Action? Changed;

        public void RaiseChanged() => Changed?.Invoke();
    }
}
