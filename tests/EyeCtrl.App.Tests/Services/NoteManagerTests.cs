namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class NoteManagerTests
{
    static NoteManagerTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [StaFact]
    public void CreateNote_AddsToActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(100, 200));

        Check.That(manager.ActiveNotes).HasSize(1);
        Check.That(manager.ActiveNotes[0].Position.X).IsEqualTo(100);
        Check.That(manager.ActiveNotes[0].Position.Y).IsEqualTo(200);
    }

    [StaFact]
    public void CreateNote_DefaultsToEditState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
    }

    [StaFact]
    public void RemoveNote_RemovesFromActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.RemoveNote(id);

        Check.That(manager.ActiveNotes).IsEmpty();
    }

    [StaFact]
    public void IsAtCapacity_ReturnsTrue_WhenMaxReached()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));

        Check.That(manager.IsAtCapacity).IsTrue();
    }

    [StaFact]
    public void CreateNote_DoesNotAdd_WhenAtCapacity()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));
        manager.CreateNoteAt(new PhysicalPoint(999, 999));

        Check.That(manager.ActiveNotes).HasSize(8);
    }

    [StaFact]
    public void StartEdit_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test text");

        manager.StartEdit(id);

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
    }

    [StaFact]
    public void FinishEdit_SavesText()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.FinishEdit(id, "hello world");

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("hello world");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void StartMove_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");

        manager.StartMove(id);

        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Move);
    }

    [StaFact]
    public void FinishMove_UpdatesPosition()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.FinishMove(id, new PhysicalPoint(500, 600));

        Check.That(manager.ActiveNotes[0].Position.X).IsEqualTo(500);
        Check.That(manager.ActiveNotes[0].Position.Y).IsEqualTo(600);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void OnGazeMoved_UpdatesLastGazePoint_ForPlaceTarget()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.OnGazeMoved(new PhysicalPoint(300, 400), 1000000);
        manager.FinishMove(id, new PhysicalPoint(300, 400));

        Check.That(manager.ActiveNotes[0].Position.X).IsEqualTo(300);
        Check.That(manager.ActiveNotes[0].Position.Y).IsEqualTo(400);
    }

    [StaFact]
    public void MoveOneNote_KeepsOtherNotesControls()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        manager.CreateNoteAt(new PhysicalPoint(300, 0));
        var id1 = manager.ActiveNotes[0].Id;
        var id2 = manager.ActiveNotes[1].Id;
        manager.FinishEdit(id1, "note1");
        manager.FinishEdit(id2, "note2");

        manager.StartMove(id1);
        manager.FinishMove(id1, new PhysicalPoint(100, 0));

        // Both notes should be in Display state and have their targets
        Check.That(manager.ActiveNotes).HasSize(2);
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
        Check.That(manager.ActiveNotes[1].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void StartEdit_DoesNotTriggerEditTwice()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "original");

        // Enter edit, cancel, re-enter — should work without retrigger
        manager.StartEdit(id);
        manager.FinishEdit(id, "original"); // cancel
        manager.StartEdit(id);
        manager.FinishEdit(id, "changed");

        Check.That(manager.ActiveNotes[0].Text).IsEqualTo("changed");
        Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
    }

    [StaFact]
    public void CreateNote_ShowsWindow()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(400, 300));

        // Regression guard: the original code only called Show() inside the window's own Loaded
        // handler, which never fires, so the note was never displayed. The window must be visible.
        Check.That(manager.AllWindowsVisible).IsTrue();
    }

    // ── review F1: the D17 inventory's note source — the DETECTOR's live targets as rects ──
    // (DPI 1.0 per this collection's static ctor, so logical == physical and every number below
    // is hand-derived from LayoutConstants/NoteLayout, the same math RegisterDwellTargets uses.)

    [StaFact]
    public void LiveTargetRects_EditState_IsTheCloseButtonSquare_DrainsOnRemove()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);

        manager.CreateNoteAt(anchor); // Edit state: close-only

        // Close-button centre (the CloseButtonDwell formula): winLeft+240, winTop+64, radius 30.
        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        var expected = new PhysicalRect(winLeft + 240 - 30, winTop + 64 - 30, 60, 60);

        Check.That(manager.LiveTargetRects()).ContainsExactly(expected);

        manager.RemoveNote(manager.ActiveNotes[0].Id);

        Check.That(manager.LiveTargetRects()).IsEmpty();
    }

    [StaFact]
    public void LiveTargetRects_DisplayState_IsAllThreeButtonSquares()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;

        manager.FinishEdit(id, "x"); // Display: close/edit/move, stepping 70px down the column

        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = winLeft + 240;
        double topCy = winTop + 64;
        // The detector snapshot's order is unspecified (dictionary-backed) — compare as a set by
        // sorting both sides down the button column.
        var rects = manager.LiveTargetRects().OrderBy(r => r.Y).ToList();

        Check.That(rects).HasSize(3);
        Check.That(rects).ContainsExactly(
            new PhysicalRect(cx - 30, topCy - 30, 60, 60),
            new PhysicalRect(cx - 30, topCy + 70 - 30, 60, 60),
            new PhysicalRect(cx - 30, topCy + 140 - 30, 60, 60));
    }

    [StaFact]
    public void LiveTargetRects_MoveState_IsThePlacementSquareAroundTheNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "x");

        manager.StartMove(id); // only the :place target, radius 100 around the note position

        Check.That(manager.LiveTargetRects()).ContainsExactly(
            new PhysicalRect(anchor.X - 100, anchor.Y - 100, 200, 200));
    }

    [StaFact]
    public void NoteLifecycle_PublishesNoteTargetsChanged_AtEveryStructuralMutation()
    {
        // The inventory's re-check trigger: every swap of the detector's target set must
        // broadcast NoteTargetsChangedMessage (create, edit, display, move, park, remove) — a
        // note appearing under a mid-read panel without the raise would leave the panel up over
        // fed dwell buttons (the R4-H1 harm).
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var count = 0;
        var probe = new NoteTargetsProbe(() => count++);
        messenger.Register(probe);
        var anchor = new PhysicalPoint(0, 0);
        manager.CreateNoteAt(anchor);          // 1: close-only registered
        var id = manager.ActiveNotes[0].Id;
        manager.StartEdit(id);                 // 2: re-registered close-only
        manager.FinishEdit(id, "x");           // 3: all three buttons
        manager.StartMove(id);                 // 4: :place only
        manager.FinishMove(id, anchor);        // 5: all three again
        manager.RemoveNote(id);                // 6: drained

        Check.That(count).IsEqualTo(6);
    }

    /// <summary>Counting recipient for <see cref="NoteTargetsChangedMessage"/> (the CueProbe shape).</summary>
    internal sealed record NoteTargetsProbe(Action OnReceived) : IRecipient<NoteTargetsChangedMessage>
    {
        public void Receive(NoteTargetsChangedMessage message) => OnReceived();
    }

    [StaFact]
    public void CloseButtonDwell_AtVisiblePosition_RemovesNote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "x"); // -> Display: registers close/edit/move dwell targets

        // Close-button center, computed the same way NoteWindow lays the button out (DPI 1.0 here,
        // so logical == physical). Regression guard for the dwell-target/visual-button mismatch:
        // the buggy code placed the target ~150px right of where the button actually renders.
        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        double cy = EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        var closeCenter = new PhysicalPoint(winLeft + cx, winTop + cy);

        // Hold gaze on the close button past the 600ms dwell threshold.
        for (long t = 0; t <= 800_000; t += 33_000)
            manager.OnGazeMoved(closeCenter, t);

        Check.That(manager.ActiveNotes).IsEmpty();
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
        using var manager = new NoteManager(messenger);
        var probe = new CueProbe(EffectRanAtSend: () => manager.ActiveNotes.Count == 0);
        messenger.Register(probe);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "x"); // -> Display: registers close/edit/move dwell targets

        // Close-button center, computed the same way CloseButtonDwell_AtVisiblePosition_RemovesNote
        // does (DPI 1.0 here, so logical == physical).
        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        double cy = EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        var closeCenter = new PhysicalPoint(winLeft + cx, winTop + cy);

        for (long t = 0; t <= 800_000; t += 33_000)
            manager.OnGazeMoved(closeCenter, t);

        // The real action ran (nothing vacuous about the count) and the cue fired exactly once,
        // AFTER the note was already gone.
        Check.That(manager.ActiveNotes).IsEmpty();
        Check.That(probe.Count).IsEqualTo(1);
        Check.WithCustomMessage(
            "The note removal must land BEFORE the cue publishes (spec D8).")
            .That(probe.EffectAtSend).IsEqualTo(true);
    }

    // Same stream, delivered as the message the adapter actually sends, but gap-held: closing a note
    // is destructive and no-confirm, so a blink that freezes the gaze over the close button must not
    // delete it.
    [StaFact]
    public void HeldSamples_DoNotDwellCloseANote()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var anchor = new PhysicalPoint(800, 500);
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "x");

        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        double cy = EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        var closeCenter = new PhysicalPoint(winLeft + cx, winTop + cy);

        for (long t = 0; t <= 800_000; t += 33_000)
            messenger.Send(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: true)));

        Check.That(manager.ActiveNotes).HasSize(1);
    }

    // W5(d) (build plan C15): the probe gate on note dwell, plus the resume reset. The seam
    // registers a Display note's targets without a window (CreateNoteAt would show and focus one).
    // The detector's clock is timestamp-based, so while the gate drops samples its clock pauses —
    // and without the reset the first post-probe sample on the target the note left would credit
    // the whole pause, completing a close dwell the user never held over it (a note close is
    // destructive and no-confirm, review 03 F5). Mutation: drop the ResetAllState call on resume →
    // the stale close dwell completes at the first resumed sample (600 ms on the detector's clock)
    // and the note is silently removed → red.
    [Fact]
    public void ProbeGate_SuspendsNoteDwell_AndResetsOnResume()
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger);
        var modal = new EyeCtrl.App.Services.Coordinators.ModalInteractionState();
        manager.SetSuspendPredicate(() => modal.ProbeOpen);
        var anchor = new PhysicalPoint(800, 500);
        manager.AddNoteTargetsForTest(anchor);

        // Close-button centre, computed as the other note-dwell tests do (DPI 1.0).
        double winLeft = anchor.X - EyeCtrl.Core.LayoutConstants.NoteWindowWidth / 2;
        double winTop = anchor.Y - EyeCtrl.Core.LayoutConstants.NoteAnchorOffsetY;
        double cx = EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        double cy = EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius;
        var closeCenter = new PhysicalPoint(winLeft + cx, winTop + cy);

        // Pre-probe: 500 ms of a 600 ms close dwell accrued on the button. Every delivered sample
        // is 100 ms after the previous one, below DwellDetector's 150 ms silence clamp.
        for (long t = 0; t <= 500_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));

        // Probe open: the sample is dropped, so the destructive target cannot complete underneath
        // the probe's window no matter how long the pause is.
        modal.ProbeOpen = true;
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, 30_000_000, IsInGap: false)));
        Check.WithCustomMessage("while the probe is open the close dwell must not advance")
            .That(manager.ActiveNotes).HasSize(1);

        // Resume. Only the FIRST resumed sample may complete a stale dwell, so the stream stops
        // 500 ms into the reset dwell: the note survives only when the detector was reset at
        // t=600 ms rather than continuing the pre-probe run that started at t=0.
        modal.ProbeOpen = false;
        for (long t = 600_000; t <= 1_100_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, t, IsInGap: false)));
        Check.WithCustomMessage(
                "the probe resume must reset the detector — the stale close dwell completed and "
                + "removed the note the user never looked at while the probe was up")
            .That(manager.ActiveNotes).HasSize(1);

        // Negative control: the target is live and completes from the reset anchor.
        manager.Receive(new GazeCursorMovedMessage(new GazeFrame(closeCenter, 1_200_000, IsInGap: false)));
        Check.That(manager.ActiveNotes).IsEmpty();
    }
}
