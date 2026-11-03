namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;
using Microsoft.Win32;

/// <summary>How badly a swap failed: <see cref="NotApplied"/> means the disk is back to the state
/// before the attempt; <see cref="Failed"/> means a partial state repair must finish.</summary>
public enum SwapFailure
{
    None,
    NotApplied,
    Failed,
}

/// <summary>The state machine's answer.</summary>
public sealed record SwapOutcome(bool Ok, SwapFailure Failure, string? Detail, bool Changed = true)
{
    /// <summary>A committed transition.</summary>
    public static SwapOutcome Success(bool changed = true) => new(true, SwapFailure.None, null, changed);

    /// <summary>Nothing changed.</summary>
    public static SwapOutcome NotApplied(string detail) => new(false, SwapFailure.NotApplied, detail);

    /// <summary>A partial state exists; repair/RunOnce must finish it.</summary>
    public static SwapOutcome Failed(string detail) => new(false, SwapFailure.Failed, detail);
}

/// <summary>One read-only view of the five folders.</summary>
public sealed record InstallSnapshot(
    bool InstallExists,
    bool PreviousExists,
    bool StagingExists,
    bool FailedExists,
    string? InstalledVersion,
    string? PreviousVersion,
    string State)
{
    /// <summary>True when repair has something to promote or finish.</summary>
    public bool Repairable => InstallExists || StagingExists || PreviousExists;
}

