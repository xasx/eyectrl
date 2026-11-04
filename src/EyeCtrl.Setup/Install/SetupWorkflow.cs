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
    private readonly SupervisionStartPlanner _planner;
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
        SupervisionStartPlanner planner,
        IFileSystem fileSystem, IProcessControl processes, IUninstallChoice uninstallChoice,
        IRunIdGenerator runIds, IPackageMetadataReader metadata, IResultRecorder recorder,
        ISetupEnvironment environment, ISetupClock clock, ISetupLog log)
    {
        _paths = paths;
        _policy = policy;
        _broker = broker;
        _appStart = appStart;
        _planner = planner;
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
        if (HandOff(SetupVerb.Update, ["update", packageDirectory]) is { } handoff)
            return handoff;

        var runId = _runIds.NewId();

        var currentRunId = CurrentRunId();
        if (currentRunId is not null)
            runId = currentRunId;

        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: packageDirectory, RunId: runId,
            ReuseRunCopy: currentRunId is not null));
        if (!prepare.Succeeded)
            return FinishRefused(SetupVerb.Update, prepare);

        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: runId));
        if (!apply.Succeeded)
        {
            if (apply.Detail == "childStillRunning")
            {
                // G1: the timed-out elevated child may still be renaming. Do not repair, revert
                // or start anything; the RunOnce/journal repair finishes at the next logon.
                return Finish(SetupVerb.Update, SetupOutcome.Failed, null, null, "childStillRunning");
            }

            if (apply.Status == ElevatedPhaseStatus.Failed)
            {
                var repaired = TryRepairAfterFailedApply(runId);
                if (repaired is not null)
                    return repaired;
            }

            return FinishRefused(SetupVerb.Update, apply);
        }

        var newVersion = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;

        var start = StartChecked();
        if (start.Passed)
        {
            return Finish(SetupVerb.Update, SetupOutcome.Ok, newVersion, previousVersion);
        }

        _log.Warn($"New version failed the pass rule: {start.FailureReason}; rolling back.");

        // Q12: a NEW version still rolls back on a UIAccess failure. Only when there is nothing
        // to roll back to, and the new version is alive, does it become the last resort.
        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot)
            && start.StillAlive && start.UiAccessOnlyFailure)
        {
            return Finish(SetupVerb.Update, SetupOutcome.Degraded, newVersion, null,
                "degraded:" + start.FailureReason);
        }

        return RevertAndCheck(
            SetupVerb.Update, newVersion, previousVersion, runId, start.FailureReason);
    }

    /// <summary>
    /// A partial apply is finished by <c>repair</c>, and the repaired version then faces the same
    /// pass rule (or a checked revert). It is never started un-checked.
    /// </summary>
    private SetupResult? TryRepairAfterFailedApply(string runId)
    {
        var repair = _broker.Run(new ElevatedRequest(ElevatedJob.Repair, RunId: runId));
        if (!repair.Succeeded)
        {
            _log.Warn($"Repair after a failed apply failed: {repair.Detail}; trying the previous version.");
            return RevertAndCheck(
                SetupVerb.Update, null, _metadata.Read(_paths.PreviousRoot).Version, runId,
                "repair:" + repair.Detail);
        }

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return null;

        var version = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;
        var start = StartChecked();
        if (start.Passed)
            return Finish(SetupVerb.Update, SetupOutcome.Ok, version, previousVersion, "repairCompleted");

        _log.Warn($"Repaired version failed the pass rule: {start.FailureReason}; reverting.");

        if (start.StillAlive && start.UiAccessOnlyFailure && !_fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            return Finish(SetupVerb.Update, SetupOutcome.Degraded, version, null,
                "degraded:" + start.FailureReason);
        }

        return RevertAndCheck(
            SetupVerb.Update, version, previousVersion, runId, "repairedVersionFailed:" + start.FailureReason);
    }

    /// <summary>Reverts to the previous version and pass-checks it. Both failing is the only
    /// <see cref="SetupOutcome.AllFailed"/> path; a previous version that is alive but only lacks
    /// UIAccess is Q12's <see cref="SetupOutcome.Degraded"/> last resort.</summary>
    private SetupResult RevertAndCheck(
        SetupVerb verb, string? attemptedVersion, string? previousVersion, string? runId,
        string? failureReason, SetupOutcome passedOutcome = SetupOutcome.RolledBack)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || !_fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, previousVersion,
                "noPreviousToRevert:" + failureReason);
        }

        var revert = _broker.Run(new ElevatedRequest(ElevatedJob.Revert, RunId: runId));
        if (!revert.Succeeded)
        {
            return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, previousVersion,
                "revert:" + revert.Detail);
        }

        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var start = StartChecked();
        if (start.Passed)
        {
            return Finish(verb, passedOutcome, attemptedVersion, restoredVersion,
                "newVersionFailed:" + failureReason);
        }

        if (start.StillAlive && start.UiAccessOnlyFailure)
        {
            // Q12: keep the last-resort version running; it names the running version.
            return Finish(verb, SetupOutcome.Degraded, restoredVersion, attemptedVersion,
                "degraded:" + start.FailureReason);
        }

        return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, restoredVersion,
            "previousFailed:" + start.FailureReason);
    }

    /// <summary>Restores <c>EyeCtrl.previous</c> and pass-checks it; a failed target rolls the
    /// rollback back to the version it displaced.</summary>
    public SetupResult Rollback()
    {
        if (MustRunMedium(SetupVerb.Rollback) is { } medium) return medium;
        if (Blocked(SetupVerb.Rollback) is { } blocked)
            return blocked;
        if (HandOff(SetupVerb.Rollback, ["rollback"]) is { } handoff)
            return handoff;

        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot))
            return Finish(SetupVerb.Rollback, SetupOutcome.NotApplied, null, null, "previousMissing");

        var currentRunId = CurrentRunId();
        var runId = currentRunId ?? _runIds.NewId();
        if (currentRunId is null)
        {
            var source = RunCopySource();
            if (source is null)
                return Finish(SetupVerb.Rollback, SetupOutcome.Failed, null, null, "setupMissing");

            var prepare = _broker.Run(new ElevatedRequest(
                ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
            if (!prepare.Succeeded)
                return FinishRefused(SetupVerb.Rollback, prepare);
        }

        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyRollback, RunId: runId));
        if (!apply.Succeeded)
            return FinishRefused(SetupVerb.Rollback, apply);

        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var displacedVersion = _metadata.Read(_paths.PreviousRoot).Version;
        var start = StartChecked();
        if (start.Passed)
            return Finish(SetupVerb.Rollback, SetupOutcome.Ok, restoredVersion, displacedVersion);

        _log.Warn($"Rollback target failed the pass rule: {start.FailureReason}; rolling forward.");
        return RevertAndCheck(
            SetupVerb.Rollback, restoredVersion, displacedVersion, runId,
            "rollbackTargetFailed:" + start.FailureReason, SetupOutcome.Failed);
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
        var relaunchArguments = new List<string> { "uninstall" };
        if (quiet)
            relaunchArguments.Add("--quiet");
        else if (keepData is true)
            relaunchArguments.Add("--keep");
        else if (keepData is false)
            relaunchArguments.Add("--delete");
        if (HandOff(SetupVerb.Uninstall, relaunchArguments) is { } handoff)
            return handoff;

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

        var currentRunId = CurrentRunId();
        var runId = currentRunId ?? _runIds.NewId();
        if (currentRunId is null)
        {
            var source = RunCopySource();
            if (source is null)
                return Finish(SetupVerb.Uninstall, SetupOutcome.Failed, null, null, "setupMissing");

            var prepare = _broker.Run(new ElevatedRequest(
                ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
            if (!prepare.Succeeded)
                return FinishRefused(SetupVerb.Uninstall, prepare);
        }

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
        if (HandOff(SetupVerb.Repair, ["repair"]) is { } handoff)
            return handoff;

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
        var start = StartChecked();
        if (start.Passed)
            return Finish(SetupVerb.Repair, SetupOutcome.Repaired, version, previousVersion);

        if (start.StillAlive && start.UiAccessOnlyFailure && !_fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            return Finish(SetupVerb.Repair, SetupOutcome.Degraded, version, null,
                "degraded:" + start.FailureReason);
        }

        return RevertAndCheck(
            SetupVerb.Repair, version, previousVersion, runId,
            "repairedVersionFailed:" + start.FailureReason);
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

    /// <summary>
    /// A medium verb started from one of the four folders a swap renames cannot survive that swap.
    /// It prepares a verified run copy, starts the same verb from it at medium IL, and exits; the
    /// child (now outside EyeCtrl\) does the work. This is plan §10's "Setup never runs from a
    /// folder it renames".
    /// </summary>
    private SetupResult? HandOff(SetupVerb verb, IReadOnlyList<string> arguments)
    {
        var self = _environment.ProcessPath;
        if (self is null || !_paths.IsInRenamedFolder(self))
            return null;

        var runId = _runIds.NewId();
        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: self));
        if (!prepare.Succeeded)
            return FinishRefused(verb, prepare);

        var runCopy = _paths.RunSetupExePath(runId);
        var pid = _processes.Start(runCopy, arguments);
        if (pid is null)
            return Finish(verb, SetupOutcome.Failed, null, null, "relaunchFailed");

        _log.Info($"{verb}: relaunched the medium verb from the run copy (PID {pid}).");
        return Finish(verb, SetupOutcome.HandedOff, null, null, "relaunchedFromRunCopy", pid);
    }

    private SetupResult? Blocked(SetupVerb verb)
    {
        var policy = _policy.Read();
        if (policy.Allowed)
            return null;

        _log.Warn(
            $"Elevation policy refuses {verb}: ConsentPromptBehaviorAdmin="
            + (policy.ConsentPromptBehaviorAdmin?.ToString() ?? "missing") + ".");
        if (verb != SetupVerb.Uninstall)
        {
            var start = TryStartInstalledIfNotRunning();
            if (start is { Passed: false, UiAccessOnlyFailure: true, StillAlive: true })
            {
                return Finish(verb, SetupOutcome.Blocked, null, null, "elevationPolicy:appDegraded");
            }

            if (start is { Passed: false })
            {
                return Finish(verb, SetupOutcome.AllFailed, null, null,
                    "restartFailed:" + start.FailureReason);
            }
        }

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

        // An update, rollback or repair must end in a pass-ruled version (or a checked revert).
        // A pre-swap refusal leaves the old version intact; start and check it. Uninstall never
        // restarts.
        if (verb is SetupVerb.Update or SetupVerb.Rollback or SetupVerb.Repair)
        {
            var start = TryStartInstalledIfNotRunning();
            if (start is { Passed: false, UiAccessOnlyFailure: true, StillAlive: true })
            {
                return Finish(verb, outcome, null, null,
                    (phase.Detail is null ? string.Empty : phase.Detail + ":") + "appDegraded");
            }

            if (start is { Passed: false })
            {
                return Finish(verb, SetupOutcome.AllFailed, null, null,
                    "restartFailed:" + start.FailureReason);
            }
        }

        return Finish(verb, outcome, null, null, phase.Detail);
    }

    /// <summary>Starts the installed App with the real supervision × boot plan in the log, then
    /// runs the three-part pass rule.</summary>
    private EyeCtrl.Setup.Install.AppStartOutcome StartChecked()
    {
        var plan = _planner.Plan();
        _log.Info(
            $"Start plan: app={plan.StartAppOnce} supervisor={plan.StartSupervisor} " +
            $"runKey={plan.WriteStartOnBootRunKey} appStartsSupervisor={plan.AppWillStartSupervisor}");
        return _appStart.StartAndCheck(_paths.AppExePath);
    }

    /// <summary>Starts the installed App and runs the pass rule, unless it is already running (a
    /// second instance would hit the single-instance guard and fail spuriously).</summary>
    private EyeCtrl.Setup.Install.AppStartOutcome? TryStartInstalledIfNotRunning()
    {
        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return null;

        var alreadyRunning = _processes.Snapshot().Any(p =>
            string.Equals(p.ImagePath, _paths.AppExePath, StringComparison.OrdinalIgnoreCase));
        return alreadyRunning ? null : StartChecked();
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
        string? detail = null, int? processId = null)
    {
        var result = new SetupResult
        {
            Verb = verb,
            Outcome = outcome,
            Version = version,
            PreviousVersion = previousVersion,
            Time = _clock.UtcNow,
            Detail = detail,
            ProcessId = processId,
        };

        if (outcome != SetupOutcome.HandedOff)
            _recorder.Write(result);
        _log.Info($"{verb}: {outcome}" + (detail is null ? string.Empty : $" ({detail})"));
        return result;
    }
}

