namespace EyeCtrl.Core.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The Supervisor → App request channel (<c>eyectrl_request</c>). The App is the only process
/// allowed to save the App's settings, so the Supervisor ASKS it to: one newline-JSON
/// <see cref="SettingRequest"/> per connection, answered by one <see cref="SettingRequestAck"/>
/// once the save has completed.
/// </summary>
/// <remarks>
/// <para><b>Wire shape.</b> Request:
/// <c>{"id":"…","kind":"setStartOnBoot","value":true,"restart":false}</c>. Acknowledgement:
/// <c>{"id":"…","status":"saved"}</c>. A new request is a new <see cref="SettingRequestKinds"/>
/// constant with its own JSON <c>value</c> type; the envelope, the status set and the transport
/// stay as they are.</para>
/// <para><b>Delivery is at-least-once.</b> An acknowledgement can be lost after the save (the App
/// exits between the two), so the Supervisor re-sends a parked request until it is acknowledged.
/// Every request must therefore be idempotent: "set X to V", never "toggle X".</para>
/// </remarks>
public static class SettingRequestChannel
{
    /// <summary>The production pipe name. Tests append <c>"_test_" + Guid</c>.</summary>
    public const string PipeName = "eyectrl_request";

    /// <summary>The one JSON contract both ends use: camelCase names and enum tokens, and
    /// case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>One wire line, without its terminating newline.</summary>
    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, JsonOptions);

    /// <summary>Parses one request line. Throws <see cref="JsonException"/> on a malformed line;
    /// the server decides what a bad line means.</summary>
    public static SettingRequest? ParseRequest(string line) =>
        JsonSerializer.Deserialize<SettingRequest>(line, JsonOptions);

    /// <summary>Parses one acknowledgement line. Throws <see cref="JsonException"/> on a malformed
    /// line, including an unknown status token.</summary>
    public static SettingRequestAck? ParseAck(string line) =>
        JsonSerializer.Deserialize<SettingRequestAck>(line, JsonOptions);
}

/// <summary>The request kinds the App understands. An unknown kind is answered
/// <see cref="SettingRequestStatus.Rejected"/>.</summary>
public static class SettingRequestKinds
{
    /// <summary>Sets <c>general.startOnBoot</c>; <c>value</c> is a JSON boolean.</summary>
    public const string SetStartOnBoot = "setStartOnBoot";
}

/// <summary>
/// One request from the Supervisor to the App.
/// </summary>
/// <param name="Id">Correlates the acknowledgement with this request, and the two ends' logs.</param>
/// <param name="Kind">One of <see cref="SettingRequestKinds"/>.</param>
/// <param name="Value">The value to set; its JSON type depends on <paramref name="Kind"/>.</param>
/// <param name="Restart">
/// <c>true</c> only for a live choice the user just made in the Supervisor, for a setting that
/// applies at restart. A parked or pending delivery always sends <c>false</c>: it is saved only,
/// so the App never restarts while the user is working. Kinds that need no restart ignore it.
/// </param>
public sealed record SettingRequest(string Id, string Kind, JsonElement Value, bool Restart)
{
    /// <summary>A <see cref="SettingRequestKinds.SetStartOnBoot"/> request. It never restarts.</summary>
    public static SettingRequest SetStartOnBoot(bool startOnBoot) =>
        new(NewId(), SettingRequestKinds.SetStartOnBoot, JsonSerializer.SerializeToElement(startOnBoot), Restart: false);

    /// <summary>A fresh correlation id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>How the App answered a request.</summary>
public enum SettingRequestStatus
{
    /// <summary>Applied and saved through the App's settings writer.</summary>
    Saved,

    /// <summary>Applied and saved, but the restart the request asked for was not started because
    /// one is already running (T05b). Counts as saved.</summary>
    RestartInProgress,

    /// <summary>The App will never accept this request as sent: an unknown kind, or a value of the
    /// wrong type. Re-sending it cannot help.</summary>
    Rejected,

    /// <summary>A valid request whose save failed. Nothing about the request was wrong, so the
    /// Supervisor keeps it parked and tries again.</summary>
    Failed,
}

/// <summary>The App's answer, sent after the save has completed.</summary>
/// <param name="Id">The <see cref="SettingRequest.Id"/> it answers.</param>
/// <param name="Status">The outcome.</param>
public sealed record SettingRequestAck(string Id, SettingRequestStatus Status)
{
    /// <summary>Whether the requested value is now saved on disk.</summary>
    [JsonIgnore]
    public bool IsSaved => Status is SettingRequestStatus.Saved or SettingRequestStatus.RestartInProgress;
}