/// <summary>
/// Plan §10 step 4/6: the swap, rollback and repair state machine. Every rename pair is bracketed
/// by the HKCU <c>RunOnce\EyeCtrlRepair</c> value, so a power loss between renames is finished by
/// <c>repair</c> at the next logon. Every failure either undoes the first rename or leaves the
/// RunOnce value in place for repair — there is deliberately no path that leaves neither.
/// </summary>
public sealed class InstallStateMachine
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IRegistryStore _registry;
    private readonly PathGuard _guard;
    private readonly AppsAndFeaturesWriter _appsAndFeatures;
    private readonly IPackageMetadataReader _metadata;
    private readonly ISetupLog _log;

    /// <summary>Creates the state machine.</summary>
    public InstallStateMachine(
        SetupPaths paths, IFileSystem fileSystem, IRegistryStore registry, PathGuard guard,
        AppsAndFeaturesWriter appsAndFeatures, IPackageMetadataReader metadata, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _registry = registry;
        _guard = guard;
        _appsAndFeatures = appsAndFeatures;
        _metadata = metadata;
        _log = log;
    }

    /// <summary>The current layout, read-only.</summary>
    public InstallSnapshot Snapshot()
    {
        var install = _fileSystem.DirectoryExists(_paths.InstallRoot);
        var previous = _fileSystem.DirectoryExists(_paths.PreviousRoot);
        var staging = _fileSystem.DirectoryExists(_paths.StagingRoot);
        var failed = _fileSystem.DirectoryExists(_paths.FailedRoot);

        var state = install ? "installed"
            : staging ? "stagingPending"
            : previous ? "previousOnly"
            : "missing";

        return new InstallSnapshot(
            install, previous, staging, failed,
            install ? _metadata.Read(_paths.InstallRoot).Version : null,
            previous ? _metadata.Read(_paths.PreviousRoot).Version : null,
            state);
    }

    /// <summary>
    /// Update: <c>EyeCtrl</c> → <c>EyeCtrl.previous</c>, then <c>EyeCtrl.staging</c> →
    /// <c>EyeCtrl</c>. A failed second rename undoes the first.
    /// </summary>
    public SwapOutcome ApplyUpdate(string runCopyPath)
    {
        RequireAllInstallPaths();
        if (!_fileSystem.DirectoryExists(_paths.StagingRoot))
            return SwapOutcome.NotApplied("stagingMissing");

        // A running previous version is only a fallback; a new update replaces it. Removing it
        // before the first rename keeps the rename atomic instead of trying to overwrite.
        if (_fileSystem.DirectoryExists(_paths.InstallRoot)
            && _fileSystem.DirectoryExists(_paths.PreviousRoot))
        {
            try
            {
                _fileSystem.DeleteDirectory(_paths.PreviousRoot, recursive: true);
            }
            catch (Exception ex)
            {
                return SwapOutcome.NotApplied("previousCleanup:" + ex.GetType().Name);
            }
        }

        SetRepairRunOnce(runCopyPath);

        var movedInstall = false;
        if (_fileSystem.DirectoryExists(_paths.InstallRoot))
        {
            try
            {
                _fileSystem.MoveDirectory(_paths.InstallRoot, _paths.PreviousRoot);
                movedInstall = true;
            }
            catch (Exception ex)
            {
                ClearRepairRunOnce();
                return SwapOutcome.NotApplied("firstRename:" + ex.GetType().Name);
            }
        }

        try
        {
            _fileSystem.MoveDirectory(_paths.StagingRoot, _paths.InstallRoot);
        }
        catch (Exception ex)
        {
            var undone = !movedInstall || TryUndo(_paths.PreviousRoot, _paths.InstallRoot);
            if (undone)
            {
                ClearRepairRunOnce();
                return SwapOutcome.NotApplied("secondRename:" + ex.GetType().Name);
            }

            // The undo failed too: keep RunOnce so repair can still finish at logon.
            return SwapOutcome.Failed("secondRenameAndUndoFailed:" + ex.GetType().Name);
        }

        ClearRepairRunOnce();
        RegisterAppsAndFeatures();
        _log.Info("Update swapped in.");
        return SwapOutcome.Success();
    }

    /// <summary>
    /// The owner's rollback: the previous folder becomes the install, and the displaced current
    /// version becomes the new previous so the move can be reversed again. Uses staging as the
    /// transfer folder but never as a source of trust — both sides were already verified.
    /// </summary>
    public SwapOutcome ApplyOwnerRollback(string runCopyPath)
    {
        RequireAllInstallPaths();
        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot))
            return SwapOutcome.NotApplied("previousMissing");
        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return SwapOutcome.NotApplied("installMissing");

        if (_fileSystem.DirectoryExists(_paths.StagingRoot))
        {
            try
            {
                _fileSystem.DeleteDirectory(_paths.StagingRoot, recursive: true);
            }
            catch (Exception ex)
            {
                return SwapOutcome.NotApplied("stagingCleanup:" + ex.GetType().Name);
            }
        }

        SetRepairRunOnce(runCopyPath);

        try
        {
            _fileSystem.MoveDirectory(_paths.PreviousRoot, _paths.StagingRoot);
        }
        catch (Exception ex)
        {
            ClearRepairRunOnce();
            return SwapOutcome.NotApplied("firstRename:" + ex.GetType().Name);
        }

        try
        {
            _fileSystem.MoveDirectory(_paths.InstallRoot, _paths.PreviousRoot);
        }
        catch (Exception ex)
        {
            var undone = TryUndo(_paths.StagingRoot, _paths.PreviousRoot);
            if (undone)
            {
                ClearRepairRunOnce();
                return SwapOutcome.NotApplied("secondRename:" + ex.GetType().Name);
            }

            return SwapOutcome.Failed("secondRenameAndUndoFailed:" + ex.GetType().Name);
        }

        try
        {
            _fileSystem.MoveDirectory(_paths.StagingRoot, _paths.InstallRoot);
        }
        catch (Exception ex)
        {
            // Put the two versions back where they were. If even that fails, repair promotes
            // staging (the requested target) over previous.
            var first = TryUndo(_paths.PreviousRoot, _paths.InstallRoot);
            var second = first && TryUndo(_paths.StagingRoot, _paths.PreviousRoot);
            if (second)
            {
                ClearRepairRunOnce();
                return SwapOutcome.NotApplied("thirdRename:" + ex.GetType().Name);
            }

            return SwapOutcome.Failed("thirdRenameAndUndoFailed:" + ex.GetType().Name);
        }

        ClearRepairRunOnce();
        RegisterAppsAndFeatures();
        _log.Info("Previous version restored.");
        return SwapOutcome.Success();
    }

    /// <summary>
    /// Automatic rollback after a new version failed the pass rule: the broken install goes to
    /// <c>EyeCtrl.failed</c> and <c>EyeCtrl.previous</c> comes back.
    /// </summary>
    public SwapOutcome RevertFailedUpdate(string runCopyPath)
    {
        RequireAllInstallPaths();
        if (!_fileSystem.DirectoryExists(_paths.PreviousRoot))
            return SwapOutcome.NotApplied("previousMissing");
        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return SwapOutcome.NotApplied("installMissing");

        if (_fileSystem.DirectoryExists(_paths.FailedRoot))
        {
            try
            {
                _fileSystem.DeleteDirectory(_paths.FailedRoot, recursive: true);
            }
            catch (Exception ex)
            {
                return SwapOutcome.NotApplied("failedCleanup:" + ex.GetType().Name);
            }
        }

        SetRepairRunOnce(runCopyPath);

        try
        {
            _fileSystem.MoveDirectory(_paths.InstallRoot, _paths.FailedRoot);
        }
        catch (Exception ex)
        {
            ClearRepairRunOnce();
            return SwapOutcome.NotApplied("firstRename:" + ex.GetType().Name);
        }

        try
        {
            _fileSystem.MoveDirectory(_paths.PreviousRoot, _paths.InstallRoot);
        }
        catch (Exception ex)
        {
            var undone = TryUndo(_paths.FailedRoot, _paths.InstallRoot);
            if (undone)
            {
                ClearRepairRunOnce();
                return SwapOutcome.NotApplied("secondRename:" + ex.GetType().Name);
            }

            return SwapOutcome.Failed("secondRenameAndUndoFailed:" + ex.GetType().Name);
        }

        ClearRepairRunOnce();
        RegisterAppsAndFeatures();
        _log.Info("Failed update reverted to the previous version.");
        return SwapOutcome.Success();
    }

    /// <summary>
    /// Repair after a crash or at the RunOnce logon. If no install exists, staging is promoted
    /// first (it is the requested target in both an update and a rollback), then previous. With an
    /// install present, a leftover staging folder is an uncommitted copy and is discarded.
    /// </summary>
    public SwapOutcome Repair(string runCopyPath)
    {
        RequireAllInstallPaths();
        var changed = false;

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
        {
            if (_fileSystem.DirectoryExists(_paths.StagingRoot))
            {
                try
                {
                    _fileSystem.MoveDirectory(_paths.StagingRoot, _paths.InstallRoot);
                    changed = true;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Repair could not promote staging: {ex.GetType().Name}");
                }
            }

            if (!_fileSystem.DirectoryExists(_paths.InstallRoot)
                && _fileSystem.DirectoryExists(_paths.PreviousRoot))
            {
                try
                {
                    _fileSystem.MoveDirectory(_paths.PreviousRoot, _paths.InstallRoot);
                    changed = true;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Repair could not promote previous: {ex.GetType().Name}");
                }
            }
        }

        if (!_fileSystem.DirectoryExists(_paths.InstallRoot))
            return SwapOutcome.Failed("nothingToRepair");

        if (_fileSystem.DirectoryExists(_paths.StagingRoot))
        {
            try
            {
                _fileSystem.DeleteDirectory(_paths.StagingRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _log.Warn($"Repair could not discard staging: {ex.GetType().Name}");
            }
        }

        ClearRepairRunOnce();
        RegisterAppsAndFeatures();
        _log.Info(changed ? "Repair completed an interrupted swap." : "Repair found a startable install.");
        return SwapOutcome.Success(changed);
    }

    /// <summary>Removes the installed folders and the run folders (uninstall, elevated).</summary>
    public void RemoveInstalledFolders()
    {
        RequireAllInstallPaths();
        foreach (var folder in new[]
                 {
                     _paths.InstallRoot, _paths.PreviousRoot,
                     _paths.StagingRoot, _paths.FailedRoot,
                 })
        {
            if (_fileSystem.DirectoryExists(folder))
                _fileSystem.DeleteDirectory(folder, recursive: true);
        }
    }

    /// <summary>Removes every HKCU Run/RunOnce value whose name starts with <c>EyeCtrl</c>.</summary>
    public void RemoveRunValues()
    {
        foreach (var (hive, key) in new[]
                 {
                     (RegistryHive.CurrentUser, SetupConstants.RunKeyPath),
                     (RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath),
                 })
        {
            foreach (var name in _registry.ReadValueNames(hive, key))
            {
                if (!name.StartsWith("EyeCtrl", StringComparison.OrdinalIgnoreCase))
                    continue;
                _guard.RequireRunValue(hive, key, name);
                _registry.DeleteValue(hive, key, name);
            }
        }
    }

    private void SetRepairRunOnce(string runCopyPath)
    {
        _guard.RequireRunValue(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath, SetupConstants.RepairRunOnceName);
        _registry.WriteString(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, $"\"{runCopyPath}\" repair");
    }

    private void ClearRepairRunOnce()
    {
        _guard.RequireRunValue(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath, SetupConstants.RepairRunOnceName);
        _registry.DeleteValue(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath, SetupConstants.RepairRunOnceName);
    }

    private bool TryUndo(string from, string to)
    {
        try
        {
            _fileSystem.MoveDirectory(from, to);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Undo {(Path.GetFileName(from))} -> {Path.GetFileName(to)} failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void RegisterAppsAndFeatures() =>
        _appsAndFeatures.Register(_metadata.Read(_paths.InstallRoot).Version);

    private void RequireAllInstallPaths()
    {
        _guard.RequireProgramFilesPath(_paths.InstallRoot);
        _guard.RequireProgramFilesPath(_paths.PreviousRoot);
        _guard.RequireProgramFilesPath(_paths.StagingRoot);
        _guard.RequireProgramFilesPath(_paths.FailedRoot);
    }
}
