namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T6 P5: the caretaker input WIRING, proven headlessly through the real control element — a
/// routed <c>TouchUp</c>/<c>MouseLeftButtonUp</c> raised on the drawn Ellipse (a fake
/// TouchDevice subclass stands in for the digitizer) — with the actions observed through the
/// manager's state and the messages it sends. Every accept path runs the SAME NoteManager
/// action the control's dwell runs: ✕ and Discard go through Undo, ✎ commits the current
/// editor first, the chip's ↺ undoes.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteControlInputTests
{
    /// <summary>A minimal TouchDevice stand-in: the routed event only needs a device identity —
    /// RaiseEvent never walks the abstract point queries.</summary>
    private sealed class FakeTouchDevice : TouchDevice
    {
        public FakeTouchDevice() : base(42) { }

        public override TouchPoint GetTouchPoint(IInputElement relativeTo) =>
            new(this, new Point(0, 0), new Rect(0, 0, 0, 0), TouchAction.Up);

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => new();
    }

    /// <summary>Counts the Edit entries/exits the manager broadcasts (the exactly-once probe)
    /// and the dwell cue (THE exactly-once observable: the production actions are all
    /// state-guarded and idempotent, so a double-fired touch shows only in the cue count —
    /// exactly one DwellActivationMessage per accepted invocation). Registered with explicit
    /// handlers — a private IRecipient would drag the MVVM source generator into a private
    /// nested type.</summary>
    private sealed class EditCounter
    {
        public int Started;
        public int Ended;
        public int Cues;
    }

    private static (NoteManager Manager, EditCounter Counter) MakeManager(out WeakReferenceMessenger messenger, Func<bool>? pausePoll = null)
    {
        messenger = new WeakReferenceMessenger();
        var counter = new EditCounter();
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        messenger.Register<NoteEditingChangedMessage>(counter,
            (_, message) =>
            {
                if (message.Editing) counter.Started++;
                else counter.Ended++;
            });
        messenger.Register<DwellActivationMessage>(counter, (_, _) => counter.Cues++);
        if (pausePoll != null) manager.SetPausePoll(pausePoll);
        return (manager, counter);
    }

    private static string SavedNote(NoteManager manager, string text, PhysicalPoint anchor)
    {
        manager.CreateNoteAt(anchor);
        var id = manager.ActiveNotes[^1].Id;
        manager.Apply(new NoteAppend(text));
        manager.FinishEdit(id, commit: true);
        return id;
    }

    private static Ellipse ControlFor(NoteManager manager, string id, string suffix)
    {
        var window = manager.WindowForTest(id)!;
        return window.ButtonsForTest()[$"{id}:{suffix}"];
    }

    private static void RaiseTouch(UIElement element) =>
        element.RaiseEvent(new TouchEventArgs(new FakeTouchDevice(), Environment.TickCount)
        {
            RoutedEvent = UIElement.TouchUpEvent
        });

    private static void RaiseClick(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        });

    [StaFact]
    public void TouchUp_RunsTheEditAction_ExactlyOnce_AndMarksTheEventHandled()
    {
        var (manager, counter) = MakeManager(out var messenger);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var edit = ControlFor(manager, id, "edit");

            // The creation itself entered Edit once — the probe counts the DELTA of the touch.
            int startedBefore = counter.Started;
            int cuesBefore = counter.Cues;

            // The window's input signal must fire EXACTLY once per physical touch — this is the
            // observable of the touch-fires-twice defect (the actions themselves are state-guarded
            // and idempotent, and the stale second invocation is policy-rejected, so the event
            // count is where a double fire can actually be caught).
            int fired = 0;
            var window = manager.WindowForTest(id)!;
            window.ControlInputActivated += (_, _, _) => fired++;

            int handled = 0;
            void OnHandled(object s, RoutedEventArgs e) { if (e.Handled) handled++; }
            edit.AddHandler(UIElement.TouchUpEvent, new RoutedEventHandler(OnHandled), handledEventsToo: true);

            RaiseTouch(edit);

            Check.WithCustomMessage("the touch must enter Edit through the same action the dwell runs")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.WithCustomMessage("the window's input signal must fire exactly ONCE per touch")
                .That(fired).IsEqualTo(1);
            Check.WithCustomMessage("exactly ONE NoteEditingChangedMessage(true) from the touch — a "
                    + "second run of the action would send another (the exactly-once probe)")
                .That(counter.Started - startedBefore).IsEqualTo(1);
            Check.WithCustomMessage("at MOST one dwell cue per touch — the stale second invocation "
                    + "(if a defect ever double-fires the signal) is policy-rejected, because the "
                    + "action consumed its own registration")
                .That(counter.Cues - cuesBefore).IsEqualTo(1);
            Check.WithCustomMessage("TouchUp must be marked handled")
                .That(handled).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Touch_OnAHiddenControl_DoesNothing()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            ArmThenChip(manager, id);
            // The chip hides every card control: the close ellipse is Collapsed now, and the
            // close target is unregistered — a touch on the dead element must do nothing.
            var close = ControlFor(manager, id, "close");
            int startedBefore = counter.Started;
            int endedBefore = counter.Ended;
            RaiseTouch(close);
            Check.WithCustomMessage("a touch on a hidden control must not act")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(counter.Started).IsEqualTo(startedBefore);
            Check.That(counter.Ended).IsEqualTo(endedBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Mouse_ActsOnlyWhilePaused()
    {
        bool paused = false;
        var (manager, counter) = MakeManager(out _, pausePoll: () => paused);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var edit = ControlFor(manager, id, "edit");
            int startedBefore = counter.Started;

            RaiseClick(edit);
            Check.WithCustomMessage("a click while EyeCtrl runs must be ignored — EyeCtrl's own "
                    + "synthesized clicks fire exactly then, and none of them may act on a note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(counter.Started).IsEqualTo(startedBefore);

            paused = true;
            RaiseClick(edit);
            Check.WithCustomMessage("a click while paused acts — the caretaker at the machine")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(counter.Started).IsEqualTo(startedBefore + 1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void AStylusPromotedMouseEvent_IsIgnored_EvenWhilePaused()
    {
        // The event path cannot synthesize a real StylusDevice headlessly (sealed, internal
        // constructor), so the promotion is driven through the manager seam with fromStylus:
        // true — the same input the window's handler reports for e.StylusDevice != null. The
        // un-promoted click path is proven through the element in Mouse_ActsOnlyWhilePaused.
        bool paused = true;
        var (manager, counter) = MakeManager(out _, pausePoll: () => paused);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            int startedBefore = counter.Started;
            manager.RunControlInput(id, "edit", NoteInputKind.Mouse, fromStylus: true);
            Check.WithCustomMessage("the touch already acted at TouchUp — the promoted click must "
                    + "never run the action a second time")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(counter.Started).IsEqualTo(startedBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void CloseByTouch_LeavesARemovedChip_AndUndoByTouchRestoresIt()
    {
        var (manager, _) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept text", new PhysicalPoint(1600, 1000));
            RaiseTouch(ControlFor(manager, id, "close"));
            Check.WithCustomMessage("✕ by touch must go through the SAME Undo protection as the dwell")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);

            RaiseTouch(ControlFor(manager, id, "undo"));
            Check.WithCustomMessage("↺ by touch restores the note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(manager.ActiveNotes[0].Text).IsEqualTo("kept text");
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void EditByTouch_EntersthroughTheSingleEditorRule_AndCommitsTheCurrentEditorFirst()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var a = SavedNote(manager, "base", new PhysicalPoint(1600, 1000));
            var b = SavedNote(manager, "second", new PhysicalPoint(1200, 100));

            // B gets a DiscardedNew chip first (its own short edit session, then Discard).
            manager.StartEdit(b);
            manager.Apply(new NoteAppend(" draft b"));
            manager.FinishEdit(b, commit: false);
            Check.That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.PendingUndo);

            // A becomes the editor and types a draft. The single-editor rule unregisters every
            // OTHER note's card targets — so a touch ✎ on B must be refused by the policy.
            manager.StartEdit(a);
            manager.Apply(new NoteAppend(" draft a"));
            Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("base draft a");
            int startedBefore = counter.Started;

            RaiseTouch(ControlFor(manager, b, "edit"));
            Check.WithCustomMessage("the single-editor rule protects the editor: B's ✎ touch "
                    + "(its target unregistered) must do nothing — A keeps the draft")
                .That(manager.ActiveNotes[0].Draft).IsEqualTo("base draft a");
            Check.That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.PendingUndo);
            Check.That(counter.Started).IsEqualTo(startedBefore);

            // The commit-first entry through touch, over the code ✎ shares: the chip's ↺ touch
            // runs ExitEditing(commit: true, handOver: true) BEFORE its Edit entry — it commits
            // A's draft first, then B enters Edit with its kept draft.
            RaiseTouch(ControlFor(manager, b, "undo"));
            Check.WithCustomMessage("the commit-first rule: the ↺ touch commits A's draft first")
                .That(manager.ActiveNotes.First(n => n.Id == a).Text).IsEqualTo("base draft a");
            Check.WithCustomMessage("and B enters Edit with its kept draft — the single editor")
                .That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.Edit);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static void ArmThenChip(NoteManager manager, string id)
    {
        var close = NoteTestBench.CellCentre(manager, id, 3);
        for (long t = 0; t <= 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(
                new PhysicalPoint(close.X + 10_000, close.Y), t, IsInGap: false)));
        for (long t = 300_000; t <= 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(close, t, IsInGap: false)));
    }
}
