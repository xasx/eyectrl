namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Reflection;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The lost-gaze exit end to end: a real <see cref="GazeEngine"/> fed from a pool thread (the gaze
/// thread's stand-in), the real drag/scroll/repeat owners, and <see cref="LostGazeExitCoordinator"/>
/// routing the exit through their own cancel messages (architecture-check-2026-10, ticket 02). Each
/// test names the production mutation that turns it red.
/// </summary>
public class LostGazeExitCoordinatorTests
{
    private sealed class PushProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Push(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private sealed class World : IDisposable
    {
        public long NowMs = 1_000_000;
        public PushProvider Provider { get; } = new();
        public TestFakes.FakeInputSimulator Input { get; } = new();
        public TestFakes.FakeEdgeButtonBar Bar { get; }
        public GazeEngine Engine { get; }
        public InteractionManager Im => Engine.InteractionManager;
        public System.Windows.Threading.Dispatcher Dispatcher { get; } =
            System.Windows.Threading.Dispatcher.CurrentDispatcher;
        public DragCoordinator Drag { get; } = new();
        public RepeatModeController Repeat { get; } = new();
        public ScrollModeController Scroll { get; }
        public ScrollModeControllerThreadingTests.RecordingScrollOverlay ScrollWindow { get; } = new();
        public LostGazeExitCoordinator Exit { get; }
        private long _sampleUs;

        public World()
        {
            var (dragCtx, bar) = TestFakes.MakeDragContextWithBar(Input);
            Bar = bar;
            Engine = new GazeEngine(Provider, Input, dragCtx.Settings);
            var ctx = dragCtx with
            {
                OverlayManager = TestFakes.MakeOverlayManager(
                    bar, cursorCircle: new TestFakes.FakeCursorCircleOverlay()),
                InteractionManager = Engine.InteractionManager,
                GazeEngine = Engine,
                Dispatcher = Dispatcher,
                CursorVM = new CursorCircleViewModel(dragCtx.Settings, dragCtx.Messenger),
            };
            Drag.Initialize(ctx);
            Repeat.Initialize(ctx);
            Scroll = new ScrollModeController(ctx, () => ScrollWindow);
            Exit = new LostGazeExitCoordinator(() => NowMs);
            Exit.Initialize(ctx);
        }

        public void Pump()
        {
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public void EnterViaActionMenu(InteractionAction action)
        {
            Im.OnFixationStarted(new PhysicalPoint(300, 300));
            Im.OnActivationButtonDwelled();
            Im.OnActionSelected(action);
            Pump();
        }

        /// <summary>Samples through the real engine, on a pool thread like the gaze thread.</summary>
        public void FeedFromPoolThread(long durationUs, bool valid)
        {
            Task.Run(() =>
            {
                for (long end = _sampleUs + durationUs; _sampleUs < end; _sampleUs += 30_000)
                    Provider.Push(new GazePoint(300, 300, _sampleUs, valid));
            }).Wait();
        }

        public bool DragOverlayOpen =>
            typeof(DragCoordinator).GetField("_dragTrailOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Drag) is not null;

        public void Dispose()
        {
            Exit.Dispose();
            Scroll.Dispose();
            Repeat.Dispose();
            Drag.Dispose();
            Engine.Dispose();
        }
    }

    // ── Invalid samples (Core's LockedModeGazeLost) ──────────────────────────

    [StaFact]
    public void Drag_LostGazeOnTheGazeThread_CancelsTheDrag_ReleasingTheButtonOnce()
    {
        // Mutations: route the drag through CompleteDrag() only → MouseUp never sent; send the
        // cancel off the UI thread → DragTrailViewModel/DragCoordinator teardown runs off-thread.
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(w.Input.MouseDownCount).IsEqualTo(1);
        Check.That(w.DragOverlayOpen).IsTrue();

        w.FeedFromPoolThread(500_000, valid: true);
        w.FeedFromPoolThread(2_500_000, valid: false);
        w.Pump();

        Check.WithCustomMessage("The lost-gaze drag did not release the pressed mouse button.")
            .That(w.Input.MouseUpCount).IsEqualTo(1);
        Check.That(w.Input.ButtonsBalanced).IsTrue();
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(w.DragOverlayOpen).IsFalse();
    }

    [StaFact]
    public void Scroll_LostGazeOnTheGazeThread_StopsScrollingOnTheUiThread()
    {
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.ScrollMode);
        Check.That(w.Scroll.IsActive).IsTrue();
        var uiThread = Environment.CurrentManagedThreadId;

        w.FeedFromPoolThread(500_000, valid: true);
        w.FeedFromPoolThread(2_500_000, valid: false);
        w.Pump();

        Check.WithCustomMessage("Scroll mode survived lost gaze: it would keep scrolling at its last velocity.")
            .That(w.Scroll.IsActive).IsFalse();
        Check.That(w.ScrollWindow.CloseThreadId).IsEqualTo(uiThread);
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    // ── No frames at all ─────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(InteractionState.Dragging)]
    [InlineData(InteractionState.ScrollMode)]
    [InlineData(InteractionState.RepeatMode)]
    public void NoFrames_PastTheThreshold_EndsTheMode(InteractionState mode)
    {
        // A walk-away TIMED_OUT storm sends no samples at all. Mutations: drop RepeatMode from the
        // locked set, or compare against the wrong clock → the mode survives.
        using var w = new World();
        if (mode == InteractionState.RepeatMode)
        {
            w.Im.EnterRepeatMode(InteractionAction.LeftClick);
            w.Pump();
        }
        else
        {
            w.EnterViaActionMenu(mode == InteractionState.Dragging ? InteractionAction.Drag : InteractionAction.ScrollMode);
        }
        Check.That(w.Im.CurrentState).IsEqualTo(mode);

        w.NowMs += LostGazeExitCoordinator.NoFramesThresholdMs;      // exactly at: not yet
        w.Exit.CheckNoFrames();
        w.Pump();
        Check.That(w.Im.CurrentState).IsEqualTo(mode);

        w.NowMs += 1;                                                 // past it
        w.Exit.CheckNoFrames();
        w.Pump();
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        if (mode == InteractionState.Dragging)
            Check.That(w.Input.MouseUpCount).IsEqualTo(1);
    }

    [StaFact]
    public void NoFrames_ASampleOfAnyValidityKeepsTheModeAlive()
    {
        // The no-frames check is about the STREAM, not the eyes: invalid samples are Core's signal.
        // Mutation: stamp only valid samples → an invalid-sample stretch reads as "no frames".
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);

        w.NowMs += 1_500;
        w.FeedFromPoolThread(30_000, valid: false);                   // one invalid sample, now
        w.NowMs += 1_500;                                             // 3 s since entry, 1.5 s since it
        w.Exit.CheckNoFrames();
        w.Pump();

        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(w.Input.MouseUpCount).IsEqualTo(0);
    }

    [StaFact]
    public void NoFramesTimer_RunsOnlyWhileALockedModeIs()
    {
        // Mutation: never start the timer → the no-frames exit cannot happen in production.
        using var w = new World();
        Check.That(w.Exit.IsWatchingForNoFrames).IsFalse();

        w.EnterViaActionMenu(InteractionAction.Drag);
        Check.That(w.Exit.IsWatchingForNoFrames).IsTrue();

        w.Drag.OnDragCancel();                                        // the user's own Cancel
        w.Pump();
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(w.Exit.IsWatchingForNoFrames).IsFalse();
    }

    // ── Re-check on the UI thread ────────────────────────────────────────────

    [StaFact]
    public void AnExitQueuedForAModeThatEndsMeanwhile_DoesNothing()
    {
        // The signal is raised, then the user drops the drag before the dispatched exit runs.
        // Mutation: drop the in-dispatch state re-check → a stray DragCancel hides the shared edge
        // bar a second time (it may by then belong to another context, e.g. the keyboard's Close).
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);

        w.NowMs += LostGazeExitCoordinator.NoFramesThresholdMs + 1;
        w.Exit.CheckNoFrames();                                       // queues the exit

        w.Drag.OnDragDrop(new DragDropMessage(new PhysicalPoint(500, 500)));   // ends the drag first
        var hidesAfterDrop = w.Bar.Calls.Count(c => c == "Hide");

        w.Pump();

        Check.WithCustomMessage("A stale lost-gaze exit cancelled a drag that had already ended.")
            .That(w.Bar.Calls.Count(c => c == "Hide")).IsEqualTo(hidesAfterDrop);
        Check.That(w.Input.MouseUpCount).IsEqualTo(1);
    }
}
