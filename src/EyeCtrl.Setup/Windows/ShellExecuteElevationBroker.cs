namespace EyeCtrl.Setup.Windows;

using System.ComponentModel;
using System.Diagnostics;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The production elevation seam: the medium instance re-launches the same exe (or its verified
/// run copy) with ShellExecute <c>runas</c> and maps the child's exit code back. A refused
/// elevation (1223/5) becomes <see cref="ElevatedPhaseStatus.Blocked"/>; nothing waits on a UI
/// prompt, and the policy gate has already refused when a prompt would be needed.
/// </summary>
public sealed class ShellExecuteElevationBroker : IElevationBroker
{
    private readonly SetupPaths _paths;
    private readonly ElevatedOperations _operations;
    private readonly ISetupEnvironment _environment;
    private readonly ISetupLog _log;

    /// <summary>Creates the broker.</summary>
    public ShellExecuteElevationBroker(
        SetupPaths paths, ElevatedOperations operations, ISetupEnvironment environment,
        ISetupLog log)
    {
        _paths = paths;
        _operations = operations;
        _environment = environment;
        _log = log;
    }

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

    /// <inheritdoc />
    public ElevatedPhaseResult Run(ElevatedRequest request)
    {
        if (IsElevated)
            return _operations.Execute(request);

        return request.Job switch
        {
            ElevatedJob.PrepareUpdate => PrepareThenApply(request),
            ElevatedJob.ApplyUpdate => LaunchElevated(
                _paths.RunSetupExePath(RequireRunId(request)),
                ["--internal", "apply-update", "--run-id", RequireRunId(request)]),
            ElevatedJob.PrepareRunCopy => LaunchElevated(
                RequireSelfPath(),
                ["--internal", "prepare-run-copy", "--source", request.SourceSetupExe ?? string.Empty,
                    "--run-id", RequireRunId(request)]),
            ElevatedJob.ApplyRollback => LaunchElevated(
                _paths.RunSetupExePath(RequireRunId(request)),
                ["--internal", "apply-rollback", "--run-id", RequireRunId(request)]),
            ElevatedJob.Revert => LaunchElevated(
                _paths.RunSetupExePath(RequireRunId(request)),
                ["--internal", "revert", "--run-id", RequireRunId(request)]),
            ElevatedJob.Uninstall => LaunchElevated(
                _paths.RunSetupExePath(RequireRunId(request)),
                ["--internal", "uninstall", request.KeepData ? "--keep" : "--delete",
                    "--run-id", RequireRunId(request)]),
            ElevatedJob.Repair => LaunchElevated(
                _paths.RunSetupExePath(RequireRunId(request)),
                ["--internal", "repair", "--run-id", RequireRunId(request)]),
            _ => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "unknownJob"),
        };
    }

    private ElevatedPhaseResult PrepareThenApply(ElevatedRequest request)
    {
        var runId = RequireRunId(request);
        var prepare = LaunchElevated(
            RequireSelfPath(),
            ["--internal", "prepare-update", request.PackageDirectory ?? string.Empty,
                "--run-id", runId]);
        if (!prepare.Succeeded)
            return prepare;

        return LaunchElevated(
            _paths.RunSetupExePath(runId),
            ["--internal", "apply-update", "--run-id", runId]);
    }

    private ElevatedPhaseResult LaunchElevated(string exePath, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingExe");

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "startFailed");

            process.WaitForExit();
            return FromExitCode(process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1223 or 5)
        {
            // ERROR_CANCELLED / ERROR_ACCESS_DENIED: the owner or policy refused. Never retry.
            _log.Warn($"Elevation refused: {ex.NativeErrorCode}.");
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "elevationRefused");
        }
        catch (Exception ex)
        {
            _log.Error($"Elevated launch failed: {ex.GetType().Name}");
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, ex.GetType().Name);
        }
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
