namespace EyeCtrl.Core.Diagnostics;

/// <summary>
/// A rolling-window fault budget: <see cref="TryConsume"/> records a fault and returns true while
/// fewer than the maximum sit inside the window, and returns false once the budget is exhausted —
/// the moment a log-and-continue policy should stop containing faults and let the process die.
/// Shared by the app's and the supervisor's <c>DispatcherUnhandledException</c> policies so the
/// two cannot drift. Not synchronized: the handlers run on their process's UI thread.
/// </summary>
public sealed class RollingFaultBudget
{
    private readonly int _max;
    private readonly long _windowMs;
    private readonly Func<long> _nowMs;
    private readonly Queue<long> _faults = new();

    /// <param name="maxFaults">Faults allowed inside the window before the budget is exhausted.</param>
    /// <param name="window">How long a single fault counts against the budget.</param>
    /// <param name="nowMs">Clock in milliseconds, injectable for tests; defaults to
    /// <see cref="Environment.TickCount64"/>.</param>
    public RollingFaultBudget(int maxFaults, TimeSpan window, Func<long>? nowMs = null)
    {
        _max = maxFaults;
        _windowMs = (long)window.TotalMilliseconds;
        _nowMs = nowMs ?? DefaultNow;
    }

    /// <summary>Records a fault unless the budget is exhausted. A fault exactly one window old
    /// still counts (expiry is strictly greater than the window).</summary>
    public bool TryConsume()
    {
        var now = _nowMs();
        while (_faults.Count > 0 && now - _faults.Peek() > _windowMs)
            _faults.Dequeue();
        if (_faults.Count >= _max)
            return false;
        _faults.Enqueue(now);
        return true;
    }

    private static long DefaultNow() => Environment.TickCount64;
}

/// <summary>
/// The dispatcher crash policy both EyeCtrl processes share. Supervised: always die on a UI-thread
/// fault — the supervisor's respawn owns recovery rather than the app limping on in a corrupt
/// state. Standalone (nothing respawns the process; a crash strands a gaze-only user until a
/// caregiver arrives): log-and-continue while the budget lasts; a hard-looping UI dies instead of
/// wedging input invisibly.
/// </summary>
public static class DispatcherCrashPolicy
{
    /// <summary>Whether the dispatcher fault should be contained (true → set
    /// <c>Handled</c> and keep running) or let the process die.</summary>
    /// <param name="supervisionEnabled">Whether a supervisor owns this process's recovery.</param>
    /// <param name="budget">The standalone-mode fault budget.</param>
    public static bool ShouldKeepRunning(bool supervisionEnabled, RollingFaultBudget budget) =>
        !supervisionEnabled && budget.TryConsume();
}
