namespace EyeCtrl.App.Tests.Services;

using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// <see cref="InteractionManager.StateChanged"/> fires on whichever thread wrote the state; a
/// lost-gaze exit writes it on the gaze thread. Scroll mode's Enter/Exit create and close WPF
/// windows and a DispatcherTimer, so the controller must marshal. Before the fix an exit raised
/// off the UI thread stopped the worker's timer on the wrong thread — the throw was swallowed by
/// the gaze pipeline, and the scroll window and worker stayed alive (architecture-check-2026-10,
/// ticket 02).
/// </summary>
public class ScrollModeControllerThreadingTests
{
    /// <summary>Records where the controller drove the overlay window from.</summary>
    internal sealed class RecordingScrollOverlay : IScrollOverlaySurface
    {
        public int? ShowThreadId { get; private set; }
        public int ShowCount { get; private set; }
        public int? CloseThreadId { get; private set; }
        public bool Closed => CloseThreadId is not null;
        public void Show(ScrollOverlayViewModel vm)
        {
            ShowThreadId = Environment.CurrentManagedThreadId;
            ShowCount++;
        }
        public void UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude) { }
        public void Close() => CloseThreadId = Environment.CurrentManagedThreadId;
    }

    internal static (ScrollModeController Controller, InteractionManager Im, RecordingScrollOverlay Window,
        System.Windows.Threading.Dispatcher Dispatcher) NewScrollWorld(
        TestFakes.FakeInputSimulator input, Func<PhysicalPoint?>? cursorReader = null,
        IGlassBackdropOverlay? glassBackdrop = null)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var im = new InteractionManager();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(
                new TestFakes.FakeEdgeButtonBar(),
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                // F-S1b: the scroll entry now completes the backdrop's pending hide, so the fake
                // must be present — its getter throws when a fake was not supplied.
                glassBackdrop: glassBackdrop ?? new TestFakes.FakeGlassBackdropOverlay()),
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            CursorVM = new CursorCircleViewModel(settings, messenger),
        };
        var window = new RecordingScrollOverlay();
        var controller = new ScrollModeController(ctx, () => window, cursorReader);
        return (controller, im, window, dispatcher);
    }

    /// <summary>Drives the real entry path: fixation → activation → action menu → Scroll.</summary>
    internal static void EnterScrollMode(InteractionManager im)
    {
        im.OnFixationStarted(new PhysicalPoint(400, 400));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.ScrollMode);
    }

    internal static void Pump(System.Windows.Threading.Dispatcher d) =>
        d.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    [StaFact]
    public void EntryOnTheUiThread_StaysSynchronous()
    {
        // The dispatcher path must not change: the action menu's Scroll enters the mode inline.
        var (controller, im, window, _) = NewScrollWorld(new TestFakes.FakeInputSimulator());
        using (controller)
        {
            EnterScrollMode(im);

            Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
            Check.That(controller.IsActive).IsTrue();   // no pump needed
            Check.That(window.ShowThreadId).IsEqualTo(Environment.CurrentManagedThreadId);
        }
    }

    [StaFact]
    public void ExitRaisedOffTheUiThread_TearsDownOnTheUiThread()
    {
        // Mutation: drop the marshal in OnInteractionStateChanged → the pool-thread exit stops the
        // worker's DispatcherTimer off its thread and throws, and the window is closed off-thread.
        var (controller, im, window, dispatcher) = NewScrollWorld(new TestFakes.FakeInputSimulator());
        using (controller)
        {
            EnterScrollMode(im);
            var uiThread = Environment.CurrentManagedThreadId;

            Exception? offThread = null;
            Task.Run(() =>
            {
                try { im.ExitScrollMode(); }
                catch (Exception ex) { offThread = ex; }
            }).Wait();

            Check.WithCustomMessage("The exit ran scroll teardown on the pool thread and threw.")
                .That(offThread).IsNull();
            Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);

            Pump(dispatcher);

            Check.That(controller.IsActive).IsFalse();
            Check.That(window.Closed).IsTrue();
            Check.WithCustomMessage("The scroll window was closed off the UI thread.")
                .That(window.CloseThreadId).IsEqualTo(uiThread);
        }
    }

    [StaFact]
    public void AStaleGazeThreadEdge_QueuedBeforeAnInlineEntry_DoesNotTearScrollDown()
    {
        // Ticket 02 review F2, the reviewer's probe: a gaze-thread Fixation edge queues
        // ApplyState for later; the action menu then enters scroll mode inline on the UI thread.
        // When the queued edge runs it must decide from the CURRENT state. Mutation: decide from the
        // event payload → it exits a live scroll mode, leaving Core in ScrollMode with no window and
        // no Stop button.
        var (controller, im, window, dispatcher) = NewScrollWorld(new TestFakes.FakeInputSimulator());
        using (controller)
        {
            Task.Run(() => im.OnFixationStarted(new PhysicalPoint(400, 400))).Wait();   // queued edge
            im.OnActivationButtonDwelled();
            im.OnActionSelected(InteractionAction.ScrollMode);                          // inline entry
            Check.That(controller.IsActive).IsTrue();

            Pump(dispatcher);

            Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);
            Check.WithCustomMessage("A stale queued edge tore down a live scroll mode.")
                .That(controller.IsActive).IsTrue();
            Check.That(window.Closed).IsFalse();
            // Re-check 1 N1: the stale edge now reaches EnterScrollMode, so its "already active"
            // guard is load-bearing. Mutation: drop it → a second worker and window replace the
            // first, which keeps scrolling after exit and never closes.
            Check.WithCustomMessage("A stale queued edge entered scroll mode a second time.")
                .That(window.ShowCount).IsEqualTo(1);
        }
    }

    [StaFact]
    public void AStaleEntryAfterTheModeEnded_EntersNothing()
    {
        // A marshaled ScrollMode entry can land after the mode already ended; the anchor is gone by
        // then. Mutation: drop the CurrentState/anchor guard in EnterScrollMode → it dereferences a
        // null anchor and throws on the dispatcher.
        var (controller, im, window, dispatcher) = NewScrollWorld(new TestFakes.FakeInputSimulator());
        using (controller)
        {
            // Raise the entry off the UI thread, and end the mode before the dispatcher runs it.
            Task.Run(() => EnterScrollMode(im)).Wait();
            Task.Run(() => im.ExitScrollMode()).Wait();

            Check.ThatCode(() => Pump(dispatcher)).DoesNotThrow();
            Check.That(controller.IsActive).IsFalse();
            Check.That(window.ShowThreadId).IsNull();
        }
    }
}
