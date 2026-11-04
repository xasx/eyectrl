namespace EyeCtrl.Setup.Install;

using System.Globalization;
using System.Text.Json;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Reads the package's own <c>package.json</c>. The file is untrusted metadata by contract: it is
/// used for the version shown to the owner and for Apps &amp; Features, never for a path or a trust
/// decision. Missing or malformed fields degrade to null.
/// </summary>
public sealed class PackageMetadataReader : IPackageMetadataReader
{
    private readonly IFileSystem _fileSystem;

    /// <summary>Creates the reader.</summary>
    public PackageMetadataReader(IFileSystem fileSystem) => _fileSystem = fileSystem;

    /// <inheritdoc />
    public PackageMetadata Read(string directory)
    {
        var path = Path.Combine(directory, SetupConstants.PackageMetadataName);
        try
        {
            if (!_fileSystem.FileExists(path))
                return PackageMetadata.Empty;

            using var document = JsonDocument.Parse(_fileSystem.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PackageMetadata.Empty;

            var version = ReadString(root, "version");
            var sha = ReadString(root, "sha");
            DateTimeOffset? created = null;
            if (ReadString(root, "created") is { } createdText
                && DateTimeOffset.TryParse(
                    createdText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                created = parsed;
            }

            return new PackageMetadata(version, sha, created);
        }
        catch (Exception)
        {
            return PackageMetadata.Empty;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
