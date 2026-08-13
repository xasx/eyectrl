namespace EyeCtrl.App.Startup;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What a scoped instance recorded about itself, written to the profile directory before anything
/// can act on input so a harness can read it and abort while nothing is on screen.
/// <para>
/// Never written when there is no instance id: a default launch must leave no new artefact.
/// </para>
/// </summary>
public sealed record InstanceManifest(
    string InstanceId,
    int ProcessId,
    DateTime StartedAtUtc,
    bool TestMode,
    IReadOnlyList<InstanceResource> Resources,
    IReadOnlyList<string> Refused)
{
    /// <summary>Manifest file name inside the profile directory.</summary>
    public const string FileName = "instance.json";

    /// <summary>camelCase, matching every other JSON emitter in this codebase.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Writes the manifest. Creates the directory tree if needed and lets any other failure
    /// propagate: a harness that cannot verify scoping must not go on to drive real input, so this
    /// is fatal by design rather than best-effort.
    /// <para>
    /// <c>WriteAllText</c> truncates, which is load-bearing: a scoped profile can survive a
    /// previous run whose teardown failed, and a stale manifest would satisfy the harness's
    /// file-exists wait while describing a process that is gone.
    /// </para>
    /// </summary>
    public static void Write(string profileDirectory, InstanceManifest manifest)
    {
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(
            Path.Combine(profileDirectory, FileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }
}
