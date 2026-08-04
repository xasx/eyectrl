namespace EyeCtrl.Core.Diagnostics;

/// <summary>
/// Emits at most one report per handler per window when an event handler throws, carrying a count of
/// what was suppressed in between.
/// </summary>
/// <remarks>
/// Rate limiting is the point. The gaze fan-out runs at 30-90 Hz inside the gaze provider's lock, so
/// an unbounded log write per fault would stall the gaze stream — a worse outcome than the exception
/// being reported. A handler that throws on every sample must cost one log line per window, not one
/// per sample.
/// <para>
/// Not thread-safe, and safe only because of an invariant worth stating: every gaze provider publishes
/// from one thread at a time. Tobii uses a dedicated <c>PollLoop</c> thread. MouseGazeProvider uses a
/// one-shot <see cref="System.Threading.Timer"/> re-armed in a <c>finally</c>, so although successive
/// ticks may land on different pool threads, two ticks never overlap. Switching that to a periodic
/// timer, or running two providers concurrently, would require a lock here.
/// </para>
/// <para>
/// The dictionary is touched on the fault path only, never per sample, and has no eviction. That is
/// deliberate and bounded: the compiler caches a lambda per call site, so each lambda subscriber
/// contributes exactly one entry; a method-group subscription allocates a fresh delegate per
/// <c>+=</c>, so a site that re-subscribes — AdaptationCoordinator subscribes and unsubscribes its
/// handler on every adaptation Enable toggle — adds one entry per distinct subscription instance
/// that faults. Growth is bounded by user actions and occurs only on the fault path; a site that
/// re-subscribes in a hot loop would still need eviction added.
/// </para>
/// </remarks>
internal sealed class HandlerFaultReporter
{
    private sealed class Entry
    {
        public long WindowStartSeconds;
        public int SuppressedSinceEmit;
    }

    private readonly Action<string, Exception> _emit;
    private readonly Func<long> _nowSeconds;
    private readonly long _windowSeconds;
    private readonly Dictionary<Delegate, Entry> _entries = [];

    /// <param name="emit">Receives the formatted message and the exception.</param>
    /// <param name="nowSeconds">Monotonic clock in seconds; injectable so tests need no real time.</param>
    /// <param name="windowSeconds">Minimum gap between two reports for the same handler.</param>
    public HandlerFaultReporter(Action<string, Exception> emit, Func<long> nowSeconds, long windowSeconds)
    {
        _emit = emit;
        _nowSeconds = nowSeconds;
        _windowSeconds = windowSeconds;
    }

    /// <summary>Records a fault, emitting a report if this handler is outside its window.</summary>
    /// <param name="eventName">Name of the event that was being published.</param>
    /// <param name="handler">The handler that threw; identified by its method for the message.</param>
    /// <param name="exception">The exception it threw.</param>
    public void Report(string eventName, Delegate handler, Exception exception)
    {
        long now = _nowSeconds();

        // First fault for this handler: emit at once and open its window.
        if (!_entries.TryGetValue(handler, out var entry))
        {
            _entries[handler] = new Entry { WindowStartSeconds = now, SuppressedSinceEmit = 0 };
            Emit(eventName, handler, exception, suppressed: 0);
            return;
        }

        // Inside the window: count it and stay silent.
        if (now - entry.WindowStartSeconds < _windowSeconds)
        {
            entry.SuppressedSinceEmit++;
            return;
        }

        // Window elapsed: emit, reporting what was swallowed, and open a fresh window.
        int suppressed = entry.SuppressedSinceEmit;
        entry.WindowStartSeconds = now;
        entry.SuppressedSinceEmit = 0;
        Emit(eventName, handler, exception, suppressed);
    }

    private void Emit(string eventName, Delegate handler, Exception exception, int suppressed)
    {
        // Method.DeclaringType + Method.Name names the enclosing method even for a lambda — e.g.
        // App+<>c__DisplayClass0_0.<OnStartup>b__0 — which is enough to locate the offender.
        string who = $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
        string message = suppressed > 0
            ? $"{eventName} handler {who} threw; {suppressed} further fault(s) suppressed since the last report"
            : $"{eventName} handler {who} threw";
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
