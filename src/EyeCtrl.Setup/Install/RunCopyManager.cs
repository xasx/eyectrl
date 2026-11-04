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
/// VERIFIED <c>EyeCtrl.Setup.exe</c> — and every file a published Setup needs beside it, including
/// WPF's loose natives (S6c D1) and the catalog that proves them — into <c>EyeCtrl.run\&lt;id&gt;\</c>
/// and continues from there. Old run folders are removed on later runs; an uninstall marks its own
/// folder for deletion at the next restart because a process cannot delete its own image.
/// </summary>
public sealed class RunCopyManager
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly IFileSignatureVerifier _signature;
    private readonly ICatalogMemberVerifier _members;
    private readonly IFileHasher _hasher;
    private readonly ISetupLog _log;

    /// <summary>Creates the manager.</summary>
    public RunCopyManager(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard,
        IFileSignatureVerifier signature, ICatalogMemberVerifier members, IFileHasher hasher,
        ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _signature = signature;
        _members = members;
        _hasher = hasher;
        _log = log;
    }

    /// <summary>True for a filesystem-safe run id: letters, digits and hyphens, at most 64 chars.</summary>
    public static bool IsValidRunId(string? runId) =>
        !string.IsNullOrWhiteSpace(runId)
        && runId.Length <= 64
        && runId.All(c => char.IsLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Verifies <paramref name="sourceSetupExe"/> against the pinned thumbprint, removes stale run
    /// folders, and copies the exe and every file its publish needs beside it (S6c D1: WPF's loose
    /// natives, plus the catalog that lets the copy prove itself) into the private folder for
    /// <paramref name="runId"/>.
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

        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(sourceSetupExe));
        if (string.IsNullOrEmpty(sourceFolder))
            return RunCopyResult.Fail("setupMissing");

        var sourceCatalog = Path.Combine(sourceFolder, SetupConstants.CatalogName);
        if (!_fileSystem.FileExists(sourceCatalog))
            return RunCopyResult.Fail("setupCatalogMissing");

        var companions = SetupPayload.FindCompanions(_fileSystem, sourceSetupExe);
        if (SetupPayload.MissingRequired(companions) is { } missing)
            return RunCopyResult.Fail("setupPayloadMissing:" + missing);

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
                return VerifyRunCopy(runId);
            }

            CleanupOldRunFolders(runId);

            if (_fileSystem.DirectoryExists(targetFolder))
                _fileSystem.DeleteDirectory(targetFolder, recursive: true);

            _fileSystem.CreateDirectory(targetFolder);
            _fileSystem.CopyFile(sourceSetupExe, targetExe, stripZoneIdentifier: true);
            foreach (var companion in companions)
            {
                _fileSystem.CopyFile(
                    Path.Combine(sourceFolder, companion),
                    Path.Combine(targetFolder, companion),
                    stripZoneIdentifier: true);
            }

            _fileSystem.CopyFile(sourceCatalog,
                Path.Combine(targetFolder, SetupConstants.CatalogName), stripZoneIdentifier: true);

            // F7 + S6c D1: re-verify the copy that will actually be launched — the exe, the
            // catalog, and every native against that catalog's recorded hash.
            var verified = VerifyRunCopy(runId);
            if (!verified.Ok)
            {
                _fileSystem.DeleteDirectory(targetFolder, recursive: true);
                return RunCopyResult.Fail(verified.FailureReason ?? "runCopySignature");
            }
        }
        catch (Exception ex)
        {
            return RunCopyResult.Fail("runCopy:" + ex.GetType().Name);
        }

        _log.Info($"Run copy prepared in EyeCtrl.run\\{runId} (exe + {companions.Count} natives).");
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

    /// <summary>
    /// Verifies an existing run copy without creating or replacing it: the exe's signature, the
    /// catalog's signature and pinned thumbprint, and every required native against that catalog.
    /// The hand-off child and the RunOnce repair already run from one (S6c D1: the folder itself is
    /// now the unit of trust, not only its exe).
    /// </summary>
    public RunCopyResult VerifyRunCopy(string runId)
    {
        if (!IsValidRunId(runId))
            return RunCopyResult.Fail("invalidRunId");

        var target = _paths.RunSetupExePath(runId);
        _guard.RequireProgramFilesPath(target);
        if (!_fileSystem.FileExists(target))
            return RunCopyResult.Fail("runCopyMissing");

        var check = _signature.Verify(target);
        if (!check.Valid
            || !string.Equals(check.SignerThumbprint, SetupConstants.PinnedThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return RunCopyResult.Fail("runCopySignature");
        }

        var folder = _paths.RunFolder(runId);
        var catalog = Path.Combine(folder, SetupConstants.CatalogName);
        _guard.RequireProgramFilesPath(catalog);
        if (!_fileSystem.FileExists(catalog))
            return RunCopyResult.Fail("runCopyCatalogMissing");

        var catalogCheck = _signature.Verify(catalog);
        if (!catalogCheck.Valid
            || !string.Equals(catalogCheck.SignerThumbprint, SetupConstants.PinnedThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return RunCopyResult.Fail("runCopyCatalog");
        }

        foreach (var companion in SetupPayload.RequiredCompanions)
        {
            var file = Path.Combine(folder, companion);
            _guard.RequireProgramFilesPath(file);
            if (!_fileSystem.FileExists(file))
                return RunCopyResult.Fail("runCopyPayload:" + companion);

            // WinTrust recomputes the file's hash and looks the tag up in the catalog. A copy
            // that was truncated or replaced under the same name cannot pass.
            if (_members.Verify(catalog, file, _hasher.CatalogTag(file)) != 0)
                return RunCopyResult.Fail("runCopyPayload:" + companion);
        }

        return RunCopyResult.Success(target);
    }
}
