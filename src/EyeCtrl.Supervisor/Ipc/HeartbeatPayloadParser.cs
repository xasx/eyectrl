namespace EyeCtrl.Supervisor.Ipc;

using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Core.Models;

/// <summary>
/// Pure JSON deserialization for <see cref="HeartbeatPayload"/>. Extracted from
/// <see cref="HeartbeatPipeServer"/> so the camelCase/case-insensitive contract
/// is unit-testable without a named pipe.
/// </summary>
internal static class HeartbeatPayloadParser
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static HeartbeatPayload? TryParse(string json)
    {
        return JsonSerializer.Deserialize<HeartbeatPayload>(json, Options);
    }
}
