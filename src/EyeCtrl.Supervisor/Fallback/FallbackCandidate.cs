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
    /// <remarks>
    /// A probe is a prediction, not a guarantee — the exe can vanish between the probe and the
    /// launch, and a registry-based probe can outlive the install it describes. Callers must
    /// therefore treat <see cref="Launch"/>'s result as the authority, never this one.
    /// </remarks>
    public Func<bool> IsInstalled { get; }

    /// <summary>
    /// Launches the AT (e.g. Process.Start on its exe). Returns true only if a process was
    /// actually started.
    /// </summary>
    /// <remarks>
    /// This was an <see cref="Action"/>, and the missing return value was a defect: the launcher
    /// could not tell a real launch from a silent no-op, so it committed the whole fallback to
    /// the first candidate whose probe passed and reported success either way. Implementations
    /// must swallow their own failures and return false rather than throwing — this runs on the
    /// last-resort path, where an escaping exception costs the user the watchdog too.
    /// </remarks>
    public Func<bool> Launch { get; }

    public FallbackCandidate(string id, Func<bool> isInstalled, Func<bool> launch)
    {
        Id = id;
        IsInstalled = isInstalled;
        Launch = launch;
    }
}
