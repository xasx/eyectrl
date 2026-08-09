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
    // CamelCase to match the app's HeartbeatPublisher emitter; case-insensitive so either PascalCase
    // or camelCase on the wire deserializes correctly. JsonStringEnumConverter included for safety
    // (HeartbeatPayload currently has no enums, but this keeps the options aligned with the app).
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Deserializes one heartbeat line. Named <c>Parse</c>, not <c>TryParse</c>: it propagates
    /// <see cref="JsonException"/> on a malformed line rather than swallowing it. The pipe server
    /// decides what a bad line means (skip it, keep the connection alive), and that decision stays
    /// at the call site.
    /// </summary>
    public static HeartbeatPayload? Parse(string json)
    {
        return JsonSerializer.Deserialize<HeartbeatPayload>(json, Options);
    }
}
