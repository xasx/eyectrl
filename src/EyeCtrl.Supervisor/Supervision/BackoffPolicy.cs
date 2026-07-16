namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class BackoffPolicy
{
    private static readonly List<TimeSpan> Curve = new()
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60)
    };

    public TimeSpan DelayFor(int consecutiveFailures)
    {
        var idx = Math.Min(consecutiveFailures - 1, Curve.Count - 1);
        return idx < 0 ? TimeSpan.Zero : Curve[idx];
    }
}
