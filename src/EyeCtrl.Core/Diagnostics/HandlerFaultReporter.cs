namespace EyeCtrl.Core.Diagnostics;

/// <summary>
/// Emits at most one report per source per window when something on the gaze thread throws, carrying a
/// count of what was suppressed in between.
/// </summary>
/// <remarks>
/// Rate limiting is the point. The gaze fan-out runs at 30-90 Hz inside the gaze provider's lock, so
/// an unbounded log write per fault would stall the gaze stream — a worse outcome than the exception
/// being reported. A source that throws on every sample must cost one log line per window, not one
/// per sample.
/// <para>
/// Two report shapes share one limiter. <see cref="Report(string, Delegate, Exception)"/> keys on the
/// faulting handler, so each subscriber of a fanned-out event gets its own window.
/// <see cref="Report(string, Exception)"/> keys on the message, for a single fixed fault site such as
/// a provider's tick callback. The two key spaces cannot collide: a <see cref="Delegate"/> never
/// equals a <see cref="string"/>.
/// </para>
/// <para>
/// Thread-safe: the window bookkeeping runs under a private lock. It has to be — a provider's fault
/// site and the fan-out it feeds can sit on different threads, and even the mouse provider alone
/// hands successive ticks to different pool threads, so the dictionary and its entries need a
/// release/acquire pair for visibility and not merely mutual exclusion. The lock is only ever taken
/// on the fault path, never per sample, and <c>_emit</c> is invoked outside it so a slow logger
/// cannot serialize two faulting threads.
/// </para>
/// <para>
/// Entries are dropped by <see cref="Forget"/> when a handler unsubscribes, which is what bounds the
/// dictionary: a site that re-subscribes on every settings toggle — AdaptationCoordinator does — no
/// longer leaves an entry behind per subscription instance. Growth without a matching unsubscribe is
/// still possible in principle, but every fanned-out event routes its removals through
/// <see cref="Forget"/>, so it does not arise in this codebase.
/// </para>
/// </remarks>
public sealed class HandlerFaultReporter
{
    private sealed class Entry
    {
        public long WindowStartSeconds;
        public int SuppressedSinceEmit;
    }

    private readonly Action<string, Exception> _emit;
    private readonly Func<long> _nowSeconds;
    private readonly long _windowSeconds;
    private readonly object _gate = new();
    private readonly Dictionary<object, Entry> _entries = [];

    /// <summary>Initializes a new instance of the <see cref="HandlerFaultReporter"/> class.</summary>
    /// <param name="emit">Receives the formatted message and the exception.</param>
    /// <param name="nowSeconds">Monotonic clock in seconds; injectable so tests need no real time.</param>
    /// <param name="windowSeconds">Minimum gap between two reports for the same source.</param>
    public HandlerFaultReporter(Action<string, Exception> emit, Func<long> nowSeconds, long windowSeconds)
    {
        _emit = emit;
        _nowSeconds = nowSeconds;
        _windowSeconds = windowSeconds;
    }

    /// <summary>Records a handler fault, emitting a report if that handler is outside its window.</summary>
    /// <param name="eventName">Name of the event that was being published.</param>
    /// <param name="handler">The handler that threw; identified by its method for the message.</param>
    /// <param name="exception">The exception it threw.</param>
    public void Report(string eventName, Delegate handler, Exception exception)
    {
        // The message is built only when it will actually be emitted: on a handler that throws every
        // sample this method runs at the gaze rate, and formatting a string to discard it would put
        // an allocation back on the hot path the fan-out was made allocation-free to avoid.
        if (!TryOpenWindow(handler, out int suppressed)) return;

        // Method.DeclaringType + Method.Name names the enclosing method even for a lambda — e.g.
        // App+<>c__DisplayClass0_0.<OnStartup>b__0 — which is enough to locate the offender.
        string who = $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
        Emit($"{eventName} handler {who} threw", exception, suppressed);
    }

    /// <summary>
    /// Records a fault at a fixed site, emitting a report if that site is outside its window. Use for a
    /// single named fault path — a provider's tick or poll callback — where there is no per-handler
    /// identity to key on.
    /// </summary>
    /// <param name="context">
    /// What failed, as a complete sentence; doubles as the rate-limiting key, so pass a constant rather
    /// than a per-fault interpolation.
    /// </param>
    /// <param name="exception">The exception that was caught.</param>
    public void Report(string context, Exception exception)
    {
        if (!TryOpenWindow(context, out int suppressed)) return;
        Emit(context, exception, suppressed);
    }

    /// <summary>
    /// Drops the window bookkeeping for a handler that has unsubscribed. Call from an event's remove
    /// accessor once the handler is gone for good, so the dictionary does not accumulate an entry per
    /// subscription instance. A handler that re-subscribes later starts with a fresh window and reports
    /// its next fault immediately, which is the wanted behaviour: it is a new subscription.
    /// </summary>
    /// <param name="handler">The handler that unsubscribed.</param>
    public void Forget(Delegate handler)
    {
        lock (_gate)
            _entries.Remove(handler);
    }

    /// <summary>
    /// Decides whether this fault is reportable and, if so, opens a fresh window for its key.
    /// </summary>
    /// <param name="key">The rate-limiting key — a handler delegate or a fault-site message.</param>
    /// <param name="suppressed">How many faults were swallowed since the last report for this key.</param>
    /// <returns><c>true</c> when the caller should emit.</returns>
    private bool TryOpenWindow(object key, out int suppressed)
    {
        suppressed = 0;
        lock (_gate)
        {
            long now = _nowSeconds();

            // First fault for this key: emit at once and open its window.
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = new Entry { WindowStartSeconds = now, SuppressedSinceEmit = 0 };
                return true;
            }

            // Inside the window: count it and stay silent.
            if (now - entry.WindowStartSeconds < _windowSeconds)
            {
                entry.SuppressedSinceEmit++;
                return false;
            }

            // Window elapsed: report what was swallowed, and open a fresh window.
            suppressed = entry.SuppressedSinceEmit;
            entry.WindowStartSeconds = now;
            entry.SuppressedSinceEmit = 0;
            return true;
        }
    }

    private void Emit(string what, Exception exception, int suppressed)
    {
        string message = suppressed > 0
            ? $"{what}; {suppressed} further fault(s) suppressed since the last report"
            : what;
        try
        {
            _emit(message, exception);
        }
        catch
        {
            // This reporter runs inside a catch on the gaze thread; an emit that throws would re-open
            // the very containment it reports into. A broken logger is itself unreportable.
        }
    }
}
