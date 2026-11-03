namespace EyeCtrl.Setup;

using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;

/// <summary>The read-only <c>status</c> document. No field causes a mutation.</summary>
public sealed record StatusReport(
    bool Installed,
    string? Version,
    string? PreviousVersion,
    bool StagingPending,
    bool FailedVersionPresent,
    string State,
    bool AppRunning,
    bool SupervisorRunning,
    bool ElevationAllowed,
    int? ConsentPromptBehaviorAdmin,
    SetupResult? LastResult,
    string InstallRoot,
    string PreviousRoot,
    string StagingRoot,
    string FailedRoot,
    string RunRoot);

/// <summary>
/// Builds the <c>status</c> report from reads only: the folder layout, the process snapshot, the
/// elevation policy and the last result record. It never renames, deletes, writes or starts.
/// </summary>
public sealed class StatusService
{
    private readonly SetupPaths _paths;
    private readonly InstallStateMachine _state;
    private readonly IProcessControl _processes;
    private readonly IElevationPolicy _policy;
    private readonly IResultRecorder _recorder;

    /// <summary>Creates the service.</summary>
    public StatusService(
        SetupPaths paths, InstallStateMachine state, IProcessControl processes,
        IElevationPolicy policy, IResultRecorder recorder)
    {
        _paths = paths;
        _state = state;
        _processes = processes;
        _policy = policy;
        _recorder = recorder;
    }

    /// <summary>Reads the state and returns the report.</summary>
    public StatusReport GetReport()
    {
        var snapshot = _state.Snapshot();
        var processes = _processes.Snapshot();
        var policy = _policy.Read();
        var appRunning = processes.Any(p =>
            string.Equals(p.ImagePath, _paths.AppExePath, StringComparison.OrdinalIgnoreCase));
        var supervisorRunning = processes.Any(p =>
            string.Equals(p.ImagePath, _paths.SupervisorExePath, StringComparison.OrdinalIgnoreCase));

        return new StatusReport(
            snapshot.InstallExists,
            snapshot.InstalledVersion,
            snapshot.PreviousVersion,
            snapshot.StagingExists,
            snapshot.FailedExists,
            snapshot.State,
            appRunning,
            supervisorRunning,
            policy.Allowed,
            policy.ConsentPromptBehaviorAdmin,
            _recorder.TryReadLast(),
            _paths.InstallRoot,
            _paths.PreviousRoot,
            _paths.StagingRoot,
            _paths.FailedRoot,
            _paths.RunRoot);
    }
}
