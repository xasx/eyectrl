namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class CrashWindow
{
    private int _maxRetries;
    private int _windowSeconds;
    private readonly List<DateTime> _strikes = new();

    public CrashWindow(int maxRetries, int windowSeconds)
    {
        _maxRetries = maxRetries;
        _windowSeconds = windowSeconds;
    }

    /// <summary>
    /// Applies a live change to the escalation limits (e.g. from the supervisor setup window) without
    /// a restart. Accumulated strikes are left untouched — only the escalation threshold and window
    /// used by <see cref="ShouldEscalate"/> going forward change.
    /// </summary>
    public void UpdateLimits(int maxRetries, int windowSeconds)
    {
        _maxRetries = maxRetries;
        _windowSeconds = windowSeconds;
    }

    public void RecordStrike(DateTime at) => _strikes.Add(at);

    public void Reset() => _strikes.Clear();

    /// <summary>
    /// Whether the crash policy has run out of patience. Two rules (ticket 06-C):
    /// more than <c>maxRetries</c> strikes inside the window, OR more than <c>maxRetries</c>
    /// consecutive failures since the last reset. The second rule exists because a loop slower
    /// than the window — e.g. one failed relaunch per 60 s grace — never accumulates enough strikes
    /// inside it, so the window rule alone would retry forever without escalating. A reset
    /// (<see cref="Reset"/>, driven by a survived crash window) clears both.
    /// </summary>
    public bool ShouldEscalate(DateTime now, int consecutiveFailures)
    {
        if (consecutiveFailures > _maxRetries) return true;

        var cutoff = now.AddSeconds(-_windowSeconds);
        // Strictly greater than maxRetries: maxRetries attempts are always permitted.
        int inWindow = 0;
        foreach (var s in _strikes)
            if (s > cutoff) inWindow++;
        return inWindow > _maxRetries;
    }
}
