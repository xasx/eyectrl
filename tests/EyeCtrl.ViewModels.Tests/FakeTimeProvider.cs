namespace EyeCtrl.ViewModels.Tests;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for the sub-pixel probe tests: the test advances the
/// clock in explicit steps and every timer due inside the step fires at its exact due time, so a
/// phase transition lands on the millisecond its duration says. Timers are created through
/// <see cref="CreateTimer"/> exactly as production would; <see cref="ScheduledTimerCount"/> is the
/// quiescence oracle after a dispose.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<FakeTimer> _timers = new();
    private long _nowMs;

    /// <summary>One tick per millisecond, so <see cref="GetElapsedTime(long)"/> reads in ms.</summary>
    public override long TimestampFrequency => 1000;

    /// <inheritdoc/>
    public override long GetTimestamp() => _nowMs;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => Epoch.AddMilliseconds(_nowMs);

    /// <summary>Timers that are armed (due) right now. Zero after a dispose or an End test.</summary>
    public int ScheduledTimerCount => _timers.Count(timer => timer.IsScheduled);

    /// <summary>Timers created through this provider, for oracle messages.</summary>
    public int CreatedTimerCount => _timers.Count;

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward, firing every due timer at its due time in due order (one callback per
    /// period for periodic timers). A callback that re-arms its timer is honoured before the loop
    /// continues.
    /// </summary>
    /// <param name="milliseconds">How far to advance; not negative.</param>
    public void Advance(long milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));

        long target = _nowMs + milliseconds;

        while (true)
        {
            FakeTimer? next = null;
            foreach (var timer in _timers)
            {
                if (!timer.IsScheduled || timer.DueMs > target) continue;
                if (next is null || timer.DueMs < next.DueMs) next = timer;
            }

            if (next is null) break;

            _nowMs = next.DueMs;
            next.Fire();
        }

        _nowMs = target;
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        public long DueMs { get; private set; }
        public long PeriodMs { get; private set; }
        public bool IsScheduled { get; private set; }
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(FakeTimer));

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                IsScheduled = false;
                return true;
            }

            DueMs = _provider._nowMs + (long)dueTime.TotalMilliseconds;
            PeriodMs = period == Timeout.InfiniteTimeSpan ? 0 : (long)period.TotalMilliseconds;
            IsScheduled = true;
            return true;
        }

        public void Fire()
        {
            if (!IsScheduled) return;

            if (PeriodMs > 0) DueMs += PeriodMs;
            else IsScheduled = false;

            _callback(_state);
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsScheduled = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
