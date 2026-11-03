namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>One folder in the update inbox, with its untrusted metadata.</summary>
public sealed record InboxPackage(string Directory, string Name, PackageMetadata Metadata);

/// <summary>
/// Reads the S6b inbox (<c>%LOCALAPPDATA%\EyeCtrl\updates</c>). It is user-writable, so only the
/// catalog inside staging is trusted; this scanner decides what to OFFER.
/// <para>
/// Contract: dot-prefixed folders are ignored (editor/partial-download litter). The newest package
/// is ordered by <c>package.json</c> <c>created</c>, then version, then folder name.
/// </para>
/// </summary>
public sealed class InboxScanner
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IPackageMetadataReader _metadata;

    /// <summary>Creates the scanner.</summary>
    public InboxScanner(
        SetupPaths paths, IFileSystem fileSystem, IPackageMetadataReader metadata)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _metadata = metadata;
    }

    /// <summary>Lists the usable package folders, unsorted.</summary>
    public IReadOnlyList<InboxPackage> List()
    {
        if (!_fileSystem.DirectoryExists(_paths.InboxRoot))
            return [];

        return _fileSystem.EnumerateDirectories(_paths.InboxRoot)
            .Select(directory => new InboxPackage(
                directory, Path.GetFileName(directory), _metadata.Read(directory)))
            .Where(package => !package.Name.StartsWith(".", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The newest package by created, then version, then name; null when none exists.</summary>
    public InboxPackage? Newest() => Newest(List());

    /// <summary>The newest package from a given list; null when it is empty.</summary>
    public static InboxPackage? Newest(IReadOnlyList<InboxPackage> packages) =>
        packages
            .OrderByDescending(p => p.Metadata.Created ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => ParseVersion(p.Metadata.Version) ?? new Version(0, 0))
            .ThenByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// True when <paramref name="packageVersion"/> is a strictly newer semantic version than
    /// <paramref name="installedVersion"/>. The <c>+sha</c> build suffix is not part of the
    /// comparison; unparseable metadata is never offered as an update.
    /// </summary>
    public static bool IsNewerThan(string? packageVersion, string? installedVersion)
    {
        var package = ParseVersion(packageVersion);
        var installed = ParseVersion(installedVersion);
        return package is not null && installed is not null && package > installed;
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var semver = value.Split('+', 2)[0].Split('-', 2)[0];
        return Version.TryParse(semver, out var version) ? version : null;
    }
}
