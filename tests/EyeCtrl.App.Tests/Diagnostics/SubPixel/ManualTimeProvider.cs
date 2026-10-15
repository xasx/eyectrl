namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

/// <summary>
/// A virtual clock for driving C5 in App-level tests (fixup 02 H1/M4): <see cref="Advance"/> fires
/// every timer whose due time it passes, in due order, and re-schedules whatever the callbacks arm.
/// <see cref="TimestampFrequency"/> is 1000, so one tick is one millisecond.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private long _nowMs;

    /// <inheritdoc/>
    public override long TimestampFrequency => 1000;

    /// <inheritdoc/>
    public override long GetTimestamp() => _nowMs;

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Advances the clock, firing every due timer until none is left before the target.</summary>
    /// <param name="ms">Milliseconds to advance; must not be negative.</param>
    public void Advance(long ms)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
        long target = _nowMs + ms;

        for (int guard = 0; guard < 100_000; guard++)
        {
            ManualTimer? next = null;
            long best = long.MaxValue;
            foreach (var timer in _timers)
            {
                if (timer.DueAt is { } due && due <= target && due < best)
                {
                    best = due;
                    next = timer;
                }
            }

            if (next is null) break;
            _nowMs = best;
            next.Fire();
        }

        _nowMs = target;
    }

    private long NowMs => _nowMs;

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public long? DueAt { get; private set; }

        public void Fire()
        {
            if (_disposed) return;
            DueAt = _period == Timeout.InfiniteTimeSpan
                ? null
                : _owner.NowMs + (long)_period.TotalMilliseconds;
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                ? null
                : _owner.NowMs + (long)dueTime.TotalMilliseconds;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            DueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
