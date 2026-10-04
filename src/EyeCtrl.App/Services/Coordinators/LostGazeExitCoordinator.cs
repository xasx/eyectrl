namespace EyeCtrl.App.Services.Coordinators;

using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Events;

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
/// or RepeatMode past the same 2 s. Measured from the last sample of ANY validity, so the worst case
/// after the last valid sample is just under 4 s: up to 2 s of invalid samples (below Core's
/// threshold) and then 2 s of silence.</item>
/// </list>
/// </para>
/// <para>
/// The exit goes through each mode's own marshaled cancel message, never a state-only write: only
/// <see cref="DragCoordinator"/> can release the pressed button, and only
/// <see cref="ScrollModeController"/> can stop the scroll worker. Same template as the ESC hatch
/// (<c>DragCoordinator.OnEscapePressed</c>): marshal to the UI thread, re-check the state THERE —
/// the mode may have ended in between, and a drag cancel sends an unconditional MouseUp — then send.
/// The owner's own handler hops once more (as with ESC), so a drop landing between the two hops
/// can still be followed by a cancel; the button is safe (released exactly once), only the shared
/// edge bar may be hidden a second time.
/// </para>
/// <para>
/// A lost-gaze cancel is accepted as fail-safe (a long eyes-closed rest mid-drag cancels the drag).
/// Each exit logs one Information line so false cancels can be found; while the silence lasts and
/// the mode has not ended, the no-frames exit retries on every tick, logging retries at Debug.
/// </para>
/// </summary>
internal sealed class LostGazeExitCoordinator : CoordinatorBase
{
    internal const long NoFramesThresholdMs = InteractionManager.LostGazeExitThresholdUs / 1000;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<long> _nowMs;
    private readonly ILogger? _log;
    // Monotonic receipt time of the last raw sample. Written on the gaze thread, read on the UI thread.
    private long _lastSampleMs;
    // The _lastSampleMs value of the silence the no-frames exit last logged at Information: a retry
    // within the same silence logs at Debug. UI thread.
    private long _noFramesLoggedForSampleMs = long.MinValue;
    private DispatcherTimer? _timer;
    private Action<GazePoint>? _sampleHandler;
    // CoordinatorBase keeps Context after Dispose, so queued work checks this instead.
    private volatile bool _disposed;

    public LostGazeExitCoordinator() : this(() => Environment.TickCount64) { }

    /// <summary>Test seam: a monotonic clock in milliseconds, and the logger (default: the static
    /// Serilog logger, resolved at the moment of writing).</summary>
    internal LostGazeExitCoordinator(Func<long> nowMs, ILogger? log = null)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _log = log;
    }

    private ILogger Logger => _log ?? Log.Logger;

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

    /// <summary>Gaze thread: the invalid-samples signal (Core raises it once per episode).</summary>
    private void OnLockedModeGazeLost(InteractionState state) =>
        RequestExit(state, "gaze samples invalid", LogEventLevel.Information);

    /// <summary>Any thread: run the no-frames timer exactly while a locked mode is active.</summary>
    private void OnStateChanged(InteractionState _)
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
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
    /// UI thread (the timer tick; also called directly by tests): ends the locked mode when no raw
    /// sample of any validity has arrived for more than <see cref="NoFramesThresholdMs"/>. Retries on
    /// every tick until the mode ends or a sample arrives; the first attempt of a silence logs at
    /// Information, later ones at Debug.
    /// </summary>
    internal void CheckNoFrames()
    {
        var context = Context;
        // No _disposed check here: the exit it queues checks it when it runs, which also covers a
        // check queued before Dispose (one guard, so a mutation of it is visible).
        if (context is null) return;
        var state = context.InteractionManager.CurrentState;
        if (!IsLockedMode(state)) return;

        var lastSample = Volatile.Read(ref _lastSampleMs);
        if (_nowMs() - lastSample <= NoFramesThresholdMs) return;

        var firstForThisSilence = lastSample != _noFramesLoggedForSampleMs;
        _noFramesLoggedForSampleMs = lastSample;
        RequestExit(state, "no gaze frames",
            firstForThisSilence ? LogEventLevel.Information : LogEventLevel.Debug);
    }

    private void RequestExit(InteractionState observed, string signal, LogEventLevel level)
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Re-check on the UI thread: the mode may have ended (drop, Stop, ESC) since the signal.
            if (context.InteractionManager.CurrentState != observed) return;
            Logger.Write(level, "Lost-gaze exit: ending {Mode} ({Signal} for over {ThresholdMs} ms)",
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
        _disposed = true;
        _timer?.Stop();
        var context = Context;
        if (context is null) return;
        if (_sampleHandler is not null) context.GazeEngine.GazeSampled -= _sampleHandler;
        context.InteractionManager.LockedModeGazeLost -= OnLockedModeGazeLost;
        context.InteractionManager.StateChanged -= OnStateChanged;
    }
}
