namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// A5 — Pause in repeat and scroll mode, through the real App owners and the real
/// <see cref="InteractionManager"/>.
/// <para>
/// The binding contract (ticket A5, sticky notes' T6): Pause first runs the mode's existing stop
/// through its owner — <c>InteractionManager.ExitRepeatModeForPause</c> /
/// <c>ExitScrollModeForPause</c> — and only then flips the state to <see cref="InteractionState.Paused"/>,
/// so no synthesized input can follow the flip. The scroll test observes the worker's state from a
/// <see cref="InteractionManager.StateChanged"/> handler that is subscribed BEFORE the controller's
/// own: at <c>StateChanged(Paused)</c> the worker must already have been stopped. A pause-then-stop
/// swap sees it still active there.
/// </para>
/// </summary>
public class UniversalPauseTests
{
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void PauseInRepeatMode_ExitsRepeatThenPauses_AndClearsTheStopButton()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var input = new TestFakes.FakeInputSimulator();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var im = new InteractionManager();
        var bar = new TestFakes.FakeEdgeButtonBar();
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(edgeButtonBar: bar),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
        };
        using var controller = new RepeatModeController();
        controller.Initialize(ctx);

        im.EnterRepeatMode(InteractionAction.LeftClick);
        Pump(dispatcher);   // the controller's entry is posted from StateChanged
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.That(bar.ShownButtonIds).ContainsExactly("repeat-stop");

        var states = new List<InteractionState>();
        im.StateChanged += states.Add;

        im.TogglePause();

        // The existing repeat exit runs first (state leaves RepeatMode for FreeLook), then Paused.
        Check.That(states).ContainsExactly(InteractionState.FreeLook, InteractionState.Paused);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(im.RepeatAction).IsNull();
        // Repeat holds no OS state: the pause path synthesizes nothing.
        Check.That(input.ButtonEvents).IsEmpty();
        Check.That(input.BareClickCount).IsEqualTo(0);

        Pump(dispatcher);   // the controller's chrome teardown is posted from StateChanged
        Check.WithCustomMessage("the repeat stop button survived the pause.")
             .That(bar.ShownButtonIds).IsEmpty();

        // (d) Resume is unchanged.
        im.TogglePause();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [StaFact]
    public void PauseInScrollMode_StopsTheWorkerBeforeTheStateFlips()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var uiThread = Environment.CurrentManagedThreadId;
        var messenger = new WeakReferenceMessenger();
        var input = new TestFakes.FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        var im = new InteractionManager();

        // Subscribed BEFORE the controller so this runs first on StateChanged: it reads whether the
        // controller had ALREADY stopped the worker when the Paused notification was raised. With
        // the order swapped (pause, then stop) the answer here is "still active".
        bool? scrollActiveAtTheFlip = null;
        ScrollModeController? controller = null;
        im.StateChanged += s =>
        {
            if (s == InteractionState.Paused)
                scrollActiveAtTheFlip = controller?.IsActive;
        };

        var window = new ScrollModeControllerThreadingTests.RecordingScrollOverlay();
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(
                edgeButtonBar: new TestFakes.FakeEdgeButtonBar(),
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            CursorVM = new CursorCircleViewModel(settings, messenger),
        };
        controller = new ScrollModeController(ctx, () => window, cursorReader: () => null);
        using (controller)
        {
            // The real entry path: fixation → activation → action menu → Scroll.
            im.OnFixationStarted(new PhysicalPoint(400, 400));
            im.OnActivationButtonDwelled();
            im.OnActionSelected(InteractionAction.ScrollMode);
            Check.That(controller.IsActive).IsTrue();

            // Arm the joystick and prove wheels are being injected before the pause.
            Send(messenger, new PhysicalPoint(300, 450), 0, 150);   // the arming fixation
            Send(messenger, new PhysicalPoint(400, 400), 180, 180);  // gaze on the anchor
            var worker = controller.Worker;
            Check.That(worker).IsNotNull();
            for (int i = 0; i < 400
                && input.BareScrollPoints.Count + input.RestoreScrolls.Count == 0; i++)
                worker!.Tick();
            int injected = input.BareScrollPoints.Count + input.RestoreScrolls.Count;
            Check.WithCustomMessage("the premise: the armed joystick injected wheels")
                 .That(injected).IsStrictlyGreaterThan(0);

            var states = new List<InteractionState>();
            im.StateChanged += states.Add;

            im.TogglePause();

            Check.That(states).ContainsExactly(InteractionState.FreeLook, InteractionState.Paused);
            Check.That(im.CurrentState).IsEqualTo(InteractionState.Paused);
            Check.That(im.ScrollAnchor).IsNull();
            Check.WithCustomMessage("Paused arrived while scroll mode was still active.")
                 .That(scrollActiveAtTheFlip).IsEqualTo(false);
            Check.That(controller.IsActive).IsFalse();
            Check.That(controller.Worker).IsNull();

            // The worker is stopped, so a tick that raced the teardown is inert: no wheel may follow
            // the flip (this is what the sticky notes' NoteInputPolicy depends on).
            for (int i = 0; i < 100; i++)
                worker!.Tick();
            Check.That(input.BareScrollPoints.Count + input.RestoreScrolls.Count).IsEqualTo(injected);
            Check.That(window.Closed).IsTrue();
            Check.That(window.CloseThreadId).IsEqualTo(uiThread);

            // (d) Resume is unchanged.
            im.TogglePause();
            Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        }
    }

    private static void Send(IMessenger messenger, PhysicalPoint at, long fromMs, long toMs)
    {
        for (long t = fromMs; t <= toMs; t += 30)
            messenger.Send(new GazeCursorMovedMessage(at, t * 1000));
    }
}
