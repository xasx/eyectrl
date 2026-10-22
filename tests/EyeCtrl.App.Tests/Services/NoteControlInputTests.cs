namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
/// T6 P5: the caretaker input WIRING, proven headlessly through the real control element —
/// a routed press (TouchDown / paused MouseLeftButtonDown) followed by the release raised on
/// the drawn Ellipse (a fake TouchDevice subclass stands in for the digitizer) — with the
/// actions observed through the manager's state and the messages it sends. T6 fixup-02:
/// press and release must land on the SAME control (m1), and the disc, glyph label and hit
/// cell all carry the wiring (B1). Every accept path runs the SAME NoteManager action the
/// control's dwell runs: ✕ and Discard go through Undo, ✎ commits the current editor first,
/// the chip's ↺ undoes.
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

    private static void RaiseTouchDown(UIElement element) =>
        element.RaiseEvent(new TouchEventArgs(new FakeTouchDevice(), Environment.TickCount)
        {
            RoutedEvent = UIElement.TouchDownEvent
        });

    private static void RaiseTouch(UIElement element) =>
        element.RaiseEvent(new TouchEventArgs(new FakeTouchDevice(), Environment.TickCount)
        {
            RoutedEvent = UIElement.TouchUpEvent
        });

    private static void RaiseMousePress(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        });

    private static void RaiseClick(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        });

    /// <summary>The release the window's clear watches — the core MouseUp, the ONE route every
    /// button's release bubbles on, visible through handledEventsToo even when a control marked
    /// its own (review-03 n.1: live input ALSO re-raises the direct MouseLeftButtonUp at every
    /// element along this route, so the window would see a left-button release either way).</summary>
    private static void RaiseMouseUp(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseUpEvent
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

            // m1: a release needs a matching press on the SAME control first.
            RaiseTouchDown(edit);
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
            // m1: the click is a press AND a release on the same control, both while paused.
            RaiseMousePress(edit);
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
            var close = ControlFor(manager, id, "close");
            RaiseTouchDown(close);
            RaiseTouch(close);
            Check.WithCustomMessage("✕ by touch must go through the SAME Undo protection as the dwell")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);

            var undo = ControlFor(manager, id, "undo");
            RaiseTouchDown(undo);
            RaiseTouch(undo);
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

            var bEdit = ControlFor(manager, b, "edit");
            RaiseTouchDown(bEdit);
            RaiseTouch(bEdit);
            Check.WithCustomMessage("the single-editor rule protects the editor: B's ✎ touch "
                    + "(its target unregistered) must do nothing — A keeps the draft")
                .That(manager.ActiveNotes[0].Draft).IsEqualTo("base draft a");
            Check.That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.PendingUndo);
            Check.That(counter.Started).IsEqualTo(startedBefore);

            // The commit-first entry through touch, over the code ✎ shares: the chip's ↺ touch
            // runs ExitEditing(commit: true, handOver: true) BEFORE its Edit entry — it commits
            // A's draft first, then B enters Edit with its kept draft.
            var bUndo = ControlFor(manager, b, "undo");
            RaiseTouchDown(bUndo);
            RaiseTouch(bUndo);
            Check.WithCustomMessage("the commit-first rule: the ↺ touch commits A's draft first")
                .That(manager.ActiveNotes.First(n => n.Id == a).Text).IsEqualTo("base draft a");
            Check.WithCustomMessage("and B enters Edit with its kept draft — the single editor")
                .That(manager.ActiveNotes.First(n => n.Id == b).State).IsEqualTo(NoteState.Edit);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ------------------------------------------------------------------ T6 fixup-02 (B1 + m1)
    // The review's measurings: the glyph TextBlock (32×43 DIP) is a hit-testable sibling drawn
    // ABOVE the disc, so only a thin disc ring acted; the 80×80 cell outside the disc was not a
    // touch target at all; and an Up without a matching Down acted. These pins run on the real
    // manager and the real window at DPI 2.0.

    private static TextBlock LabelFor(NoteManager manager, string id, string suffix) =>
        manager.WindowForTest(id)!.LabelsForTest()[$"{id}:{suffix}"];

    private static Rectangle CellFor(NoteManager manager, string id, int row) =>
        manager.WindowForTest(id)!.CellFillForTest(row)!;

    [StaFact]
    public void Touch_OnTheGlyphLabel_EntersEditExactlyOnce()
    {
        // T6 fixup-08 R4: this test's discriminating assertion is a VISUAL hit test on the drawn
        // tree, and an unrendered tree (the manager's seam keeps the window off the owner's
        // screen) resolves that hit to the glyph label even though the label is not hit-testable.
        // It needs the window actually shown, so it belongs behind the UI gate - never on the
        // owner's default run.
        Fixtures.AppFixture.SkipIfDisabled();

        // T6 fixup-09: the visual hit test below needs a RENDERED tree, so this test switches the
        // manager's show seam back on for its own scope. The restore is in a finally and the
        // class sits in the exclusive "DpiHelper Tests (App)" collection, so the process-global
        // flag cannot leak - and the UI gate keeps the default run from showing anything.
        NoteManager.RealNoteWindows = true;
        try
        {
            KeyboardControlInputAsserts();
        }
        finally
        {
            // Process-global: ALWAYS restore it, even when an assertion above fails.
            NoteManager.RealNoteWindows = false;
        }
    }

    /// <summary>The body of <see cref="Touch_OnTheGlyphLabel_EntersEditExactlyOnce"/>, run with
    /// the manager's show seam switched ON so the window is really rendered.</summary>
    private void KeyboardControlInputAsserts()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var label = LabelFor(manager, id, "edit");
            var editDisc = ControlFor(manager, id, "edit");

            // What a finger at the glyph lands on: at the glyph's middle the topmost hit must
            // never be the LABEL — a hit-testable label swallows the input and bubbles it to
            // the Canvas, past every wired element. Headless, WPF may serve a collapsed
            // sibling's 0×0 artefact as the top hit (the reviewer's probe saw the same), so
            // the discriminating assertion is the NEGATIVE one.
            var window = manager.WindowForTest(id)!;
            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
            var centre = new Point(Canvas.GetLeft(editDisc) + editDisc.Width / 2, Canvas.GetTop(editDisc) + editDisc.Height / 2);
            var hit = VisualTreeHelper.HitTest(canvas, centre)?.VisualHit;
            Check.That(hit).IsNotNull();
            Check.WithCustomMessage("a finger at the glyph's middle must land on ANY wired element — never the label")
                .That(ReferenceEquals(hit, label)).IsFalse();

            int startedBefore = counter.Started;
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            RaiseTouchDown(label);
            RaiseTouch(label);

            Check.WithCustomMessage("a tap on the GLYPH — what a caretaker actually aims at — must act")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Started - startedBefore).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PausedMouse_OnTheGlyphLabel_EntersEditExactlyOnce()
    {
        var (manager, counter) = MakeManager(out _, pausePoll: () => true);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var label = LabelFor(manager, id, "edit");
            int startedBefore = counter.Started;
            int cuesBefore = counter.Cues;
            int fired = 0;
            manager.WindowForTest(id)!.ControlInputActivated += (_, _, _) => fired++;

            RaiseMousePress(label);
            RaiseClick(label);

            Check.WithCustomMessage("a paused click on the GLYPH must act exactly once")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Started - startedBefore).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Touch_And_PausedMouse_OnTheCellOutsideTheDisc_ActExactlyOnce()
    {
        var (manager, counter) = MakeManager(out _, pausePoll: () => true);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;

            // The cell outside the disc is what hit-testing serves a finger at a cell corner:
            // 80×80 fill, the 56 DIP disc centred on it — a point 2,2 in from the cell corner
            // is ~53.7 DIP off centre, well outside the disc's radius (28 DIP).
            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
            var cell = window.CellFillForTest(3)!;
            var corner = new Point(Canvas.GetLeft(cell) + 2, Canvas.GetTop(cell) + 2);
            var hit = VisualTreeHelper.HitTest(canvas, corner)?.VisualHit;
            Check.WithCustomMessage("a finger at the cell corner lands on the CELL FILL — the touch "
                    + "target must be at least the area gaze uses")
                .That(hit).IsSameReferenceAs(cell);

            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;
            RaiseTouchDown(cell);
            RaiseTouch(cell);
            Check.WithCustomMessage("a release over the cell corner outside the disc runs the control's action")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PausedMouse_OnTheCellOutsideTheDisc_EntersEditExactlyOnce()
    {
        var (manager, counter) = MakeManager(out _, pausePoll: () => true);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var cell = CellFor(manager, id, 0);
            int startedBefore = counter.Started;
            int cuesBefore = counter.Cues;
            int fired = 0;
            manager.WindowForTest(id)!.ControlInputActivated += (_, _, _) => fired++;

            RaiseMousePress(cell);
            RaiseClick(cell);

            Check.WithCustomMessage("a paused click on the cell corner outside the disc must act exactly once")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Started - startedBefore).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PressOnEdit_ReleasedOnClose_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            int cuesBefore = counter.Cues;
            int fired = 0;
            manager.WindowForTest(id)!.ControlInputActivated += (_, _, _) => fired++;

            RaiseTouchDown(ControlFor(manager, id, "edit"));
            RaiseTouch(ControlFor(manager, id, "close"));

            Check.WithCustomMessage("a press on ✎ and a release on ✕ must run nothing — "
                    + "press and release belong to the SAME control")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void ReleaseWithoutAnyPress_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            int cuesBefore = counter.Cues;
            int fired = 0;
            manager.WindowForTest(id)!.ControlInputActivated += (_, _, _) => fired++;

            RaiseTouch(ControlFor(manager, id, "edit"));

            Check.WithCustomMessage("a release without a matching press must run nothing — "
                    + "a finger that slid onto the control and lifted cannot act")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void MousePressWhileEyeCtrlRuns_StartsNothing_EvenReleasedWhilePaused()
    {
        bool paused = false;
        var (manager, counter) = MakeManager(out _, pausePoll: () => paused);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            int startedBefore = counter.Started;

            // The press is made while EyeCtrl runs: it records nothing even though the
            // release lands while paused — a caretaker click is made AND given while paused.
            RaiseMousePress(ControlFor(manager, id, "edit"));
            paused = true;
            RaiseClick(ControlFor(manager, id, "edit"));

            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(counter.Started).IsEqualTo(startedBefore);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PressAndReleaseOnClose_MarksRemovedExactlyOnce()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept", new PhysicalPoint(1600, 1000));
            int cuesBefore = counter.Cues;
            int fired = 0;
            manager.WindowForTest(id)!.ControlInputActivated += (_, _, _) => fired++;

            var close = ControlFor(manager, id, "close");
            RaiseTouchDown(close);
            RaiseTouch(close);
            RaiseTouch(close); // the release consumed the pending press; a second Up acts nothing

            Check.That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            Check.That(manager.ActiveNotes[0].UndoKind).IsEqualTo(PendingUndoKind.Removed);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void GlyphLabel_Arc_AndBadgeTag_AreNotHitTestable()
    {
        var (manager, _) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // Two notes so a badge is pushed (RefreshBadges shows tags from 2 non-chip notes).
            SavedNote(manager, "one", new PhysicalPoint(1600, 1000));
            var second = SavedNote(manager, "two", new PhysicalPoint(1200, 100));
            var window = manager.WindowForTest(second)!;
            Check.That(window.BadgeTagForTest).IsNotNull();

            foreach (var (key, label) in window.LabelsForTest())
            {
                Check.WithCustomMessage($"the glyph label {key} must not be a hit target — it covers the "
                        + "middle of every disc and would swallow the input")
                    .That(label.IsHitTestVisible).IsFalse();
            }
            Check.WithCustomMessage("the dwell arc is drawn above the disc during a dwell and must "
                    + "never swallow the input under it")
                .That(window.ArcForTest("edit")!.IsHitTestVisible).IsFalse();
            Check.WithCustomMessage("the badge tag is pure chrome — a filled Border would otherwise "
                    + "be a hit surface over the card's top edge")
                .That(window.BadgeTagForTest!.IsHitTestVisible).IsFalse();
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    // ------------------------------------------------------------------ T6 fixup-03 (review-02 F2)
    // The review's measurings: a pending press survived releases and leaves the window never
    // saw an Up for — press ✕, drag off the note and lift outside, then a bare Up on ✕ REMOVED
    // the note; press ✕ and release over the card body, then a bare Up on ✕ did the same. The
    // fix consumes the pending press on every release/leave the WINDOW can see, while a
    // control's own Up still acts first (routing order: the element's handlers run before the
    // window's). These pins run the two measured sequences and the hit path a SHOWN window
    // would use, headlessly on the real manager and window at DPI 2.0.

    /// <summary>The element a SHOWN window routes input to: skip Collapsed / Hidden /
    /// non-hit-testable elements — an unshown window does not apply that view filter, which is
    /// why the plain headless HitTest can serve collapsed siblings' 0×0 artefacts.</summary>
    private static UIElement? TopHit(Canvas canvas, Point p)
    {
        UIElement? top = null;
        VisualTreeHelper.HitTest(canvas,
            d => d is UIElement u && (u.Visibility != Visibility.Visible || !u.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            r => { top = r.VisualHit as UIElement; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(p));
        return top;
    }

    private static void RaiseTouchLeave(UIElement element) =>
        element.RaiseEvent(new TouchEventArgs(new FakeTouchDevice(), Environment.TickCount)
        {
            RoutedEvent = UIElement.TouchLeaveEvent
        });

    private static void RaiseMouseLeave(UIElement element) =>
        element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = UIElement.MouseLeaveEvent
        });

    [StaFact]
    public void Touch_OnTheShownWindowHitElement_ActsExactlyOnce()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "hello", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;
            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
            var editDisc = ControlFor(manager, id, "edit");
            double cx = Canvas.GetLeft(editDisc) + editDisc.Width / 2;
            double cy = Canvas.GetTop(editDisc) + editDisc.Height / 2;
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            // What a shown window routes a finger at the glyph's middle to: the DISC — the
            // label (not hit-testable) and every collapsed sibling artefact are filtered —
            // and down+up there enters Edit exactly once.
            var centre = TopHit(canvas, new Point(cx, cy));
            Check.WithCustomMessage("the shown-window hit at the glyph's middle is the disc — " +
                    "the label is skipped by the view filter")
                .That(centre).IsSameReferenceAs(editDisc);
            RaiseTouchDown(centre!);
            RaiseTouch(centre!);
            Check.WithCustomMessage("down+up on what a shown window routes the glyph-centre touch to acts ONCE")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(fired).IsEqualTo(1);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(1);
            manager.FinishEdit(id, commit: true); // back to Display for the second point

            // The cell edge left of the disc: outside the disc the shown window routes to the
            // CELL, and down+up there acts exactly once too.
            var edge = TopHit(canvas, new Point(cx - 38, cy));
            Check.WithCustomMessage("the shown-window hit at the cell edge outside the disc is the cell fill")
                .That(edge).IsSameReferenceAs(CellFor(manager, id, 0));
            RaiseTouchDown(edge!);
            RaiseTouch(edge!);
            Check.WithCustomMessage("down+up on what a shown window routes the cell-edge touch to acts ONCE")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Edit);
            Check.That(fired).IsEqualTo(2);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(2);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Touch_PressClose_DragOffTheWindow_ThenBareUpOnClose_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;
            var close = ControlFor(manager, id, "close");
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            RaiseTouchDown(close);
            RaiseTouchLeave(window); // the finger dragged off the note and lifted outside — no Up arrives
            RaiseTouch(close);       // later a finger slides onto ✕ with no Down on it and lifts

            Check.WithCustomMessage("press ✕, drag off the window, then a bare Up on ✕ must keep the note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PausedMouse_PressClose_DragOffTheWindow_ThenBareUpOnClose_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _, pausePoll: () => true);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;
            var close = ControlFor(manager, id, "close");
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            RaiseMousePress(close);
            RaiseMouseLeave(window); // the caretaker dragged the pointer off the note
            RaiseClick(close);       // later a bare release on ✕

            Check.WithCustomMessage("press ✕, drag the pointer off the window, then a bare Up on ✕ must keep the note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Touch_PressClose_ReleasedOnTheCardBody_ThenBareUpOnClose_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;
            var canvas = (Canvas)window.Content;
            var close = ControlFor(manager, id, "close");
            var cardBody = canvas.Children.OfType<Border>().First();
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            RaiseTouchDown(close);
            RaiseTouch(cardBody); // lifted over the card text — no control handler runs there
            RaiseTouch(close);    // later a bare Up on ✕

            Check.WithCustomMessage("press ✕, release over the card body, then a bare Up on ✕ must keep the note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void PausedMouse_PressClose_ReleasedOnTheCardBody_ThenBareUpOnClose_RunsNothing()
    {
        var (manager, counter) = MakeManager(out _, pausePoll: () => true);
        manager.NoticeWindowShowsForTest = false;
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = SavedNote(manager, "kept", new PhysicalPoint(1600, 1000));
            var window = manager.WindowForTest(id)!;
            var canvas = (Canvas)window.Content;
            var close = ControlFor(manager, id, "close");
            var cardBody = canvas.Children.OfType<Border>().First();
            int cuesBefore = counter.Cues;
            int fired = 0;
            window.ControlInputActivated += (_, _, _) => fired++;

            RaiseMousePress(close);
            RaiseMouseUp(cardBody); // lifted over the card text
            RaiseClick(close);    // later a bare Up on ✕

            Check.WithCustomMessage("paused press ✕, release over the card body, then a bare Up on ✕ must keep the note")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
            Check.That(fired).IsEqualTo(0);
            Check.That(counter.Cues - cuesBefore).IsEqualTo(0);
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

    // ------------------------------------------------------------------ T6 fixup-04 (review-03 P1-P4)
    // review-03's concern about the fixup-03 test model: control releases were raised on the
    // LEFT-SPECIFIC event, which skips the core bubble a live release always has; live input's
    // path is the core MouseDown/MouseUp on the hit element, whose class handlers re-raise the
    // direct left-button events at EVERY element along the route. These pins adopt the
    // reviewer's core-event probes: with the fixup-04 cleanup in place (no OriginalSource
    // guard), each sequence's outcome must be identical on the class-handler path.

    /// <summary>The press a live mouse raises: the CORE MouseDown on the hit element — the
    /// class handler re-raises the direct MouseLeftButtonDown there, so the paused-press gate
    /// records the control exactly as it must when the caretaker really clicks.</summary>
    private static void CoreMousePress(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent
        });

    /// <summary>The release a live mouse raises: the CORE MouseUp on the element under the
    /// pointer — the class handler re-raises the direct MouseLeftButtonUp there, its instance
    /// handlers run, and only then does the core event bubble on to the window's clear.</summary>
    private static void CoreMouseRelease(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseUpEvent
        });

    private static (NoteManager Manager, string Id, NoteWindow Window, int[] Cues) SavedSetup()
    {
        var messenger = new WeakReferenceMessenger();
        var cues = new int[1];
        messenger.Register<DwellActivationMessage>(cues, (_, _) => cues[0]++);
        var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
        manager.NoticeWindowShowsForTest = false;
        manager.SetPausePoll(() => true); // a caretaker click: EyeCtrl paused
        var id = SavedNote(manager, "hello", new PhysicalPoint(1200, 1000));
        return (manager, id, manager.WindowForTest(id)!, cues);
    }

    [StaFact]
    public void CoreEventPipeline_TheReviewersProbeSequences_KeepTheirOutcomes()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            // P1: down and up on ✕ — the standard caretaker click acts exactly once.
            {
                var (m, id, w, cues) = SavedSetup();
                try
                {
                    var close = w.ButtonsForTest()[$"{id}:close"];
                    int cuesBefore = cues[0];
                    CoreMousePress(close);
                    CoreMouseRelease(close);
                    Check.WithCustomMessage("P1: a core down+up on ✕ must enter the Undo chip exactly once")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
                    Check.That(cues[0] - cuesBefore).IsEqualTo(1);
                }
                finally { m.Dispose(); }
            }

            // P2: down on ✕, release over the card body; then a bare up on ✕ — no act, no act.
            {
                var (m, id, w, cues) = SavedSetup();
                try
                {
                    var close = w.ButtonsForTest()[$"{id}:close"];
                    var cardBody = ((Canvas)w.Content).Children.OfType<Border>().First();
                    int cuesBefore = cues[0];
                    int fired = 0;
                    w.ControlInputActivated += (_, _, _) => fired++;
                    CoreMousePress(close);
                    CoreMouseRelease(cardBody);
                    Check.WithCustomMessage("P2: a core release over the card body must not act")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    CoreMouseRelease(close);
                    Check.WithCustomMessage("P2: the bare core up after the card-body release must not act (F2)")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    Check.That(fired).IsEqualTo(0);
                    Check.That(cues[0] - cuesBefore).IsEqualTo(0);
                }
                finally { m.Dispose(); }
            }

            // P3: overshoot — down on the ✕ disc, release on ✕'s own cell: same control, acts once.
            {
                var (m, id, w, cues) = SavedSetup();
                try
                {
                    var close = w.ButtonsForTest()[$"{id}:close"];
                    var cell = w.CellFillForTest(3)!;
                    int cuesBefore = cues[0];
                    CoreMousePress(close);
                    CoreMouseRelease(cell);
                    Check.WithCustomMessage("P3: an in-window overshoot onto ✕'s own cell acts exactly once")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
                    Check.That(cues[0] - cuesBefore).IsEqualTo(1);
                }
                finally { m.Dispose(); }
            }

            // P4: down on ✕, up on the ✎ disc; then a bare up on ✕ — the ✎ release consumed the
            // pending press (it belonged to ✕), so nothing is left to act on either up.
            {
                var (m, id, w, cues) = SavedSetup();
                try
                {
                    var close = w.ButtonsForTest()[$"{id}:close"];
                    var edit = w.ButtonsForTest()[$"{id}:edit"];
                    int cuesBefore = cues[0];
                    int fired = 0;
                    w.ControlInputActivated += (_, _, _) => fired++;
                    CoreMousePress(close);
                    CoreMouseRelease(edit);
                    Check.WithCustomMessage("P4: a core release on the WRONG control must not act")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    CoreMouseRelease(close);
                    Check.WithCustomMessage("P4: the bare core up after the wrong-control release must not act")
                        .That(m.ActiveNotes[0].State).IsEqualTo(NoteState.Display);
                    Check.That(fired).IsEqualTo(0);
                    Check.That(cues[0] - cuesBefore).IsEqualTo(0);
                }
                finally { m.Dispose(); }
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }
}
