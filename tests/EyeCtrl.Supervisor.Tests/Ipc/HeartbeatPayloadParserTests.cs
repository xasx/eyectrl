namespace EyeCtrl.Supervisor.Tests.Ipc;

using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using NFluent;
using Xunit;

public class HeartbeatPayloadParserTests
{
    [Fact]
    public void TryParse_CamelCaseJson_DeserializesCorrectly()
    {
        var json = """{"pid":1234,"startedAtUtc":"2025-01-01T00:00:00Z","isConnected":true,"isPaused":false,"lastFrameUtc":"2025-01-01T00:00:01Z","shuttingDown":false,"restartRequested":false}""";

        var payload = HeartbeatPayloadParser.TryParse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(1234);
        Check.That(payload.IsConnected).IsTrue();
        Check.That(payload.IsPaused).IsFalse();
        Check.That(payload.ShuttingDown).IsFalse();
    }

    [Fact]
    public void TryParse_PascalCaseJson_DeserializesCorrectly()
    {
        // Case-insensitive safety net: either casing on the wire must work.
        var json = """{"Pid":5678,"StartedAtUtc":"2025-01-01T00:00:00Z","IsConnected":false,"IsPaused":true,"LastFrameUtc":"2025-01-01T00:00:01Z","ShuttingDown":true,"RestartRequested":true}""";

        var payload = HeartbeatPayloadParser.TryParse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(5678);
        Check.That(payload.IsPaused).IsTrue();
        Check.That(payload.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsTrue();
    }

    [Fact]
    public void TryParse_UnknownFields_Ignored()
    {
        var json = """{"pid":1,"startedAtUtc":"2025-01-01T00:00:00Z","isConnected":false,"isPaused":false,"lastFrameUtc":"2025-01-01T00:00:01Z","shuttingDown":false,"extraField":"ignored"}""";

        var payload = HeartbeatPayloadParser.TryParse(json);

        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(1);
    }

    [Fact]
    public void TryParse_InvalidJson_ThrowsOrReturnsNull()
    {
        // Empty or malformed JSON: System.Text.Json throws JsonException on empty input.
        // The pipe server wraps this in try/catch (skip line), so the behavior here is
        // "does not corrupt state" — we just verify it doesn't return a valid payload.
        try
        {
            var payload = HeartbeatPayloadParser.TryParse("");
            // If it doesn't throw, it must return null.
            Check.That(payload).IsNull();
        }
        catch (System.Text.Json.JsonException)
        {
            // Expected: empty input is a JSON read error.
        }
    }
}
