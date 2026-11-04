namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Display;

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
    private readonly ISetupDisplayFactory _displayFactory;

    /// <summary>Creates the workflow.</summary>
    public SetupWorkflow(
        SetupPaths paths, IElevationPolicy policy, IElevationBroker broker, AppStartService appStart,
        SupervisionStartPlanner planner,
        IFileSystem fileSystem, IProcessControl processes, IUninstallChoice uninstallChoice,
        IRunIdGenerator runIds, IPackageMetadataReader metadata, IResultRecorder recorder,
        ISetupEnvironment environment, ISetupClock clock, ISetupLog log,
        ISetupDisplayFactory? displayFactory = null)
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
        _displayFactory = displayFactory ?? NullSetupDisplayFactory.Instance;
    }

    /// <summary>Installs the package and applies the three-part pass rule, rolling back on a
    /// failure. The old version is restarted on any pre-swap refusal.</summary>
    public SetupResult Update(string packageDirectory)
    {
        if (MustRunMedium(SetupVerb.Update) is { } medium) return medium;

        // Created before the hand-off, but only shown on first use: a hand-off child that exits
        // without touching it shows nothing, while a policy-blocked run-copy prepare can still
        // show uninstall.needsHelp from the installed exe.
        using var display = _displayFactory.Create(SetupVerb.Update);

        if (HandOff(SetupVerb.Update, ["update", packageDirectory], display) is { } handoff)
            return handoff;
        if (Blocked(SetupVerb.Update, display) is { } blocked)
            return blocked;

        var runId = _runIds.NewId();

        var currentRunId = CurrentRunId();
        if (currentRunId is not null)
            runId = currentRunId;

        display.Stage(SetupStage.Verify);
        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: packageDirectory, RunId: runId,
            ReuseRunCopy: currentRunId is not null));
        if (!prepare.Succeeded)
            return FinishRefused(SetupVerb.Update, prepare, display);

        display.Stage(SetupStage.Stop);
        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: runId));
        if (!apply.Succeeded)
        {
            if (apply.Detail == "childStillRunning")
            {
                // G1: the timed-out elevated child may still be renaming. Do not repair, revert
                // or start anything; the RunOnce/journal repair finishes at the next logon.
                return Finish(SetupVerb.Update, SetupOutcome.Failed, null, null, "childStillRunning", display: display);
            }

            if (apply.Status == ElevatedPhaseStatus.Failed)
            {
                var repaired = TryRepairAfterFailedApply(runId, display);
                if (repaired is not null)
                    return repaired;
            }

            return FinishRefused(SetupVerb.Update, apply, display);
        }

        display.Stage(SetupStage.Swap);
        var newVersion = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;

        display.Stage(SetupStage.Start);
        var start = StartChecked();
        if (start.Passed)
        {
            return Finish(SetupVerb.Update, SetupOutcome.Ok, newVersion, previousVersion, display: display);
        }

        _log.Warn($"New version failed the pass rule: {start.FailureReason}; rolling back.");

        // Q12: a NEW version still rolls back on a UIAccess failure. Only when there is nothing
        // to roll back to, and the new version is alive, does it become the last resort.
        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot)
            && start.StillAlive && start.UiAccessOnlyFailure)
        {
            return Finish(SetupVerb.Update, SetupOutcome.Degraded, newVersion, null,
                "degraded:" + start.FailureReason, display: display);
        }

        return RevertAndCheck(
            SetupVerb.Update, newVersion, previousVersion, runId, start.FailureReason,
            attemptedStart: start, display: display);
    }

    /// <summary>
    /// A partial apply is finished by <c>repair</c>, and the repaired version then faces the same
    /// pass rule (or a checked revert). It is never started un-checked.
    /// </summary>
    private SetupResult? TryRepairAfterFailedApply(string runId, ISetupDisplay? display)
    {
        var repair = _broker.Run(new ElevatedRequest(ElevatedJob.Repair, RunId: runId));
        if (!repair.Succeeded)
        {
            if (repair.Detail == "childStillRunning")
            {
                // G1: the stopped child may still be renaming; do not launch the revert.
                return Finish(SetupVerb.Update, SetupOutcome.Failed, null, null, "childStillRunning", display: display);
            }

            _log.Warn($"Repair after a failed apply failed: {repair.Detail}; trying the previous version.");
            return RevertAndCheck(
                SetupVerb.Update, null, _metadata.Read(_paths.PreviousRoot).Version, runId,
                "repair:" + repair.Detail, display: display);
        }

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return null;

        var version = _metadata.Read(_paths.InstallRoot).Version;
        var previousVersion = _metadata.Read(_paths.PreviousRoot).Version;
        var start = StartChecked();
        if (start.Passed)
            return Finish(SetupVerb.Update, SetupOutcome.Ok, version, previousVersion, "repairCompleted", display: display);

        _log.Warn($"Repaired version failed the pass rule: {start.FailureReason}; reverting.");

        if (start.StillAlive && start.UiAccessOnlyFailure && !_fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            return Finish(SetupVerb.Update, SetupOutcome.Degraded, version, null,
                "degraded:" + start.FailureReason, display: display);
        }

        return RevertAndCheck(
            SetupVerb.Update, version, previousVersion, runId, "repairedVersionFailed:" + start.FailureReason,
            attemptedStart: start, display: display);
    }

    /// <summary>Reverts to the previous version and pass-checks it. Both failing is the only
    /// <see cref="SetupOutcome.AllFailed"/> path; a previous version that is alive but only lacks
    /// UIAccess is Q12's <see cref="SetupOutcome.Degraded"/> last resort.</summary>
    private SetupResult RevertAndCheck(
        SetupVerb verb, string? attemptedVersion, string? previousVersion, string? runId,
        string? failureReason, SetupOutcome passedOutcome = SetupOutcome.RolledBack,
        AppStartOutcome? attemptedStart = null, ISetupDisplay? display = null)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || !_fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, previousVersion,
                "noPreviousToRevert:" + failureReason, display: display);
        }

        var revert = _broker.Run(new ElevatedRequest(ElevatedJob.Revert, RunId: runId));
        if (!revert.Succeeded)
        {
            // H2: the revert was refused before its kill path. If the attempted version is still
            // alive and only lacks UIAccess, it is what runs; report it as degraded, not allFailed.
            if (attemptedStart is { StillAlive: true, UiAccessOnlyFailure: true, ProcessId: { } pid }
                && _processes.IsAlive(pid))
            {
                return Finish(verb, SetupOutcome.Degraded, attemptedVersion, previousVersion,
                    "degradedRevertRefused:" + revert.Detail, display: display);
            }

            return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, previousVersion,
                "revert:" + revert.Detail, display: display);
        }

        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var start = StartChecked();
        if (start.Passed)
        {
            return Finish(verb, passedOutcome, attemptedVersion, restoredVersion,
                "newVersionFailed:" + failureReason, display: display);
        }

        if (start.StillAlive && start.UiAccessOnlyFailure)
        {
            // Q12: keep the last-resort version running; it names the running version.
            return Finish(verb, SetupOutcome.Degraded, restoredVersion, attemptedVersion,
                "degraded:" + start.FailureReason, display: display);
        }

        return Finish(verb, SetupOutcome.AllFailed, attemptedVersion, restoredVersion,
            "previousFailed:" + start.FailureReason, display: display);
    }

    /// <summary>Restores <c>EyeCtrl.previous</c> and pass-checks it; a failed target rolls the
    /// rollback back to the version it displaced.</summary>
    public SetupResult Rollback()
    {
        if (MustRunMedium(SetupVerb.Rollback) is { } medium) return medium;

        using var display = _displayFactory.Create(SetupVerb.Rollback);

        if (HandOff(SetupVerb.Rollback, ["rollback"], display) is { } handoff)
            return handoff;
        if (Blocked(SetupVerb.Rollback, display) is { } blocked)
            return blocked;

        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot))
            return Finish(SetupVerb.Rollback, SetupOutcome.NotApplied, null, null, "previousMissing", display: display);

        var currentRunId = CurrentRunId();
        var runId = currentRunId ?? _runIds.NewId();
        display.Stage(SetupStage.Verify);
        if (currentRunId is null)
        {
            var source = RunCopySource();
            if (source is null)
                return Finish(SetupVerb.Rollback, SetupOutcome.Failed, null, null, "setupMissing", display: display);

            var prepare = _broker.Run(new ElevatedRequest(
                ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
            if (!prepare.Succeeded)
                return FinishRefused(SetupVerb.Rollback, prepare, display);
        }

        display.Stage(SetupStage.Stop);
        var apply = _broker.Run(new ElevatedRequest(ElevatedJob.ApplyRollback, RunId: runId));
        if (!apply.Succeeded)
            return FinishRefused(SetupVerb.Rollback, apply, display);

        display.Stage(SetupStage.Swap);
        var restoredVersion = _metadata.Read(_paths.InstallRoot).Version;
        var displacedVersion = _metadata.Read(_paths.PreviousRoot).Version;
        display.Stage(SetupStage.Start);
        var start = StartChecked();
        if (start.Passed)
            return Finish(SetupVerb.Rollback, SetupOutcome.Ok, restoredVersion, displacedVersion, display: display);

        _log.Warn($"Rollback target failed the pass rule: {start.FailureReason}; rolling forward.");
        return RevertAndCheck(
            SetupVerb.Rollback, restoredVersion, displacedVersion, runId,
            "rollbackTargetFailed:" + start.FailureReason, SetupOutcome.Failed,
            attemptedStart: start, display: display);
    }

    /// <summary>
    /// Uninstalls. <c>--quiet</c> never asks and always keeps <c>%APPDATA%\EyeCtrl</c>; a plain
    /// uninstall consults the S6e choice seam (S6a's default keeps the data).
    /// </summary>
    public SetupResult Uninstall(bool quiet, bool? keepData)
    {
        if (MustRunMedium(SetupVerb.Uninstall) is { } medium) return medium;

        // --quiet is scripted: no window, not even a result. The App's uninstall hand-off passes
        // --keep/--delete and shows the progress/result.
        using var display = quiet ? null : _displayFactory.Create(SetupVerb.Uninstall);

        var relaunchArguments = new List<string> { "uninstall" };
        if (quiet)
            relaunchArguments.Add("--quiet");
        else if (keepData is true)
            relaunchArguments.Add("--keep");
        else if (keepData is false)
            relaunchArguments.Add("--delete");
        if (HandOff(SetupVerb.Uninstall, relaunchArguments, display) is { } handoff)
            return handoff;
        if (Blocked(SetupVerb.Uninstall, display) is { } blocked)
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
                return Finish(SetupVerb.Uninstall, SetupOutcome.Cancelled, null, null, "cancelled", display: display);
            keep = choice == UninstallChoice.Keep;
        }

        var currentRunId = CurrentRunId();
        var runId = currentRunId ?? _runIds.NewId();
        if (currentRunId is null)
        {
            var source = RunCopySource();
            if (source is null)
                return Finish(SetupVerb.Uninstall, SetupOutcome.Failed, null, null, "setupMissing", display: display);

            var prepare = _broker.Run(new ElevatedRequest(
                ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: source));
            if (!prepare.Succeeded)
                return FinishRefused(SetupVerb.Uninstall, prepare, display);
        }

        display?.Stage(SetupStage.Stop);
        var uninstall = _broker.Run(new ElevatedRequest(
            ElevatedJob.Uninstall, RunId: runId, KeepData: keep));
        return uninstall.Succeeded
            ? Finish(SetupVerb.Uninstall, SetupOutcome.Uninstalled, null, null,
                keep ? "kept" : "deleted", display: display)
            : Finish(SetupVerb.Uninstall, SetupOutcome.Failed, null, null, uninstall.Detail, display: display);
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
            "repairedVersionFailed:" + start.FailureReason, attemptedStart: start);
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
    private SetupResult? HandOff(
        SetupVerb verb, IReadOnlyList<string> arguments, ISetupDisplay? display = null)
    {
        var self = _environment.ProcessPath;
        if (self is null || !_paths.IsInRenamedFolder(self))
            return null;

        var runId = _runIds.NewId();
        var prepare = _broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareRunCopy, RunId: runId, SourceSetupExe: self));
        if (!prepare.Succeeded)
            return FinishRefused(verb, prepare, display);

        var runCopy = _paths.RunSetupExePath(runId);
        var pid = _processes.Start(runCopy, arguments);
        if (pid is null)
            return Finish(verb, SetupOutcome.Failed, null, null, "relaunchFailed", display: display);

        _log.Info($"{verb}: relaunched the medium verb from the run copy (PID {pid}).");
        return Finish(verb, SetupOutcome.HandedOff, null, null, "relaunchedFromRunCopy", pid, display);
    }

    private SetupResult? Blocked(SetupVerb verb, ISetupDisplay? display = null)
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
                return Finish(verb, SetupOutcome.Blocked, null, null, "elevationPolicy:appDegraded", display: display);
            }

            if (start is { Passed: false })
            {
                return Finish(verb, SetupOutcome.AllFailed, null, null,
                    "restartFailed:" + start.FailureReason, display: display);
            }
        }

        return Finish(verb, SetupOutcome.Blocked, null, null, "elevationPolicy", display: display);
    }

    private SetupResult FinishRefused(SetupVerb verb, ElevatedPhaseResult phase, ISetupDisplay? display = null)
    {
        var outcome = phase.Status switch
        {
            ElevatedPhaseStatus.Blocked => SetupOutcome.Blocked,
            ElevatedPhaseStatus.VerifyFailed => SetupOutcome.VerifyFailed,
            ElevatedPhaseStatus.NotApplied => SetupOutcome.NotApplied,
            _ => SetupOutcome.Failed,
        };

        // H1: a stopped child that still runs may still be renaming EyeCtrl / EyeCtrl.previous.
        // Never restart the App from EyeCtrl while it does. This covers every caller, including
        // the rollback apply (and the prepare children, where a restart would be only wasteful).
        if (phase.Detail == "childStillRunning")
            return Finish(verb, outcome, null, null, phase.Detail, display: display);

        // An update, rollback or repair must end in a pass-ruled version (or a checked revert).
        // A pre-swap refusal leaves the old version intact; start and check it. Uninstall never
        // restarts.
        if (verb is SetupVerb.Update or SetupVerb.Rollback or SetupVerb.Repair)
        {
            var start = TryStartInstalledIfNotRunning();
            if (start is { Passed: false, UiAccessOnlyFailure: true, StillAlive: true })
            {
                return Finish(verb, outcome, null, null,
                    (phase.Detail is null ? string.Empty : phase.Detail + ":") + "appDegraded", display: display);
            }

            if (start is { Passed: false })
            {
                return Finish(verb, SetupOutcome.AllFailed, null, null,
                    "restartFailed:" + start.FailureReason, display: display);
            }
        }

        return Finish(verb, outcome, null, null, phase.Detail, display: display);
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
        string? detail = null, int? processId = null, ISetupDisplay? display = null)
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
        {
            // Record first: a display failure must never lose the terminal result.
            _recorder.Write(result);
            display?.Finish(result);
        }

        _log.Info($"{verb}: {outcome}" + (detail is null ? string.Empty : $" ({detail})"));
        return result;
    }
}

