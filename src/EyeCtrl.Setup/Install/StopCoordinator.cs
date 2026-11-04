namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>The stop result: whether the swap may proceed, whether the kill path ran, and what
/// still blocks it.</summary>
public sealed record StopResult(bool Ok, bool WasKillPath, string? Blocker)
{
    /// <summary>Stop completed and the image check is clean.</summary>
    public static StopResult Clean(bool killPath) => new(true, killPath, null);

    /// <summary>A process still runs from the install folder; the swap must not proceed.</summary>
    public static StopResult Blocked(bool killPath, string blocker) => new(false, killPath, blocker);
}

/// <summary>
/// Plan §10 step 3's stop order.
/// <para>
/// Owner path: the App has already exited cleanly (S6f), Setup waits briefly for it and then stops
/// the idle Supervisor. It never sends <c>RestartRequested</c> and never kills the App.
/// </para>
/// <para>
/// Coordinator/uninstall path: the App is running, so the Supervisor is stopped FIRST and the App
/// only after it — otherwise the kill looks like a crash and the watchdog relaunches the App into
/// a half-swapped folder. After the kill the C3 held-button marker is released with the same
/// single button-up the Supervisor's <c>HeldButtonRelease</c> sends.
/// </para>
/// <para>Either way the image check is the last word: any process, the Supervisor included, still
/// running an image from <c>EyeCtrl\</c> refuses the swap.</para>
/// </summary>
public sealed class StopCoordinator
{
    private readonly SetupPaths _paths;
    private readonly IProcessControl _processes;
    private readonly IHeldButtonRecovery _heldButton;
    private readonly ISetupLog _log;

    /// <summary>How many App images the kill path will chase after the Supervisor exits.</summary>
    private const int MaxKillRounds = 5;

    /// <summary>Creates the coordinator.</summary>
    public StopCoordinator(
        SetupPaths paths, IProcessControl processes, IHeldButtonRecovery heldButton, ISetupLog log)
    {
        _paths = paths;
        _processes = processes;
        _heldButton = heldButton;
        _log = log;
    }

    /// <summary>Runs the stop sequence and the image check.</summary>
    /// <param name="cleanExitGrace">How long to wait for a clean App exit before treating the
    /// path as a kill.</param>
    /// <param name="forceKillPath">Uninstall always kills: EyeCtrl is still running while the
    /// owner chooses.</param>
    public StopResult StopForSwap(TimeSpan cleanExitGrace, bool forceKillPath = false)
    {
        var snapshot = _processes.Snapshot();
        var app = FindByImage(snapshot, _paths.AppExePath);
        var supervisor = FindByImage(snapshot, _paths.SupervisorExePath);

        var killPath = forceKillPath;
        if (!killPath && app is { } runningApp)
        {
            // Owner path: S6f has already asked the App to exit. A return of false means the App
            // is still here (or the caller is the coordinator), which is the kill path.
            killPath = !_processes.WaitForExit(runningApp.Id, cleanExitGrace);
        }

        if (!killPath)
        {
            // Clean path: stop only the Supervisor. An idle Supervisor launches nothing, so this
            // cannot race. No marker release: there was no kill.
            if (supervisor is { } idleSupervisor)
            {
                _processes.Stop(idleSupervisor.Id);
                _processes.WaitForExit(idleSupervisor.Id, SetupConstants.ProcessExitWait);
            }
        }
        else
        {
            // Kill path: Supervisor first, then the App. Reversing these is the mutant this
            // order exists to catch.
            if (supervisor is { } liveSupervisor)
            {
                _processes.Stop(liveSupervisor.Id);
                _processes.WaitForExit(liveSupervisor.Id, SetupConstants.ProcessExitWait);
            }

            // F11: re-snapshot after the Supervisor has exited. It may have relaunched the App
            // in between; kill every App image that appears, with a bound.
            for (var attempt = 0; attempt < MaxKillRounds; attempt++)
            {
                var liveApp = FindByImage(_processes.Snapshot(), _paths.AppExePath);
                if (liveApp is not { } current)
                    break;

                _processes.Kill(current.Id);
                _processes.WaitForExit(current.Id, SetupConstants.ProcessExitWait);
            }

            // C3: the killed process ran none of its in-process release paths. Release once, in
            // place, and only when the marker exists (HeldButtonRelease itself enforces that).
            var release = _heldButton.ReleaseIfMarked();
            _log.Info(release.MarkerWasSet
                ? $"Held-button release sent; stillDown={release.LeftButtonStillDown?.ToString() ?? "unknown"}."
                : "No held-button marker after the kill.");
        }

        var after = _processes.Snapshot();
        var remaining = after.FirstOrDefault(p =>
            p.ImagePath is { } image && _paths.IsUnderInstallRoot(image));
        if (remaining.ImagePath is { } blocker)
        {
            _log.Warn($"Image check refused the swap: {Path.GetFileName(blocker)} (PID {remaining.Id}).");
            return StopResult.Blocked(killPath, Path.GetFileName(blocker));
        }

        return StopResult.Clean(killPath);
    }

    /// <summary>True when any process in the snapshot runs an image from the install folder.</summary>
    public bool HasProcessFromInstallDirectory(IReadOnlyList<RunningProcess> snapshot) =>
        snapshot.Any(p => p.ImagePath is { } image && _paths.IsUnderInstallRoot(image));

    private static RunningProcess? FindByImage(IReadOnlyList<RunningProcess> snapshot, string path)
    {
        foreach (var process in snapshot)
        {
            if (string.Equals(process.ImagePath, path, StringComparison.OrdinalIgnoreCase))
                return process;
        }

        return null;
    }
}
