namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Diagnostics;
using System.Windows.Threading;

/// <summary>
/// The dispatcher-backed <see cref="TimeProvider"/> the sub-pixel probe injects into C5 (SP-3 review
/// 01 deviation 2; build plan C5/C7). C5 is single-threaded by contract (review 05 F4b): every timer
/// callback must run on the UI thread, so every timer this provider creates is a
/// <see cref="DispatcherTimer"/> owned by <paramref name="dispatcher"/>.
///
/// <para>
/// <b>There is deliberately no fallback to <see cref="TimeProvider.System"/>.</b> A thread-pool
/// callback would arrive off the UI thread and let C5 raise <c>BlockCompleted</c> in the middle of a
/// dispatcher callback — the exact quiescence failure review 05 F4b closed. A construction without a
/// dispatcher is a programming error, not a supported mode.
/// </para>
///
/// <para>
/// <b>Cancellation is generation-based.</b> The C5 retiming pattern re-arms constantly (the silence
/// guard re-arms to the exact millisecond the current run crosses 500 ms, and the eyes-closed timer
/// re-arms on every valid sample), so <see cref="ITimer.Change"/> and <see cref="ITimer.Dispose"/>
/// often run while a tick is pending. Two layers cover that. <c>DispatcherTimer.Stop()</c> aborts a
/// tick that is still in the dispatcher queue. A tick whose dispatch has already begun (it was
/// dequeued before Change or Dispose ran) cannot be aborted, so each arm gets a token, and a tick
/// with a stale token, or one arriving after Dispose, is dropped silently. Together they guarantee
/// that Change and Dispose never let a stale tick reach the callback, as review 01 requires.
/// </para>
///
/// <para>
/// The four C5 timers (phase, eyes-closed, screen, void guard) are all created through this class;
/// the probe creates one provider per run and disposes it in Teardown alongside the session timer
/// and gaze handler — before C5's own dispose, whose four timers this provider created and which
/// C5 stops itself.
/// </para>
/// </summary>
internal sealed class DispatcherTimeProvider : TimeProvider
{
    private readonly Dispatcher _dispatcher;
    private readonly List<Timer> _timers = new();

    /// <summary>
    /// Creates the provider. Every timer it creates ticks on <paramref name="dispatcher"/>.
    /// </summary>
    /// <param name="dispatcher">The UI dispatcher whose thread every C5 callback must run on.</param>
    public DispatcherTimeProvider(Dispatcher dispatcher) =>
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <inheritdoc/>
    public override long TimestampFrequency => Stopwatch.Frequency;

    /// <inheritdoc/>
    public override long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new Timer(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Cancels every timer this provider created that is still armed, so no queued tick can reach a
    /// torn-down run. Called once per run, after C5 has been disposed; disposing a timer twice is
    /// harmless.
    /// </summary>
    public void Dispose()
    {
        foreach (var timer in _timers) timer.Dispose();
        _timers.Clear();
    }

    private sealed class Timer : ITimer
    {
        private readonly DispatcherTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DispatcherTimer? _timer;
        private long _token;
        private bool _disposed;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public Timer(DispatcherTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;

            // Invalidate any tick already queued from a previous arm, then stop the timer itself.
            long token = ++_token;
            _timer?.Stop();
            _timer = null;
            _period = period;

            if (dueTime == Timeout.InfiniteTimeSpan) return true;

            var timer = new DispatcherTimer(DispatcherPriority.Normal, _owner._dispatcher)
            {
                Interval = dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime,
            };
            timer.Tick += (_, _) => OnTick(timer, token);
            _timer = timer;
            timer.Start();
            return true;
        }

        private void OnTick(DispatcherTimer sender, long token)
        {
            // A queued tick whose arm was replaced (Change) or cancelled (Dispose) must not run.
            if (_disposed || token != _token) return;

            if (_period == Timeout.InfiniteTimeSpan)
            {
                sender.Stop();
                if (ReferenceEquals(_timer, sender)) _timer = null;
            }
            else
            {
                sender.Interval = _period;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _token++;
            try { _timer?.Stop(); }
            catch (InvalidOperationException) { /* dispatcher already shut down */ }
            _timer = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
