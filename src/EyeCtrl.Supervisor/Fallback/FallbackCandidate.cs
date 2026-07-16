namespace EyeCtrl.Supervisor.Fallback;

using System;

/// <summary>
/// One candidate assistive-technology (AT) app that the supervisor can fall back to
/// when EyeCtrl is in a crash loop (FALLEN_BACK). Each candidate knows how to probe
/// whether it is installed and how to launch itself, so the ordering logic in
/// <see cref="FallbackAtLauncher"/> stays probe-agnostic and fully unit-testable.
/// </summary>
public sealed class FallbackCandidate
{
    /// <summary>Stable identifier used for logging, settings ordering, and test assertions (e.g. "TDControl").</summary>
    public string Id { get; }

    /// <summary>Returns true when this AT is available to launch on the current machine.</summary>
    public Func<bool> IsInstalled { get; }

    /// <summary>Launches the AT (e.g. Process.Start on its exe).</summary>
    public Action Launch { get; }

    public FallbackCandidate(string id, Func<bool> isInstalled, Action launch)
    {
        Id = id;
        IsInstalled = isInstalled;
        Launch = launch;
    }
}
