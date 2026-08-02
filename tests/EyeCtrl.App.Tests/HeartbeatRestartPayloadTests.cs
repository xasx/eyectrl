namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// Each pipe-binding test class uses its own GUID-suffixed pipe name (see
/// <see cref="HeartbeatRestartPayloadTests.PipeName"/> / the field on
/// <c>HeartbeatPublisherTests</c>) rather than the machine-global production name
/// <c>eyectrl_watch</c>. The server is <c>maxInstances: 1</c>, and xUnit v3 runs test
/// methods concurrently; a unique name per test instance removes the
/// <c>IOException: All pipe instances are busy</c> contention at the source, with no
/// dependence on xUnit's collection-parallelism controls (which proved unreliable on CI).
/// The collection is retained only to group the related classes.
/// </summary>
[CollectionDefinition("HeartbeatPipe")]
public sealed class HeartbeatPipeCollection
{
}

/// <summary>
/// The restart request travels on the existing heartbeat pipe. These tests stand up a
/// real NamedPipeServerStream, so "was it actually delivered?" is answered by the
/// wire rather than by a mock — which is the question AppRestartCoordinator bets the
/// user's only input channel on.
/// </summary>
[Collection("HeartbeatPipe")]
public class HeartbeatRestartPayloadTests
{
    /// <summary>
    /// Unique pipe name per test instance (xUnit constructs a new instance per method).
    /// Avoids contending for the machine-global single-instance production name across
    /// concurrent test methods — the original cause of CI flakes and hangs.
    /// </summary>
    private readonly string _pipeName = "eyectrl_watch_test_" + Guid.NewGuid().ToString("N");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Accepts one connection and returns the payload it received, or null on timeout.</summary>
    private async Task<HeartbeatPayload?> ReceiveOneAsync(TimeSpan timeout)
    {
        await using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(timeout);
        try { await server.WaitForConnectionAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }

        using var reader = new StreamReader(server, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        return line is null ? null : JsonSerializer.Deserialize<HeartbeatPayload>(line, JsonOptions);
    }

    [Fact]
    public void Payload_DefaultsRestartRequestedToFalse()
    {
        // The flag is additive: every existing construction site must keep compiling
        // and keep meaning "no restart".
        var p = new HeartbeatPayload(1, DateTime.UtcNow, true, false, DateTime.UtcNow, ShuttingDown: false);
        Check.That(p.RestartRequested).IsFalse();
    }

    [Fact]
    public async Task SignalShuttingDown_WithRestart_PutsBothFlagsOnTheWire()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true, pipeName: _pipeName);
        publisher.SignalShuttingDown(restartRequested: true);

        var payload = await received;
        Check.WithCustomMessage("no payload arrived on the pipe").That(payload).IsNotNull();
        Check.That(payload!.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsTrue();
    }

    [Fact]
    public async Task SignalShuttingDown_WithoutRestart_LeavesTheFlagClear()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true, pipeName: _pipeName);
        publisher.SignalShuttingDown();

        var payload = await received;
        Check.That(payload).IsNotNull();
        Check.That(payload!.ShuttingDown).IsTrue();
        Check.That(payload.RestartRequested).IsFalse();
    }

    [Fact]
    public async Task TryReachSupervisor_IsTrue_WhenAServerIsListening()
    {
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true, pipeName: _pipeName);
        var reachable = publisher.TryReachSupervisor();

        Check.That(reachable).IsTrue();
        Check.That(await received).IsNotNull();
    }

    [Fact]
    public void TryReachSupervisor_IsFalse_WhenNothingIsListening()
    {
        // No server stood up. This is the case that must stop a restart dead.
        using var publisher = new HeartbeatPublisher(supervised: true, pipeName: _pipeName);
        Check.That(publisher.TryReachSupervisor()).IsFalse();
    }

    [Fact]
    public void TryReachSupervisor_IsFalse_WhenNotSupervised()
    {
        // Standalone mode starts no writer at all; there is no supervisor by construction.
        using var publisher = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        Check.That(publisher.TryReachSupervisor()).IsFalse();
    }

    [Fact]
    public async Task TryReachSupervisor_DoesNotLatchTheShutdownFlag()
    {
        // The probe must be semantically inert. If it set _shuttingDown, an aborted
        // restart would leave every later Stamp announcing a shutdown that never
        // comes — the supervisor arms its 5s deadline and force-kills a healthy app.
        var probe = ReceiveOneAsync(TimeSpan.FromSeconds(5));

        using var publisher = new HeartbeatPublisher(supervised: true, pipeName: _pipeName);
        _ = publisher.TryReachSupervisor();
        var probePayload = await probe;
        Check.That(probePayload!.ShuttingDown).IsFalse();
        Check.That(probePayload.RestartRequested).IsFalse();

        // The probe must not latch _shuttingDown: a Stamp issued AFTER the probe must still
        // carry ShuttingDown=false. We observe on the wire rather than via CurrentPayload,
        // because the supervised writer drains _pending (Interlocked.Exchange) within
        // microseconds of Stamp signalling it — racing the in-memory read and nulling it under
        // warmed-up suite timing. Reading what the writer actually sends is deterministic and
        // matches the "answered by the wire" stance of the rest of this class.
        var postProbe = ReceiveOneAsync(TimeSpan.FromSeconds(5));
        publisher.Stamp(isConnected: true, isPaused: false);
        var postPayload = await postProbe;
        Check.WithCustomMessage("no post-stamp payload arrived on the pipe").That(postPayload).IsNotNull();
        Check.That(postPayload!.ShuttingDown).IsFalse();
        Check.That(postPayload.RestartRequested).IsFalse();
    }

    [Fact]
    public async Task EnablePublishing_TurnsTheProbeOn()
    {
        // A session that started unsupervised can be upgraded when the user chooses to
        // start a supervisor. Until then the probe must stay false — see below.
        using var publisher = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        Check.That(publisher.TryReachSupervisor()).IsFalse();

        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));
        publisher.EnablePublishing();

        Check.That(publisher.TryReachSupervisor()).IsTrue();
        Check.That(await received).IsNotNull();
    }

    [Fact]
    public void TryReachSupervisor_IsFalseWhileNotPublishing_EvenWithAServerListening()
    {
        // Load-bearing, and not an optimisation: a successful probe ADOPTS this app into
        // the supervisor's Supervising state (Program.cs:270-278). With no periodic
        // heartbeats following, PeriodicCheckStale force-kills it after
        // HangThresholdSeconds. Probing without publishing is a delayed kill.
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(2));

        using var publisher = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        Check.That(publisher.TryReachSupervisor()).IsFalse();

        Check.WithCustomMessage("nothing should have been sent")
            .That(received.Wait(TimeSpan.FromSeconds(3)) && received.Result is not null).IsFalse();
    }
}
