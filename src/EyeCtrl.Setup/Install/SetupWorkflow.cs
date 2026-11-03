namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The medium instance's workflow: the elevation policy gate, the elevated phases, the start and
/// pass rule, the automatic rollback, and the single result record. Every mutation outside the
/// result record happens through <see cref="IElevationBroker"/>.
/// </summary>
public sealed class SetupWorkflow
{
    private readonly SetupPaths _paths;
    private readonly IElevationPolicy _policy;
    private readonly IElevationBroker _broker;
    private readonly AppStartService _appStart;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessControl _processes;
    private readonly IUninstallChoice _uninstallChoice;
    private readonly IRunIdGenerator _runIds;
    private readonly IPackageMetadataReader _metadata;
    private readonly IResultRecorder _recorder;
    private readonly ISetupEnvironment _environment;
    private readonly ISetupClock _clock;
    private readonly ISetupLog _log;

    /// <summary>Creates the workflow.</summary>
    public SetupWorkflow(
        SetupPaths paths, IElevationPolicy policy, IElevationBroker broker, AppStartService appStart,
        IFileSystem fileSystem, IProcessControl processes, IUninstallChoice uninstallChoice,
        IRunIdGenerator runIds, IPackageMetadataReader metadata, IResultRecorder recorder,
        ISetupEnvironment environment, ISetupClock clock, ISetupLog log)
    {
        _paths = paths;
        _policy = policy;
        _broker = broker;
        _appStart = appStart;
        _fileSystem = fileSystem;
        _processes = processes;
        _uninstallChoice = uninstallChoice;
        _runIds = runIds;
        _metadata = metadata;
        _recorder = recorder;
        _environment = environment;
        _clock = clock;
        _log = log;
    }

