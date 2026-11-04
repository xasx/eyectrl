namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>The run-copy result: either a verified path, or the reason the copy was refused.</summary>
public sealed record RunCopyResult(bool Ok, string? RunCopyPath, string? FailureReason)
{
    /// <summary>A verified run copy.</summary>
    public static RunCopyResult Success(string path) => new(true, path, null);

    /// <summary>A refusal with a stable reason.</summary>
    public static RunCopyResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Plan §10: Setup never runs from a folder it renames. Before any swap or uninstall it copies a
/// VERIFIED <c>EyeCtrl.Setup.exe</c> into <c>EyeCtrl.run\&lt;id&gt;\</c> and continues from there.
/// Old run folders are removed on later runs; an uninstall marks its own folder for deletion at
/// the next restart because a process cannot delete its own image.
/// </summary>
public sealed class RunCopyManager
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly IFileSignatureVerifier _signature;
    private readonly ISetupLog _log;

    /// <summary>Creates the manager.</summary>
    public RunCopyManager(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard,
        IFileSignatureVerifier signature, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _signature = signature;
        _log = log;
    }

    /// <summary>True for a filesystem-safe run id: letters, digits and hyphens, at most 64 chars.</summary>
    public static bool IsValidRunId(string? runId) =>
        !string.IsNullOrWhiteSpace(runId)
        && runId.Length <= 64
        && runId.All(c => char.IsLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Verifies <paramref name="sourceSetupExe"/> against the pinned thumbprint, removes stale run
    /// folders, and copies the exe into the private folder for <paramref name="runId"/>.
    /// </summary>
    public RunCopyResult Prepare(string sourceSetupExe, string runId)
    {
        if (!IsValidRunId(runId))
            return RunCopyResult.Fail("invalidRunId");

        if (!_fileSystem.FileExists(sourceSetupExe))
            return RunCopyResult.Fail("setupMissing");

        var check = _signature.Verify(sourceSetupExe);
        if (!check.Valid)
            return RunCopyResult.Fail("setupSignature:" + (check.Error ?? "invalid"));
        if (!string.Equals(check.SignerThumbprint, SetupConstants.PinnedThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return RunCopyResult.Fail("setupThumbprint");
        }

        var targetFolder = _paths.RunFolder(runId);
        var targetExe = _paths.RunSetupExePath(runId);
        _guard.RequireProgramFilesPath(targetFolder);
        _guard.RequireProgramFilesPath(targetExe);

        try
        {
            // F2: the current process may already be the run copy for this id; verify it and
            // return without deleting the folder that holds the running image.
            if (string.Equals(
                    Path.GetFullPath(sourceSetupExe), Path.GetFullPath(targetExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                var existing = _signature.Verify(targetExe);
                return existing.Valid
                       && string.Equals(existing.SignerThumbprint, SetupConstants.PinnedThumbprint,
                           StringComparison.OrdinalIgnoreCase)
                    ? RunCopyResult.Success(targetExe)
                    : RunCopyResult.Fail("runCopySignature");
            }

            CleanupOldRunFolders(runId);

            if (_fileSystem.DirectoryExists(targetFolder))
                _fileSystem.DeleteDirectory(targetFolder, recursive: true);

            _fileSystem.CreateDirectory(targetFolder);
            _fileSystem.CopyFile(sourceSetupExe, targetExe, stripZoneIdentifier: true);

            // F7: re-verify the copy that will actually be launched, in the admin-only folder.
            var copied = _signature.Verify(targetExe);
            if (!copied.Valid
                || !string.Equals(copied.SignerThumbprint, SetupConstants.PinnedThumbprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                _fileSystem.DeleteDirectory(targetFolder, recursive: true);
                return RunCopyResult.Fail("runCopySignature");
            }
        }
        catch (Exception ex)
        {
            return RunCopyResult.Fail("runCopy:" + ex.GetType().Name);
        }

        _log.Info($"Run copy prepared in EyeCtrl.run\\{runId}.");
        return RunCopyResult.Success(targetExe);
    }

    /// <summary>Deletes every run folder except the current one. Failures are logged, never fatal.</summary>
    public void CleanupOldRunFolders(string currentRunId)
    {
        if (!_fileSystem.DirectoryExists(_paths.RunRoot))
            return;

        foreach (var folder in _fileSystem.EnumerateDirectories(_paths.RunRoot))
        {
            var name = Path.GetFileName(folder);
            if (string.Equals(name, currentRunId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                _fileSystem.DeleteDirectory(folder, recursive: true);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove old run folder '{name}': {ex.GetType().Name}");
            }
        }
    }

    /// <summary>Registers the named run folder for deletion at the next restart (uninstall path).</summary>
    public void MarkForDeletionAtReboot(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        var folder = _paths.RunFolder(runId);
        _guard.RequireProgramFilesPath(folder);
        if (!_fileSystem.DirectoryExists(folder))
            return;

        _fileSystem.MarkForDeletionAtReboot(folder);
    }
}
