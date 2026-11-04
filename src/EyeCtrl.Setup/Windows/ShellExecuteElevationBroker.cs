namespace EyeCtrl.Setup.Windows;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The production elevation seam: the medium instance re-launches the same exe (or its verified
/// run copy) with ShellExecute <c>runas</c> and maps the child's exit code back. Each request maps
/// to exactly ONE child launch. The elevation policy is re-read immediately before every launch
/// (a policy refresh mid-run must not put a prompt on the secure desktop), and the child wait is
/// bounded.
/// </summary>
public sealed class ShellExecuteElevationBroker : IElevationBroker
{
    private readonly SetupPaths _paths;
    private readonly ElevatedOperations _operations;
    private readonly ISetupEnvironment _environment;
    private readonly IElevationState _elevationState;
    private readonly IElevationPolicy _policy;
    private readonly IElevatedLauncher _launcher;
    private readonly ISetupLog _log;

    /// <summary>Creates the broker.</summary>
    public ShellExecuteElevationBroker(
        SetupPaths paths, ElevatedOperations operations, ISetupEnvironment environment,
        IElevationState elevationState, IElevationPolicy policy, IElevatedLauncher launcher,
        ISetupLog log)
    {
        _paths = paths;
        _operations = operations;
        _environment = environment;
        _elevationState = elevationState;
        _policy = policy;
        _launcher = launcher;
        _log = log;
    }

    /// <inheritdoc />
    public bool IsElevated => _elevationState.IsElevated;

    /// <inheritdoc />
    public ElevatedPhaseResult Run(ElevatedRequest request)
    {
        if (IsElevated)
            return _operations.Execute(request);

        var policy = _policy.Read();
        if (!policy.Allowed)
        {
            _log.Warn("Elevation policy refuses before the runas launch.");
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "elevationPolicy");
        }

        var (exePath, arguments) = BuildLaunch(request);
        if (string.IsNullOrWhiteSpace(exePath))
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingExe");

        var result = _launcher.Launch(exePath, arguments);
        return result.Outcome switch
        {
            LauncherOutcome.Exited => FromExitCode(result.ExitCode),
            LauncherOutcome.Blocked => new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "elevationRefused"),
            LauncherOutcome.TimedOut => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childTimeout"),
            LauncherOutcome.StillRunning => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childStillRunning"),
            _ => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, result.Detail ?? "launchFailed"),
        };
    }

    /// <summary>
    /// The exe and argv for one request. Internal so the round-trip test can prove that every
    /// argv the broker builds parses back through <see cref="SetupCommandLine"/> into the same job.
    /// </summary>
    internal (string ExePath, IReadOnlyList<string> Arguments) BuildLaunch(ElevatedRequest request)
    {
        var runId = RequireRunId(request);
        return request.Job switch
        {
            ElevatedJob.PrepareUpdate => (RequireSelfPath(),
            [
                "--internal", "prepare-update", request.PackageDirectory ?? string.Empty,
                "--run-id", runId,
            ]),
            ElevatedJob.ApplyUpdate => (_paths.RunSetupExePath(runId),
                ["--internal", "apply-update", "--run-id", runId]),
            ElevatedJob.PrepareRunCopy => (RequireSelfPath(),
            [
                "--internal", "prepare-run-copy", "--source", request.SourceSetupExe ?? string.Empty,
                "--run-id", runId,
            ]),
            ElevatedJob.ApplyRollback => (_paths.RunSetupExePath(runId),
                ["--internal", "apply-rollback", "--run-id", runId]),
            ElevatedJob.Revert => (_paths.RunSetupExePath(runId),
                ["--internal", "revert", "--run-id", runId]),
            ElevatedJob.Uninstall => (_paths.RunSetupExePath(runId),
            [
                "--internal", "uninstall", request.KeepData ? "--keep" : "--delete",
                "--run-id", runId,
            ]),
            ElevatedJob.Repair => (_paths.RunSetupExePath(runId),
                ["--internal", "repair", "--run-id", runId]),
            _ => (string.Empty, Array.Empty<string>()),
        };
    }

    private static ElevatedPhaseResult FromExitCode(int exitCode) => exitCode switch
    {
        SetupExitCodes.Ok => ElevatedPhaseResult.Ok,
        SetupExitCodes.Blocked => new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "childBlocked"),
        SetupExitCodes.VerifyFailed => new ElevatedPhaseResult(ElevatedPhaseStatus.VerifyFailed, "childVerifyFailed"),
        SetupExitCodes.NotApplied => new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, "childNotApplied"),
        SetupExitCodes.AllFailed => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childAllFailed"),
        _ => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childFailed"),
    };

    private string RequireSelfPath() =>
        _environment.ProcessPath ?? throw new InvalidOperationException("The process path is unavailable.");

    private static string RequireRunId(ElevatedRequest request) =>
        request.RunId ?? throw new InvalidOperationException("The elevated request has no run id.");
}

/// <summary>The production elevation state: the current process token's elevated bit.</summary>
public sealed class WindowsElevationState : IElevationState
{
    /// <inheritdoc />
    public bool IsElevated
    {
        get
        {
            try
            {
                return WindowsTokenReader.Read(Environment.ProcessId).Elevated == true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

/// <summary>The production launcher: ShellExecute <c>runas</c>, one process, bounded wait, and a
/// plain <c>Kill()</c> of that child only (never a tree).</summary>
public sealed class ShellExecuteRunAsLauncher : IElevatedLauncher
{
    private readonly TimeSpan _timeout;
    private readonly ISetupLog _log;

    /// <summary>Creates the launcher with a ten-minute bound.</summary>
    public ShellExecuteRunAsLauncher(ISetupLog log, TimeSpan? timeout = null)
    {
        _log = log;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    /// <inheritdoc />
    public LauncherResult Launch(string exePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return new LauncherResult(LauncherOutcome.Failed, Detail: "startFailed");

            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                // Kill the elevated child itself; its own children are unrelated programs.
                try { process.Kill(); } catch (Exception) { /* raced exit */ }
                process.WaitForExit((int)SetupConstants.ProcessExitWait.TotalMilliseconds);
                if (!process.HasExited)
                {
                    // G1: the medium caller may lack TERMINATE on a runas child. Never launch
                    // the next elevated phase while this one may still be renaming.
                    _log.Error("Elevated child is still running after the timeout kill.");
                    return new LauncherResult(LauncherOutcome.StillRunning, Detail: "childStillRunning");
                }

                _log.Error("Elevated child exceeded the wait bound and was killed.");
                return new LauncherResult(LauncherOutcome.TimedOut, Detail: "childTimeout");
            }

            return new LauncherResult(LauncherOutcome.Exited, process.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1223 or 5)
        {
            _log.Warn($"Elevation refused: {ex.NativeErrorCode}.");
            return new LauncherResult(LauncherOutcome.Blocked, Detail: "elevationRefused");
        }
        catch (Exception ex)
        {
            _log.Error($"Elevated launch failed: {ex.GetType().Name}");
            return new LauncherResult(LauncherOutcome.Failed, Detail: ex.GetType().Name);
        }
    }
}