    /// <summary>Installs the package and applies the three-part pass rule, rolling back on a
    /// failure. The old version is restarted on any pre-swap refusal.</summary>
    public SetupResult Update(string packageDirectory)
    {
        if (MustRunMedium(SetupVerb.Update) is { } medium) return medium;
        if (Blocked(SetupVerb.Update) is { } blocked)
            return blocked;

        var runId = _runIds.NewId();

        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: packageDirectory, RunId: runId));
        if (!prepare.Succeeded)
            return FinishRefused(SetupVerb.Update, prepare);

        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: runId));
        if (!apply.Succeeded)
        {
            if (apply.Status == ElevatedPhaseStatus.Failed)
                _broker.Run(new ElevatedRequest(ElevatedJob.Repair, RunId: runId));
            return FinishRefused(SetupVerb.Update, apply);
        }

        var newVersion = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;

        var start = _appStart.StartAndCheck(_paths.AppExePath);
        if (start.Passed)
        {
            return Finish(SetupVerb.Update, SetupOutcome.Ok, newVersion, previousVersion);
        }

        _log.Warn($"New version failed the pass rule: {start.FailureReason}; rolling back.");

        var revert = _broker.Run(new ElevatedRequest(ElevatedJob.Revert, RunId: runId));
        if (!revert.Succeeded)
        {
            return Finish(SetupVerb.Update, SetupOutcome.AllFailed, newVersion, previousVersion,
                "revert:" + revert.Detail);
        }

        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var oldStart = _appStart.StartAndCheck(_paths.AppExePath);
        return oldStart.Passed
            ? Finish(SetupVerb.Update, SetupOutcome.RolledBack, newVersion, restoredVersion,
                "newVersionFailed:" + start.FailureReason)
            : Finish(SetupVerb.Update, SetupOutcome.AllFailed, newVersion, restoredVersion,
                "previousFailed:" + oldStart.FailureReason);
    }

    /// <summary>Restores <c>EyeCtrl.previous</c> and pass-checks it; a failed target rolls the
    /// rollback back to the version it displaced.</summary>
    public SetupResult Rollback()
    {
        if (MustRunMedium(SetupVerb.Rollback) is { } medium) return medium;
        if (Blocked(SetupVerb.Rollback) is { } blocked)
            return blocked;

        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot))
            return Finish(SetupVerb.Rollback, SetupOutcome.NotApplied, null, null, "previousMissing");

        var runId = _runIds.NewId();
        var source = RunCopySource();
        if (source is null)
            return Finish(SetupVerb.Rollback, SetupOutcome.Failed, null, null, "setupMissing");

        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
        if (!prepare.Succeeded)
            return FinishRefused(SetupVerb.Rollback, prepare);

        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyRollback, RunId: runId));
        if (!apply.Succeeded)
            return FinishRefused(SetupVerb.Rollback, apply);

        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var displacedVersion = _metadata.Read(_paths.PreviousRoot).Version;
        var start = _appStart.StartAndCheck(_paths.AppExePath);
        if (start.Passed)
            return Finish(SetupVerb.Rollback, SetupOutcome.Ok, restoredVersion, displacedVersion);

        _log.Warn($"Rollback target failed the pass rule: {start.FailureReason}; rolling forward.");

        var revert = _broker.Run(new ElevatedRequest(ElevatedJob.Revert, RunId: runId));
        if (!revert.Succeeded)
        {
            return Finish(SetupVerb.Rollback, SetupOutcome.AllFailed, restoredVersion, displacedVersion,
                "revert:" + revert.Detail);
        }

        var forwardVersion = _metadata.Read(_paths.InstallRoot).Version;
        var forwardStart = _appStart.StartAndCheck(_paths.AppExePath);
        return forwardStart.Passed
            ? Finish(SetupVerb.Rollback, SetupOutcome.Failed, restoredVersion, forwardVersion,
                "rollbackTargetFailed:" + start.FailureReason)
            : Finish(SetupVerb.Rollback, SetupOutcome.AllFailed, restoredVersion, forwardVersion,
                "displacedFailed:" + forwardStart.FailureReason);
    }

    /// <summary>
    /// Uninstalls. <c>--quiet</c> never asks and always keeps <c>%APPDATA%\EyeCtrl</c>; a plain
    /// uninstall consults the S6e choice seam (S6a's default keeps the data).
    /// </summary>
    public SetupResult Uninstall(bool quiet, bool? keepData)
    {
        if (MustRunMedium(SetupVerb.Uninstall) is { } medium) return medium;
        if (Blocked(SetupVerb.Uninstall) is { } blocked)
            return blocked;

        bool keep;
        if (quiet)
        {
            keep = true;
        }
        else if (keepData is { } explicitChoice)
        {
            keep = explicitChoice;
        }
        else
        {
            var choice = _uninstallChoice.Ask();
            if (choice == UninstallChoice.Cancel)
                return Finish(SetupVerb.Uninstall, SetupOutcome.Cancelled, null, null, "cancelled");
            keep = choice == UninstallChoice.Keep;
        }

        var runId = _runIds.NewId();
        var source = RunCopySource();
        if (source is null)
            return Finish(SetupVerb.Uninstall, SetupOutcome.Failed, null, null, "setupMissing");

        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
        if (!prepare.Succeeded)
            return FinishRefused(SetupVerb.Uninstall, prepare);

        var uninstall = _broker.Run(new ElevatedRequest(
            ElevatedJob.Uninstall, RunId: runId, KeepData: keep));
        return uninstall.Succeeded
            ? Finish(SetupVerb.Uninstall, SetupOutcome.Uninstalled, null, null,
                keep ? "kept" : "deleted")
            : Finish(SetupVerb.Uninstall, SetupOutcome.Failed, null, null, uninstall.Detail);
    }

    /// <summary>Finishes an interrupted swap (the RunOnce logon path) and starts the App.</summary>
    public SetupResult Repair()
    {
        if (MustRunMedium(SetupVerb.Repair) is { } medium) return medium;
        if (Blocked(SetupVerb.Repair) is { } blocked)
            return blocked;

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot)
            && !_fileSystem.DirectoryExists(_paths.PreviousRoot)
            && !_fileSystem.DirectoryExists(_paths.StagingRoot))
        {
            return Finish(SetupVerb.Repair, SetupOutcome.AllFailed, null, null, "nothingToRepair");
        }

        var runId = CurrentRunId();
        if (runId is null)
        {
            runId = _runIds.NewId();
            var source = RunCopySource();
            if (source is null)
                return Finish(SetupVerb.Repair, SetupOutcome.AllFailed, null, null, "setupMissing");

            var prepare = _broker.Run(new ElevatedRequest(
                ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
            if (!prepare.Succeeded)
                return FinishRefused(SetupVerb.Repair, prepare);
        }

        var repair = _broker.Run(new ElevatedRequest(ElevatedJob.Repair, RunId: runId));
        if (!repair.Succeeded)
            return Finish(SetupVerb.Repair, SetupOutcome.AllFailed, null, null, repair.Detail);

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return Finish(SetupVerb.Repair, SetupOutcome.AllFailed, null, null, "nothingToStart");

        var version = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;
        var start = _appStart.StartAndCheck(_paths.AppExePath);
        return start.Passed
            ? Finish(SetupVerb.Repair, SetupOutcome.Repaired, version, previousVersion)
            : Finish(SetupVerb.Repair, SetupOutcome.AllFailed, version, previousVersion,
                "startFailed:" + start.FailureReason);
    }

    /// <summary>Nothing elevated ever starts EyeCtrl: a public verb in an elevated token is refused
    /// before it changes anything (the internal elevated jobs are dispatched elsewhere).</summary>
    private SetupResult? MustRunMedium(SetupVerb verb)
    {
        if (!_broker.IsElevated)
            return null;

        _log.Warn($"{verb} refused: public verbs must run at medium IL.");
        return Finish(verb, SetupOutcome.Failed, null, null, "mustRunMedium");
    }

    private SetupResult? Blocked(SetupVerb verb)
    {        var policy = _policy.Read();
        if (policy.Allowed)
            return null;

        _log.Warn(
            $"Elevation policy refuses {verb}: ConsentPromptBehaviorAdmin="
            + (policy.ConsentPromptBehaviorAdmin?.ToString() ?? "missing") + ".");
        if (verb != SetupVerb.Uninstall)
            TryRestartInstalled();
        return Finish(verb, SetupOutcome.Blocked, null, null, "elevationPolicy");
    }

    private SetupResult FinishRefused(SetupVerb verb, ElevatedPhaseResult phase)
    {
        var outcome = phase.Status switch
        {
            ElevatedPhaseStatus.Blocked => SetupOutcome.Blocked,
            ElevatedPhaseStatus.VerifyFailed => SetupOutcome.VerifyFailed,
            ElevatedPhaseStatus.NotApplied => SetupOutcome.NotApplied,
            _ => SetupOutcome.Failed,
        };

        // An update, rollback or repair restart the old version after every pre-swap refusal
        // (verify, image check, partial failure). Uninstall never restarts.
        if (verb is SetupVerb.Update or SetupVerb.Rollback or SetupVerb.Repair)
            TryRestartInstalled();

        return Finish(verb, outcome, null, null, phase.Detail);
    }

    private void TryRestartInstalled()
    {
        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return;

        var alreadyRunning = _processes.Snapshot().Any(p =>
            string.Equals(p.ImagePath, _paths.AppExePath, StringComparison.OrdinalIgnoreCase));
        if (!alreadyRunning)
            _appStart.TryStart(_paths.AppExePath);
    }

    private string? RunCopySource()
    {
        if (_fileSystem.FileExists(_paths.InstalledSetupExePath))
            return _paths.InstalledSetupExePath;
        if (_environment.ProcessPath is { } self && _fileSystem.FileExists(self))
            return self;
        if (_fileSystem.FileExists(_paths.PreviousSetupExePath))
            return _paths.PreviousSetupExePath;
        return null;
    }

    /// <summary>The run id of the folder the current exe sits in, when it is already a run copy
    /// (the RunOnce logon path). Reusing it avoids deleting the image that is currently running.</summary>
    private string? CurrentRunId()
    {
        var path = _environment.ProcessPath;
        if (path is null)
            return null;

        var folder = Path.GetDirectoryName(path);
        if (folder is null || !SetupPaths.IsUnder(_paths.RunRoot, folder))
            return null;

        return Path.GetFileName(folder);
    }

    private SetupResult Finish(
        SetupVerb verb, SetupOutcome outcome, string? version, string? previousVersion,
        string? detail = null)
    {
        var result = new SetupResult
        {
            Verb = verb,
            Outcome = outcome,
            Version = version,
            PreviousVersion = previousVersion,
            Time = _clock.UtcNow,
            Detail = detail,
        };

        _recorder.Write(result);
        _log.Info($"{verb}: {outcome}" + (detail is null ? string.Empty : $" ({detail})"));
        return result;
    }
}
