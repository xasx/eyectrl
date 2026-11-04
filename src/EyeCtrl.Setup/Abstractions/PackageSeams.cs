namespace EyeCtrl.Setup.Abstractions;

/// <summary>One file's Authenticode check.</summary>
public readonly record struct FileSignatureCheck(bool Valid, string? SignerThumbprint, string? Error);

/// <summary>Verifies one file's Authenticode signature and returns its signer thumbprint.</summary>
public interface IFileSignatureVerifier
{
    /// <summary>Checks the file; a missing/unreadable/unsigned file is <c>Valid=false</c>.</summary>
    FileSignatureCheck Verify(string filePath);
}

/// <summary>
/// The staging re-check: the catalog's signature, the pinned signer thumbprint, every catalogued
/// file's hash, and the rule that no file in staging is uncatalogued.
/// </summary>
public sealed record CatalogVerificationResult(
    bool SignatureValid,
    string? SignerThumbprint,
    bool ThumbprintMatches,
    IReadOnlyList<string> HashMismatches,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> UncataloguedFiles,
    string? Error);

/// <summary>Verifies a signed <c>New-FileCatalog</c> catalog and every file it covers.</summary>
public interface ICatalogVerifier
{
    /// <summary>Verifies <paramref name="catalogPath"/> over <paramref name="directory"/>.</summary>
    CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint);
}

/// <summary>The untrusted <c>package.json</c> metadata. Never used for a path or a trust decision.</summary>
public sealed record PackageMetadata(string? Version, string? Sha, DateTimeOffset? Created)
{
    /// <summary>No metadata file (or an unreadable one).</summary>
    public static PackageMetadata Empty { get; } = new(null, null, null);
}

/// <summary>Reads the package's own metadata for display and the Apps &amp; Features version.</summary>
public interface IPackageMetadataReader
{
    /// <summary>Reads <c>&lt;directory&gt;\package.json</c>, tolerantly.</summary>
    PackageMetadata Read(string directory);
}
