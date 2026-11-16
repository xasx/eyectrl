namespace EyeCtrl.Platform.Services;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs window-management actions off the calling thread, one at a time.
///
/// <para>
/// The caller is the WPF UI dispatcher, which is also the gaze-dwell thread: every gaze sample and
/// every overlay dwell is delivered there. A synchronous cross-process window call
/// (<c>ShowWindow</c>, <c>SetForegroundWindow</c>) waits for the target's thread, so one hung
/// application would freeze every EyeCtrl surface — Pause included — and take the owner's control
/// away. Every action therefore runs on its own background thread and the entry point returns at
/// once.
/// </para>
///
/// <para>
/// Actions are serialised through a hand-off chain (each one waits for its predecessor) so two
/// picks never interleave their ALT foreground grants. The wait is bounded: an action that is
/// still waiting for its predecessor after <see cref="DefaultHandoffTimeout"/> treats it as stuck
/// on a target that is not answering, cancels it (so a late completion cannot inject a stale ALT
/// grant) and runs itself instead. A request that still has not started
/// <see cref="DefaultStartDeadline"/> after it was made is dropped and logged rather than executed
/// late. A running action that passes <see cref="DefaultCompletionBudget"/> drops its remaining
/// steps before injecting input (see <see cref="WindowActionContext"/>): once the target has
/// answered the restore, the ALT pair and the foreground call may still complete within 3 s of the
/// request — a long-minimised app can take over a second to restore and must still switch — but no
/// input may be injected after that. Exceptions are contained and logged: the action thread must
/// never take the process down.
/// </para>
/// </summary>
internal sealed class WindowActionScheduler
{
    /// <summary>
    /// How long an action waits for the previous one before treating it as stuck. A responsive
    /// window action finishes in single-digit milliseconds; half a second is already far outside
    /// that, and short enough that the owner's next action still feels immediate.
    /// </summary>
    internal static readonly TimeSpan DefaultHandoffTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The longest a queued action may take to start. Past it the action is dropped and logged: a
    /// pick must never start seconds after the dwell that asked for it.
    /// </summary>
    internal static readonly TimeSpan DefaultStartDeadline = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a started action may keep working before its remaining steps count as stale. The
    /// allowance starts at the request, so it includes any hand-off wait and the restore itself; a
    /// target that answers within it still gets its ALT grant and switch.
    /// </summary>
    internal static readonly TimeSpan DefaultCompletionBudget = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly TimeSpan _handoffTimeout;
    private readonly TimeSpan _startDeadline;
    private readonly TimeSpan _completionBudget;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ChainLink? _tail;

    /// <summary>Creates the scheduler. The bounds are injectable so tests stay fast.</summary>
    /// <param name="logger">Records drops, abandonments and failures.</param>
    /// <param name="handoffTimeout">Overrides <see cref="DefaultHandoffTimeout"/>.</param>
    /// <param name="startDeadline">Overrides <see cref="DefaultStartDeadline"/>.</param>
    /// <param name="completionBudget">Overrides <see cref="DefaultCompletionBudget"/>.</param>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    internal WindowActionScheduler(
        ILogger logger,
        TimeSpan? handoffTimeout = null,
        TimeSpan? startDeadline = null,
        TimeSpan? completionBudget = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _handoffTimeout = handoffTimeout ?? DefaultHandoffTimeout;
        _startDeadline = startDeadline ?? DefaultStartDeadline;
        _completionBudget = completionBudget ?? DefaultCompletionBudget;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Queues one action and returns immediately. This never waits for the previous action, for the
    /// target, or for the action itself.
    /// </summary>
    /// <param name="action">A short action name for the diagnostics.</param>
    /// <param name="hwnd">The target window, for the diagnostics.</param>
    /// <param name="body">
    /// The work. The context reports whether the action has been abandoned by a later one (its
    /// token is cancelled) or has run past its start allowance; a body that injects input must stop
    /// at either point.
    /// </param>
    internal void Enqueue(string action, IntPtr hwnd, Action<WindowActionContext> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var link = new ChainLink(action, hwnd, _time.GetTimestamp());
        ChainLink? previous;
        lock (_gate)
        {
            previous = _tail;
            _tail = link;
        }

        var worker = new Thread(() => Run(previous, link, body))
        {
            IsBackground = true,
            Name = $"EyeCtrl window action ({action})",
        };
        worker.Start();
    }

    private void Run(ChainLink? previous, ChainLink link, Action<WindowActionContext> body)
    {
        try
        {
            if (previous is not null && !previous.Finished.Wait(_handoffTimeout))
            {
                // The predecessor is still inside a cross-process call after the hand-off bound:
                // its target is not answering. Abandon it so it cannot inject its ALT grant when
                // the target finally answers, and run now instead of queueing behind a hung window
                // forever.
                previous.Abandon(_logger, _time);
            }

            var waited = _time.GetElapsedTime(link.RequestedAt);
            if (waited > _startDeadline)
            {
                _logger.LogWarning(
                    "Window action {Action} for window {Hwnd} was dropped: it could not start within "
                    + "{DeadlineMs:0} ms of the request (waited {WaitedMs:0} ms), so it was not "
                    + "executed late.",
                    link.Action, link.Hwnd, _startDeadline.TotalMilliseconds, waited.TotalMilliseconds);
                return;
            }

            body(new WindowActionContext(link.Token, _time, link.RequestedAt, _completionBudget));
        }
        catch (Exception ex)
        {
            // The body runs on a background thread now, so the caller's try/catch no longer sees
            // its exceptions: contain them here or one bad action deregisters the process.
            _logger.LogError(ex, "Window action {Action} for window {Hwnd} failed", link.Action, link.Hwnd);
        }
        finally
        {
            link.Finish();
            lock (_gate)
            {
                if (ReferenceEquals(_tail, link))
                    _tail = null;
            }
        }
    }

    /// <summary>One queued action and the completion signal its successor waits on.</summary>
    private sealed class ChainLink
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _abandoned;

        internal ChainLink(string action, IntPtr hwnd, long requestedAt)
        {
            Action = action;
            Hwnd = hwnd;
            RequestedAt = requestedAt;
        }

        internal string Action { get; }

        internal IntPtr Hwnd { get; }

        internal long RequestedAt { get; }

        internal ManualResetEventSlim Finished { get; } = new(false);

        internal CancellationToken Token => _cancellation.Token;

        /// <summary>
        /// Cancels a stuck action and logs it once. Idempotent: only the first successor to give up
        /// on it owns the log line.
        /// </summary>
        internal void Abandon(ILogger logger, TimeProvider time)
        {
            if (Finished.IsSet || Interlocked.Exchange(ref _abandoned, 1) != 0)
                return;

            _cancellation.Cancel();

            logger.LogWarning(
                "Window action {Action} for window {Hwnd} was abandoned after {ElapsedMs:0} ms: the "
                + "target stopped answering while a later window action was waiting, so a late "
                + "completion cannot inject a stale input grant.",
                Action, Hwnd, time.GetElapsedTime(RequestedAt).TotalMilliseconds);
        }

        internal void Finish()
        {
            // Deliberately no CancellationTokenSource.Dispose: a successor may still be inside
            // Abandon → Cancel (the CTS docs require Dispose only when every other operation has
            // completed), and there is nothing to release — no registrations and no WaitHandle.
            Finished.Set();
        }
    }
}
