namespace EyeCtrl.Setup;

using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Setup.Abstractions;

/// <summary>The JSON shape shared by the result record and the status output.</summary>
public static class SetupJson
{
    /// <summary>camelCase, indented, enums as their camelCase names (the S6f vocabulary).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>
/// Writes <c>%APPDATA%\EyeCtrl\setup-last.json</c>: the outcome, both versions and the time. It is
/// the only file Setup may write under app data (the path guard enforces exactly that), and S6f
/// reads it for the one-time notice.
/// </summary>
public sealed class ResultRecorder : IResultRecorder
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly ISetupLog _log;

    /// <summary>Creates the recorder.</summary>
    public ResultRecorder(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _log = log;
    }

    /// <inheritdoc />
    public void Write(SetupResult result)
    {
        _guard.RequireResultRecordPath(_paths.RecordPath);
        try
        {
            var json = JsonSerializer.Serialize(result, SetupJson.Options);
            _guard.RequireResultRecordDirectory(_paths.AppDataRoot);
            _fileSystem.CreateDirectory(_paths.AppDataRoot);
            _fileSystem.WriteAllText(_paths.RecordPath, json);
        }
        catch (Exception ex)
        {
            // The result screen must still appear; a record that cannot be written only costs
            // the next-start notice.
            _log.Warn($"Could not write setup-last.json: {ex.GetType().Name}");
        }
    }

    /// <inheritdoc />
    public SetupResult? TryReadLast()
    {
        try
        {
            if (!_fileSystem.FileExists(_paths.RecordPath))
                return null;

            var json = _fileSystem.ReadAllText(_paths.RecordPath);
            return JsonSerializer.Deserialize<SetupResult>(json, SetupJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
