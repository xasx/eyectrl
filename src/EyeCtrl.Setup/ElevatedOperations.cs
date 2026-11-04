namespace EyeCtrl.Setup;

using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;

/// <summary>
/// The elevated phases. Every one re-reads the elevation policy before touching anything, then
/// runs exactly one job and returns a status the medium workflow maps to an outcome. Nothing here
/// starts the App: an elevated process must never launch EyeCtrl.
/// </summary>
public sealed class ElevatedOperations
{
    private readonly SetupPaths _paths;
    private readonly IElevationPolicy _policy;
    private readonly RunCopyManager _runCopies;
    private readonly StagingService _staging;
    private readonly StopCoordinator _stop;
    private readonly InstallStateMachine _state;
    private readonly AppsAndFeaturesWriter _appsAndFeatures;
    private readonly IAppDataDeletion _appDataDeletion;
    private readonly IElevatedPhaseResultStore _phaseResults;
    private readonly ISetupLog _log;

    /// <summary>Creates the phases.</summary>
    public ElevatedOperations(
        SetupPaths paths, IElevationPolicy policy, RunCopyManager runCopies, StagingService staging,
        StopCoordinator stop, InstallStateMachine state, AppsAndFeaturesWriter appsAndFeatures,
        IAppDataDeletion appDataDeletion, IElevatedPhaseResultStore phaseResults, ISetupLog log)
    {
        _paths = paths;
        _policy = policy;
        _runCopies = runCopies;
        _staging = staging;
        _stop = stop;
        _state = state;
        _appsAndFeatures = appsAndFeatures;
        _appDataDeletion = appDataDeletion;
        _phaseResults = phaseResults;
        _log = log;
    }

    /// <summary>Runs one elevated job and records its result for the medium instance.</summary>
    public ElevatedPhaseResult Execute(ElevatedRequest request)
    {
        var result = ExecuteCore(request);
        _phaseResults.Write(request, result);
        return result;
    }

