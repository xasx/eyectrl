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

    internal sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) { lock (Events) Events.Add(logEvent); }

        public List<(Serilog.Events.LogEventLevel Level, string? Mode, string? Signal)> Exits()
        {
            lock (Events)
                return Events
                    .Where(e => e.MessageTemplate.Text.StartsWith("Lost-gaze exit", StringComparison.Ordinal))
                    .Select(e => (e.Level, Prop(e, "Mode"), Prop(e, "Signal")))
                    .ToList();
        }

        private static string? Prop(Serilog.Events.LogEvent e, string name) =>
            e.Properties.TryGetValue(name, out var v) && v is Serilog.Events.ScalarValue { Value: { } o }
                ? o.ToString() : null;
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
        public CommunityToolkit.Mvvm.Messaging.IMessenger Messenger { get; }
        public CapturingSink Log { get; } = new();
        public int UiThreadId { get; } = Environment.CurrentManagedThreadId;
        private long _sampleUs;

        public World()
        {
            var (dragCtx, bar) = TestFakes.MakeDragContextWithBar(Input);
            Bar = bar;
            Messenger = dragCtx.Messenger;
            Engine = new GazeEngine(Provider, Input, dragCtx.Settings);
            var ctx = dragCtx with
            {
                OverlayManager = TestFakes.MakeOverlayManager(
                    bar,
                    cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                    // The drag's OnDragStarted completes the backdrop's pending hide before the
                    // press (the deferred-hide fix); the manager must carry one or the property
                    // throws by design.
                    glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
                InteractionManager = Engine.InteractionManager,
                GazeEngine = Engine,
                Dispatcher = Dispatcher,
                CursorVM = new CursorCircleViewModel(dragCtx.Settings, dragCtx.Messenger),
            };
            Drag.Initialize(ctx);
            Repeat.Initialize(ctx);
            Scroll = new ScrollModeController(ctx, () => ScrollWindow);
            Exit = new LostGazeExitCoordinator(() => NowMs,
                new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(Log).CreateLogger());
            Exit.Initialize(ctx);
        }

        /// <summary>Records the thread each <typeparamref name="T"/> is sent on. Keep the returned
        /// recipient alive for the test (the messenger holds it weakly).</summary>
        public (object Recipient, List<int> Threads) RecordSends<T>() where T : class
        {
            var threads = new List<int>();
            var recipient = new object();
            CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<object, T>(
                Messenger, recipient, (_, _) => threads.Add(Environment.CurrentManagedThreadId));
            return (recipient, threads);
        }

        /// <summary>Lets the dispatcher run — the real DispatcherTimer included — until
        /// <paramref name="done"/> holds or <paramref name="timeout"/> of wall time passes.</summary>
        public void RunDispatcherUntil(Func<bool> done, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!done() && DateTime.UtcNow < deadline)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var wake = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(50), System.Windows.Threading.DispatcherPriority.Background,
                    (_, _) => frame.Continue = false, Dispatcher);
                wake.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                wake.Stop();
            }
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
        // cancel on the signalling (gaze) thread → DragTrailViewModel, which does not marshal,
        // tears its dwell down off the UI thread (review F4: pinned by the thread recorder); drop the
        // exit's Information line → false cancels cannot be found in the log (review F5).
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(w.Input.MouseDownCount).IsEqualTo(1);
        Check.That(w.DragOverlayOpen).IsTrue();
        var (recipient, sendThreads) = w.RecordSends<DragCancelMessage>();

        w.FeedFromPoolThread(500_000, valid: true);
        w.FeedFromPoolThread(2_500_000, valid: false);
        w.Pump();

        Check.WithCustomMessage("The lost-gaze drag did not release the pressed mouse button.")
            .That(w.Input.MouseUpCount).IsEqualTo(1);
        Check.That(w.Input.ButtonsBalanced).IsTrue();
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(w.DragOverlayOpen).IsFalse();
        Check.WithCustomMessage("DragCancelMessage was not sent exactly once, on the UI thread.")
            .That(sendThreads).ContainsExactly(w.UiThreadId);
        Check.That(w.Log.Exits()).ContainsExactly(
            (Serilog.Events.LogEventLevel.Information, (string?)"Dragging", (string?)"gaze samples invalid"));
        GC.KeepAlive(recipient);
    }

    [StaFact]
    public void Scroll_LostGazeOnTheGazeThread_StopsScrollingOnTheUiThread()
    {
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.ScrollMode);
        Check.That(w.Scroll.IsActive).IsTrue();
        var (recipient, sendThreads) = w.RecordSends<ExitScrollModeMessage>();

        w.FeedFromPoolThread(500_000, valid: true);
        w.FeedFromPoolThread(2_500_000, valid: false);
        w.Pump();

        Check.WithCustomMessage("Scroll mode survived lost gaze: it would keep scrolling at its last velocity.")
            .That(w.Scroll.IsActive).IsFalse();
        Check.That(w.ScrollWindow.CloseThreadId).IsEqualTo(w.UiThreadId);
        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.WithCustomMessage("ExitScrollModeMessage was not sent exactly once, on the UI thread.")
            .That(sendThreads).ContainsExactly(w.UiThreadId);
        GC.KeepAlive(recipient);
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

    [StaFact]
    public void NoFrames_TheRealTimerEndsTheDrag()
    {
        // Review F1: every other no-frames test calls CheckNoFrames() itself. This one lets the
        // production DispatcherTimer tick (the clock is fake, only the 250 ms cadence is real).
        // Mutation: drop the timer's Tick → CheckNoFrames wiring → the walk-away TIMED_OUT case
        // leaves the button pressed, and only this test sees it.
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);
        Check.That(w.Exit.IsWatchingForNoFrames).IsTrue();

        w.NowMs += LostGazeExitCoordinator.NoFramesThresholdMs + 1;
        w.RunDispatcherUntil(() => w.Im.CurrentState == InteractionState.FreeLook, TimeSpan.FromSeconds(3));

        Check.WithCustomMessage("The no-frames timer never ended the drag.")
            .That(w.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(w.Input.MouseUpCount).IsEqualTo(1);
        Check.That(w.Log.Exits()).ContainsExactly(
            (Serilog.Events.LogEventLevel.Information, (string?)"Dragging", (string?)"no gaze frames"));
    }

    [StaFact]
    public void NoFrames_AnExitThatDoesNotLand_IsRetried_LoggingInformationOnce()
    {
        // Review F6: while the silence lasts and the mode is still there, every tick retries; the
        // first attempt logs at Information, retries at Debug. Simulated failed exit: the repeat
        // owner no longer handles ExitRepeatModeMessage. Mutations: re-introduce the
        // once-per-silence guard on the exit → one attempt only; log every attempt at Information.
        using var w = new World();
        w.Im.EnterRepeatMode(InteractionAction.LeftClick);
        w.Pump();
        w.Messenger.UnregisterAll(w.Repeat);                           // the exit will not land
        // Re-check 1 N2: this test ticks by hand; a live 250 ms tick would add stray retries and
        // make the exact counts below flaky on a slow box. Stop the real timer (pinned elsewhere).
        ((System.Windows.Threading.DispatcherTimer?)typeof(LostGazeExitCoordinator)
            .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(w.Exit))?.Stop();
        var (recipient, sends) = w.RecordSends<ExitRepeatModeMessage>();

        w.NowMs += LostGazeExitCoordinator.NoFramesThresholdMs + 1;
        for (int tick = 0; tick < 3; tick++)
        {
            w.Exit.CheckNoFrames();
            w.Pump();
            w.NowMs += 250;
        }

        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.WithCustomMessage("A failed no-frames exit was not retried on the next ticks.")
            .That(sends.Count).IsEqualTo(3);
        Check.That(w.Log.Exits().Select(e => e.Level)).ContainsExactly(
            Serilog.Events.LogEventLevel.Information,
            Serilog.Events.LogEventLevel.Debug,
            Serilog.Events.LogEventLevel.Debug);
        GC.KeepAlive(recipient);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [StaFact]
    public void Dispose_DropsAQueuedTimerStart()
    {
        // Review F3: Context outlives Dispose, so a StateChanged queued before it would restart the
        // timer afterwards. Mutation: drop the _disposed check in OnStateChanged → it restarts.
        using var w = new World();
        w.Im.OnFixationStarted(new PhysicalPoint(300, 300));
        w.Im.OnActivationButtonDwelled();
        w.Im.OnActionSelected(InteractionAction.Drag);                 // queues the timer start
        w.Exit.Dispose();
        w.Pump();

        Check.That(w.Exit.IsWatchingForNoFrames).IsFalse();
    }

    [StaFact]
    public void AfterDispose_NothingEndsTheMode()
    {
        // Review F3: neither a check after Dispose nor an exit queued before it may act. Both go
        // through RequestExit's dispatched action, which checks _disposed when it runs. Mutation:
        // drop that check → the drag is cancelled by a disposed coordinator.
        using var w = new World();
        w.EnterViaActionMenu(InteractionAction.Drag);
        w.NowMs += LostGazeExitCoordinator.NoFramesThresholdMs + 1;

        w.Exit.CheckNoFrames();                                       // queued before Dispose
        w.Exit.Dispose();
        w.Exit.CheckNoFrames();                                       // after Dispose
        w.Pump();

        Check.That(w.Im.CurrentState).IsEqualTo(InteractionState.Dragging);
        Check.That(w.Input.MouseUpCount).IsEqualTo(0);
    }

    // ── Re-check on the UI thread ────────────────────────────────────────────

    [StaFact]
    public void AnExitQueuedForAModeThatEndsMeanwhile_DoesNothing()
    {
        // The signal is raised, then the user drops the drag before the dispatched exit runs.
        // Mutation: drop the in-dispatch state re-check → a stray DragCancel hides the shared edge
        // bar a second time (it may by then belong to another context, e.g. the keyboard's Close).
        // This closes the window before OUR hop only: DragCoordinator hops once more on receipt,
        // and a drop between the two hops is not covered — the same as the ESC hatch today.
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
