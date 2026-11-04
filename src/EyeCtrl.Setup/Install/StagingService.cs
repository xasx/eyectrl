namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>Why staging refused a package. The strings are stable and machine-readable.</summary>
public static class StagingFailure
{
    public const string CopyFailed = "copyFailed";
    public const string CatalogMissing = "catalogMissing";
    public const string CatalogSignature = "catalogSignature";
    public const string Thumbprint = "thumbprint";
    public const string Hash = "hash";
    public const string Missing = "missing";
    public const string Uncatalogued = "uncatalogued";
    public const string CatalogError = "catalogError";
}

/// <summary>The result of one staging attempt.</summary>
public sealed record StagingResult(bool Ok, string? FailureReason, string? FailureDetail)
{
    /// <summary>A successful stage.</summary>
    public static StagingResult Success { get; } = new(true, null, null);

    /// <summary>A refusal with a stable reason and one diagnostic sentence.</summary>
    public static StagingResult Fail(string reason, string? detail = null) => new(false, reason, detail);
}

/// <summary>
/// Plan §10 step 2: copy the package into the admin-only staging folder AFTER stripping
/// Mark-of-the-Web, then re-check the catalog INSIDE staging — the inbox itself is user-writable
/// and is never trusted. On any mismatch staging is removed again and the system is unchanged.
/// </summary>
public sealed class StagingService
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly ICatalogVerifier _catalog;
    private readonly ISetupLog _log;

    /// <summary>Creates the service.</summary>
    public StagingService(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard,
        ICatalogVerifier catalog, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _catalog = catalog;
        _log = log;
    }

    /// <summary>Copies and verifies a package. The staging folder exists only on success.</summary>
    public StagingResult Stage(string packageDirectory)
    {
        _guard.RequireProgramFilesPath(_paths.StagingRoot);

        try
        {
            // A leftover staging folder is either an interrupted copy or an applied update's
            // source; either way it is not trusted. Remove it before the fresh copy.
            if (_fileSystem.DirectoryExists(_paths.StagingRoot))
                _fileSystem.DeleteDirectory(_paths.StagingRoot, recursive: true);

            _fileSystem.CreateDirectory(_paths.StagingRoot);
            _fileSystem.CopyDirectory(packageDirectory, _paths.StagingRoot, stripZoneIdentifier: true);
        }
        catch (Exception ex)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.CopyFailed, ex.GetType().Name);
        }

        var catalogPath = Path.Combine(_paths.StagingRoot, SetupConstants.CatalogName);
        if (!_fileSystem.FileExists(catalogPath))
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.CatalogMissing);
        }

        var result = _catalog.Verify(catalogPath, _paths.StagingRoot, SetupConstants.PinnedThumbprint);

        // The order matters for the diagnosis: a broken signature is the most specific answer,
        // then a wrong signer, then a tampered file, then an extra file.
        if (!result.SignatureValid)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.CatalogSignature, result.Error);
        }

        if (!result.ThumbprintMatches)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.Thumbprint, result.SignerThumbprint);
        }

        if (result.HashMismatches.Count > 0)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.Hash, string.Join(";", result.HashMismatches));
        }

        if (result.MissingFiles.Count > 0)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.Missing, string.Join(";", result.MissingFiles));
        }

        if (result.UncataloguedFiles.Count > 0)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.Uncatalogued, string.Join(";", result.UncataloguedFiles));
        }

        // Y2: a verifier error (for example an unreadable catalog) must fail closed even when
        // every list happens to be empty.
        if (result.Error is not null)
        {
            TryRemoveStaging();
            return StagingResult.Fail(StagingFailure.CatalogError, result.Error);
        }

        _log.Info("Staging verified: catalog, pinned thumbprint, every hash, no uncatalogued file.");
        return StagingResult.Success;
    }

    private void TryRemoveStaging()
    {
        try
        {
            if (_fileSystem.DirectoryExists(_paths.StagingRoot))
                _fileSystem.DeleteDirectory(_paths.StagingRoot, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not remove the failed staging folder: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Removes a staging folder an aborted update no longer needs (S6c D4). Called by the elevated
    /// side, which owns the admin-only folder. Never throws: the leftover is harmless, the disk is
    /// not.
    /// </summary>
    public void Discard()
    {
        _guard.RequireProgramFilesPath(_paths.StagingRoot);
        TryRemoveStaging();
    }
}
