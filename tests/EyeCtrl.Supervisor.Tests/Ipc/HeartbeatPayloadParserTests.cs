namespace EyeCtrl.Supervisor.Tests.Ipc;

using System.Text.Json;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using NFluent;
using Xunit;

public class HeartbeatPayloadParserTests
{
    [Fact]
    public void Parse_CamelCaseJson_DeserializesCorrectly()
    {
        var json = """{"pid":1234,"startedAtUtc":"2025-01-01T00:00:00Z","isConnected":true,"isPaused":false,"lastFrameUtc":"2025-01-01T00:00:01Z","shuttingDown":false,"restartRequested":false}""";

        var payload = HeartbeatPayloadParser.Parse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(1234);
        Check.That(payload.IsConnected).IsTrue();
        Check.That(payload.IsPaused).IsFalse();
        Check.That(payload.ShuttingDown).IsFalse();
    }

    [Fact]
    public void Parse_PascalCaseJson_DeserializesCorrectly()
    {
        // Case-insensitive safety net: either casing on the wire must work.
        var json = """{"Pid":5678,"StartedAtUtc":"2025-01-01T00:00:00Z","IsConnected":false,"IsPaused":true,"LastFrameUtc":"2025-01-01T00:00:01Z","ShuttingDown":true,"RestartRequested":true}""";

        var payload = HeartbeatPayloadParser.Parse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(5678);
        Check.That(payload.IsPaused).IsTrue();
        Check.That(payload.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsTrue();
    }

    [Fact]
    public void Parse_UnknownFields_Ignored()
    {
        var json = """{"pid":1,"startedAtUtc":"2025-01-01T00:00:00Z","isConnected":false,"isPaused":false,"lastFrameUtc":"2025-01-01T00:00:01Z","shuttingDown":false,"extraField":"ignored"}""";

        var payload = HeartbeatPayloadParser.Parse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(1);
    }

    [Fact]
    public void Parse_EmptyInput_ThrowsJsonException()
    {
        // The pipe server's `catch { continue; }` around this call is load-bearing — a malformed
        // line must skip without killing the connection. Pin the exception type it relies on.
        Check.ThatCode(() => HeartbeatPayloadParser.Parse("")).Throws<JsonException>();
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsJsonException()
    {
        Check.ThatCode(() => HeartbeatPayloadParser.Parse("{\"pid\":")).Throws<JsonException>();
    }

    [Fact]
    public void Parse_JsonNullLiteral_ReturnsNull()
    {
        // Distinct from the throwing paths: a literal `null` line deserializes to null, which the
        // pipe server drops via its own `if (payload == null) continue;`.
        Check.That(HeartbeatPayloadParser.Parse("null")).IsNull();
    }
}