    private ElevatedPhaseResult ExecuteCore(ElevatedRequest request)
    {
        if (Blocked() is { } blocked)
            return blocked;

        return request.Job switch
        {
            ElevatedJob.PrepareUpdate => PrepareUpdate(request),
            ElevatedJob.ApplyUpdate => ApplyUpdate(request),
            ElevatedJob.PrepareRunCopy => PrepareRunCopy(request),
            ElevatedJob.ApplyRollback => ApplyRollback(request),
            ElevatedJob.Revert => Revert(request),
            ElevatedJob.Uninstall => Uninstall(request),
            ElevatedJob.Repair => Repair(request),
            _ => new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "unknownJob"),
        };
    }

    private ElevatedPhaseResult PrepareUpdate(ElevatedRequest request)
    {
        if (request.PackageDirectory is not { } packageDirectory || request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingArguments");

        // F7: verify inside staging FIRST, then take the run copy from the verified staging
        // folder (never from the user-writable inbox), and re-verify the copy itself.
        var staging = _staging.Stage(packageDirectory);
        if (!staging.Ok)
        {
            _log.Warn($"Staging refused: {staging.FailureReason} {staging.FailureDetail}");
            return new ElevatedPhaseResult(ElevatedPhaseStatus.VerifyFailed, staging.FailureReason);
        }

        var stagedSetup = Path.Combine(_paths.StagingRoot, SetupConstants.SetupExeName);
        if (request.ReuseRunCopy)
        {
            // G4: the hand-off child already runs from a verified run copy; reuse it instead of
            // creating a second EyeCtrl.run\<id> folder (and a second runas).
            var existing = _runCopies.VerifyRunCopy(runId);
            if (!existing.Ok)
            {
                _log.Warn($"Existing run copy refused: {existing.FailureReason}");
                // S6c D4: the stage happened before this check; the update never applied.
                _staging.Discard();
                _runCopies.MarkForDeletionAtReboot(runId);
                return new ElevatedPhaseResult(MapRunCopyFailure(existing.FailureReason), existing.FailureReason);
            }

            return ElevatedPhaseResult.Ok;
        }

        var runCopy = _runCopies.Prepare(stagedSetup, runId);
        if (!runCopy.Ok)
        {
            _log.Warn($"Run copy refused: {runCopy.FailureReason}");
            // S6c D4: nothing will apply this package; do not leave its staging folder behind.
            _staging.Discard();
            return new ElevatedPhaseResult(MapRunCopyFailure(runCopy.FailureReason), runCopy.FailureReason);
        }

        return ElevatedPhaseResult.Ok;
    }

    private ElevatedPhaseResult ApplyUpdate(ElevatedRequest request)
    {
        if (request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingRunId");

        var stop = _stop.StopForSwap(SetupConstants.CleanExitGrace);
        if (!stop.Ok)
        {
            // S6c D4: a refusal changes nothing, so neither staging nor the run copy stays.
            CleanupRefusedUpdate(runId);
            return new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, "image:" + stop.Blocker);
        }

        var swap = _state.ApplyUpdate(_paths.RunSetupExePath(runId));
        if (swap.Failure == SwapFailure.NotApplied)
            CleanupRefusedUpdate(runId);

        return MapSwap(swap);
    }

    /// <summary>
    /// S6c D4: a refused update must not leave its staged package and a fresh run copy behind. The
    /// staging folder goes now. This child runs from the run copy, so it cannot delete its own
    /// image; the reboot registration removes it (and the next run's CleanupOldRunFolders also
    /// takes it).
    /// </summary>
    private void CleanupRefusedUpdate(string runId)
    {
        _staging.Discard();
        _runCopies.MarkForDeletionAtReboot(runId);
    }

    private ElevatedPhaseResult PrepareRunCopy(ElevatedRequest request)
    {
        if (request.RunId is not { } runId || request.SourceSetupExe is not { } source)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingArguments");

        var runCopy = _runCopies.Prepare(source, runId);
        if (!runCopy.Ok)
        {
            _log.Warn($"Run copy refused: {runCopy.FailureReason}");
            return new ElevatedPhaseResult(MapRunCopyFailure(runCopy.FailureReason), runCopy.FailureReason);
        }

        return ElevatedPhaseResult.Ok;
    }

    private ElevatedPhaseResult ApplyRollback(ElevatedRequest request)
    {
        if (request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingRunId");

        var stop = _stop.StopForSwap(SetupConstants.CleanExitGrace);
        if (!stop.Ok)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, "image:" + stop.Blocker);

        return MapSwap(_state.ApplyOwnerRollback(_paths.RunSetupExePath(runId)));
    }

    private ElevatedPhaseResult Revert(ElevatedRequest request)
    {
        if (request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingRunId");

        // The App just failed its pass rule and may still be alive; this is the kill path.
        var stop = _stop.StopForSwap(SetupConstants.CleanExitGrace, forceKillPath: true);
        if (!stop.Ok)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, "image:" + stop.Blocker);

        return MapSwap(_state.RevertFailedUpdate(_paths.RunSetupExePath(runId)));
    }

    private ElevatedPhaseResult Uninstall(ElevatedRequest request)
    {
        if (request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingRunId");

        var stop = _stop.StopForSwap(SetupConstants.CleanExitGrace, forceKillPath: true);
        if (!stop.Ok)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, "image:" + stop.Blocker);

        try
        {
            // Program first, then the launch values, then the Apps & Features entry. If a folder
            // cannot be removed, the entry stays so the owner can retry and the data is kept.
            _state.RemoveInstalledFolders();
            _state.RemoveRunValues();
            _appsAndFeatures.Remove();
        }
        catch (Exception ex)
        {
            _log.Error($"Uninstall removal failed: {ex.GetType().Name}");
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "remove:" + ex.GetType().Name);
        }

        // This process runs from the run copy; it cannot delete its own image.
        _runCopies.MarkForDeletionAtReboot(runId);

        if (!request.KeepData)
        {
            // Only after the program was removed, and only after S6e's confirmed delete.
            var deletion = _appDataDeletion.Delete();
            if (!deletion.Succeeded)
            {
                _log.Warn($"App-data delete incomplete: {deletion.FailedFiles.Count} file(s) remained.");
                return new ElevatedPhaseResult(
                    ElevatedPhaseStatus.Failed,
                    "dataIncomplete:" + string.Join(";", deletion.FailedFiles));
            }
        }

        return ElevatedPhaseResult.Ok;
    }

    private ElevatedPhaseResult Repair(ElevatedRequest request)
    {
        if (request.RunId is not { } runId)
            return new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "missingRunId");

        return MapSwap(_state.Repair(_paths.RunSetupExePath(runId)));
    }

    private ElevatedPhaseResult? Blocked()
    {
        var policy = _policy.Read();
        if (policy.Allowed)
            return null;

        _log.Warn(
            $"Elevation policy refuses: ConsentPromptBehaviorAdmin={policy.ConsentPromptBehaviorAdmin?.ToString() ?? "missing"}.");
        return new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "elevationPolicy");
    }

    private static ElevatedPhaseStatus MapRunCopyFailure(string? reason) =>
        reason is "invalidRunId" or "setupMissing" or "setupThumbprint" or "runCopySignature"
        || reason?.StartsWith("setupSignature:", StringComparison.Ordinal) == true
            ? ElevatedPhaseStatus.VerifyFailed
            : ElevatedPhaseStatus.Failed;

    private static ElevatedPhaseResult MapSwap(SwapOutcome swap) => swap.Ok
        ? ElevatedPhaseResult.Ok
        : swap.Failure == SwapFailure.NotApplied
            ? new ElevatedPhaseResult(ElevatedPhaseStatus.NotApplied, swap.Detail)
            : new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, swap.Detail);
}
