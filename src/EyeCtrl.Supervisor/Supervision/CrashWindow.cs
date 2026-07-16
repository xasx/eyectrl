namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class CrashWindow
{
    private readonly int _maxRetries;
    private readonly int _windowSeconds;
    private readonly List<DateTime> _strikes = new();

    public CrashWindow(int maxRetries, int windowSeconds)
    {
        _maxRetries = maxRetries;
        _windowSeconds = windowSeconds;
    }

    public void RecordStrike(DateTime at) => _strikes.Add(at);

    public void Reset() => _strikes.Clear();

    public bool ShouldEscalate(DateTime now)
    {
        var cutoff = now.AddSeconds(-_windowSeconds);
        // Strictly greater than maxRetries: maxRetries attempts are always permitted.
        int inWindow = 0;
        foreach (var s in _strikes)
            if (s > cutoff) inWindow++;
        return inWindow > _maxRetries;
    }
}
