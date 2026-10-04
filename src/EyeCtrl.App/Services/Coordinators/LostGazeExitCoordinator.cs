namespace EyeCtrl.App.Services.Coordinators;

using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Ends a locked mode — drag, scroll, repeat — when the user's gaze is gone, because then nobody
/// can dwell its cancel/stop button and Pause is refused in those modes. Without it a lost-gaze
/// drag kept the OS mouse button pressed and a lost-gaze scroll kept scrolling at its last velocity
/// until a second person pressed ESC (architecture-check-2026-10, ticket 02).
/// <para>
/// Two signals, one exit:
/// <list type="bullet">
/// <item><b>Invalid samples</b> — <see cref="InteractionManager.LockedModeGazeLost"/>, raised on the
/// gaze thread after 2 s of invalid raw gaze in Dragging or ScrollMode (repeat exits itself).</item>
/// <item><b>No frames at all</b> — a walk-away Tobii <c>TIMED_OUT</c> storm delivers no samples, so no
/// sample-driven check can run. A UI-thread timer, alive only while a locked mode is, compares a
/// monotonic local clock against the last raw sample of any validity and ends Dragging, ScrollMode
/// or RepeatMode past the same 2 s.</item>
/// </list>
/// </para>
/// <para>
/// The exit goes through each mode's own marshaled cancel message, never a state-only write: only
/// <see cref="DragCoordinator"/> can release the pressed button, and only
/// <see cref="ScrollModeController"/> can stop the scroll worker. Same template as the ESC hatch
/// (<c>DragCoordinator.OnEscapePressed</c>): marshal to the UI thread, re-check the state THERE —
/// the mode may have ended in between, and a drag cancel sends an unconditional MouseUp — then send.
/// A lost-gaze cancel is accepted as fail-safe (a long eyes-closed rest mid-drag cancels the drag);
/// each exit logs one Information line so false cancels can be found.
/// </para>
/// </summary>
internal sealed class LostGazeExitCoordinator : CoordinatorBase
{
    internal const long NoFramesThresholdMs = InteractionManager.LostGazeExitThresholdUs / 1000;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<long> _nowMs;
    // Monotonic receipt time of the last raw sample. Written on the gaze thread, read on the UI thread.
    private long _lastSampleMs;
    // The _lastSampleMs value the no-frames exit last fired for: one exit per silent stretch. UI thread.
    private long _noFramesHandledForSampleMs = long.MinValue;
    private DispatcherTimer? _timer;
    private Action<GazePoint>? _sampleHandler;

    public LostGazeExitCoordinator() : this(() => Environment.TickCount64) { }

    /// <summary>Test seam: a monotonic clock in milliseconds.</summary>
    internal LostGazeExitCoordinator(Func<long> nowMs) =>
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));

    /// <summary>Whether the no-frames timer is running — only while a locked mode is.</summary>
    internal bool IsWatchingForNoFrames => _timer?.IsEnabled == true;

    protected override void OnInitialize()
    {
        var context = Context!;
        Volatile.Write(ref _lastSampleMs, _nowMs());
        _sampleHandler = _ => Volatile.Write(ref _lastSampleMs, _nowMs());
        context.GazeEngine.GazeSampled += _sampleHandler;
        context.InteractionManager.LockedModeGazeLost += OnLockedModeGazeLost;
        context.InteractionManager.StateChanged += OnStateChanged;
    }

    private static bool IsLockedMode(InteractionState state) =>
        state is InteractionState.Dragging or InteractionState.ScrollMode or InteractionState.RepeatMode;

    /// <summary>Gaze thread: the invalid-samples signal.</summary>
    private void OnLockedModeGazeLost(InteractionState state) =>
        RequestExit(state, "gaze samples invalid");

    /// <summary>Any thread: run the no-frames timer exactly while a locked mode is active.</summary>
    private void OnStateChanged(InteractionState _)
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (Context is null) return;   // disposed while queued
            if (IsLockedMode(context.InteractionManager.CurrentState))
            {
                _timer ??= CreateTimer(context.Dispatcher);
                _timer.Start();
            }
            else
            {
                _timer?.Stop();
            }
        });
    }

    private DispatcherTimer CreateTimer(Dispatcher dispatcher)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = CheckInterval };
        timer.Tick += (_, _) => CheckNoFrames();
        return timer;
    }

    /// <summary>
    /// UI thread (the timer tick; called directly by tests): ends the locked mode when no raw sample
    /// of any validity has arrived for <see cref="NoFramesThresholdMs"/>. Fires once per silent
    /// stretch — the next sample re-arms it.
    /// </summary>
    internal void CheckNoFrames()
    {
        var context = Context;
        if (context is null) return;
        var state = context.InteractionManager.CurrentState;
        if (!IsLockedMode(state)) return;

        var lastSample = Volatile.Read(ref _lastSampleMs);
        if (_nowMs() - lastSample <= NoFramesThresholdMs) return;
        if (lastSample == _noFramesHandledForSampleMs) return;
        _noFramesHandledForSampleMs = lastSample;
        RequestExit(state, "no gaze frames");
    }

    private void RequestExit(InteractionState observed, string signal)
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            // Re-check on the UI thread: the mode may have ended (drop, Stop, ESC) since the signal.
            if (context.InteractionManager.CurrentState != observed) return;
            Log.Information("Lost-gaze exit: ending {Mode} ({Signal} for over {ThresholdMs} ms)",
                observed, signal, NoFramesThresholdMs);
            switch (observed)
            {
                case InteractionState.Dragging:
                    context.Messenger.Send(new DragCancelMessage());
                    break;
                case InteractionState.ScrollMode:
                    context.Messenger.Send(new ExitScrollModeMessage());
                    break;
                case InteractionState.RepeatMode:
                    context.Messenger.Send(new ExitRepeatModeMessage());
                    break;
            }
        });
    }

    protected override void OnDispose()
    {
        _timer?.Stop();
        var context = Context;
        if (context is null) return;
        if (_sampleHandler is not null) context.GazeEngine.GazeSampled -= _sampleHandler;
        context.InteractionManager.LockedModeGazeLost -= OnLockedModeGazeLost;
        context.InteractionManager.StateChanged -= OnStateChanged;
    }
}
