namespace EyeCtrl.Core.Tests.Models;

using System.Text.Json;
using EyeCtrl.Core.Models;

/// <summary>
/// The request channel's wire contract, shared verbatim by the App's server and the Supervisor's
/// client. The literal JSON is pinned: both ends parse these exact shapes, and T05b adds a kind
/// without changing them.
/// </summary>
public class SettingRequestTests
{
    [Fact]
    public void SetStartOnBoot_HasThePinnedWireShape()
    {
        var request = SettingRequest.SetStartOnBoot(true) with { Id = "abc" };

        Check.That(SettingRequestChannel.Serialize(request))
            .IsEqualTo("""{"id":"abc","kind":"setStartOnBoot","value":true,"restart":false}""");
    }

    [Fact]
    public void SetStartOnBoot_NeverAsksForARestart_AndGetsAFreshId()
    {
        var first = SettingRequest.SetStartOnBoot(false);
        var second = SettingRequest.SetStartOnBoot(false);

        Check.That(first.Restart).IsFalse();
        Check.That(first.Value.GetBoolean()).IsFalse();
        Check.That(first.Id).IsNotEqualTo(second.Id);
    }

    [Fact]
    public void ARequest_RoundTrips()
    {
        var parsed = SettingRequestChannel.ParseRequest(
            """{"id":"x1","kind":"setUiLanguage","value":"german","restart":true}""")!;

        Check.That(parsed.Id).IsEqualTo("x1");
        Check.That(parsed.Kind).IsEqualTo("setUiLanguage");
        Check.That(parsed.Value.GetString()).IsEqualTo("german");
        Check.That(parsed.Restart).IsTrue();
    }

    [Fact]
    public void AMissingValue_ParsesAsUndefined_SoTheHandlerCanRejectIt()
    {
        var parsed = SettingRequestChannel.ParseRequest("""{"id":"x","kind":"setStartOnBoot"}""")!;

        Check.That(parsed.Value.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        Check.That(parsed.Restart).IsFalse();
    }

    [Theory]
    [InlineData(SettingRequestStatus.Saved, "saved", true)]
    [InlineData(SettingRequestStatus.RestartInProgress, "restartInProgress", true)]
    [InlineData(SettingRequestStatus.Rejected, "rejected", false)]
    [InlineData(SettingRequestStatus.Failed, "failed", false)]
    public void AnAcknowledgement_HasThePinnedWireShape(SettingRequestStatus status, string token, bool saved)
    {
        var ack = new SettingRequestAck("abc", status);
        var line = SettingRequestChannel.Serialize(ack);

        Check.That(line).IsEqualTo($$"""{"id":"abc","status":"{{token}}"}""");
        Check.That(SettingRequestChannel.ParseAck(line)).IsEqualTo(ack);
        Check.That(ack.IsSaved).IsEqualTo(saved);
    }

    [Theory]
    [InlineData("""{"id":"abc","status":"done"}""")]   // unknown token
    [InlineData("""{"id":"abc","status":0}""")]        // integers are not status tokens
    public void AnUnknownStatus_DoesNotParse(string line)
    {
        JsonException? thrown = null;
        try { SettingRequestChannel.ParseAck(line); }
        catch (JsonException ex) { thrown = ex; }

        Check.That(thrown).IsNotNull();
    }
}
