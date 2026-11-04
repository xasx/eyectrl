namespace EyeCtrl.Setup.Install;

using System.Text.Json;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Reads the two install-time switches from <c>%APPDATA%\EyeCtrl\settings.json</c>. Read-only and
/// tolerant: a missing or damaged file means both are off, which is the safe answer (Setup starts
/// only the App either way).
/// </summary>
public sealed class SettingsSnapshotReader : ISettingsSnapshotReader
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;

    /// <summary>Creates the reader.</summary>
    public SettingsSnapshotReader(SetupPaths paths, IFileSystem fileSystem)
    {
        _paths = paths;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public SupervisionBootState Read()
    {
        try
        {
            if (!_fileSystem.FileExists(_paths.SettingsPath))
                return default;

            using var document = JsonDocument.Parse(_fileSystem.ReadAllText(_paths.SettingsPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return default;

            var supervision = ReadBool(root, "supervision", "enabled");
            var startOnBoot = ReadBool(root, "general", "startOnBoot");
            return new SupervisionBootState(supervision, startOnBoot);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static bool ReadBool(JsonElement root, string section, string name) =>
        root.TryGetProperty(section, out var sectionElement)
        && sectionElement.ValueKind == JsonValueKind.Object
        && sectionElement.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();
}
