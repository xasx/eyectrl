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
/// The collection also carries <see cref="ThreadPoolFloorFixture"/>, which every pipe test depends
/// on to reach the wire at all — see that type for why.
/// </summary>
[CollectionDefinition("HeartbeatPipe")]
public sealed class HeartbeatPipeCollection : ICollectionFixture<ThreadPoolFloorFixture>
{
}

/// <summary>
/// Raises the thread-pool minimum-worker floor for the test process.
///
/// <para>Every send in <see cref="HeartbeatPublisher"/> needs a pool WORKER before a single byte
/// reaches the pipe: <c>NamedPipeClientStream.ConnectAsync</c> queues the blocking connect as a
/// work item. At the stock floor a pool whose workers are all busy grows only by hill-climbing
/// injection — roughly one thread per 500ms — so that one work item can sit in the global queue
/// for seconds. <see cref="HeartbeatPublisher.SignalShuttingDown"/> gives its flush 2s and
/// <see cref="HeartbeatPublisher.TryReachSupervisor"/> gives its probe <c>probeWait</c>; past
/// that the payload is abandoned, and no assertion about what reached the wire can hold.</para>
///
/// <para>That is what turned <c>SignalShuttingDown_WithRestart_PutsBothFlagsOnTheWire</c> red on a
/// docs-only commit (CI run 31339802092, "no payload arrived on the pipe"): the suite's eleven test
/// assemblies run concurrently on one hosted runner, and the pipe never saw a connection inside the
/// test's 5s window — on a commit that touched only AGENTS.md and CLAUDE.md, so nothing about the
/// product had changed. The
/// exposure was predicted when the probe budget was made injectable — the residual noted there was
/// "a stall long enough to push the connect past ConnectTimeout still makes the send report false
/// ... the same exposure the never-flaking SignalShuttingDown tests carry".</para>
///
/// <para>Measured under a deliberately starved pool (512 blocking work items queued to the GLOBAL
/// queue from a non-pool thread, <c>DOTNET_PROCESSOR_COUNT=2</c>, 2026-08-10): the connect completes
/// at ~700ms at the stock floor versus ~21ms with the floor raised — 33x lower, leaving two orders
/// of magnitude of headroom under the 2s flush budget, from one process-wide setting. Capping the
/// pool's MAXIMUM instead (the same harness with <c>SetMaxThreads(2, 2)</c>) reproduces the CI
/// failure exactly and deterministically — <c>WaitForConnection TIMED OUT</c>, payload null, 3 of 3
/// runs — which is what identified the connect work item as the thing being starved.</para>
///
/// <para>This relaxes nothing the tests check and moves no production budget: the flush stays at
/// 2s, and <see cref="HeartbeatPublisher.DefaultProbeWait"/> stays at 500ms (pinned by
/// <c>DefaultProbeWait_StaysAtTheUiResponsivenessBudget</c>). It removes an artefact of the test
/// host — a pool starved by hundreds of unrelated concurrent tests — that the app does not have,
/// because production calls these methods from the WPF dispatcher of a process that is not running
/// a test suite.</para>
///
/// <para>A floor is not a reservation: the pool creates a worker only when work is queued and no
/// worker is idle, so the value below caps demand-driven growth rather than starting threads.</para>
/// </summary>
public sealed class ThreadPoolFloorFixture
{
    /// <summary>Workers the pool may create on demand before hill-climbing throttles growth.</summary>
    public const int Floor = 128;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadPoolFloorFixture"/> class, raising the
    /// pool floor. Throws rather than degrading quietly: if the floor is not in effect the pipe
    /// tests are back to betting on thread injection, and the failure to look at is this one — not
    /// a payload that mysteriously never arrived.
    /// </summary>
    public ThreadPoolFloorFixture()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        // Never lower an existing floor: another fixture may have raised it further.
        if (!ThreadPool.SetMinThreads(Math.Max(workers, Floor), Math.Max(completionPorts, Floor)))
        {
            throw new InvalidOperationException(
                $"Could not raise the thread-pool floor to {Floor}; the heartbeat pipe tests would " +
                "be left racing hill-climbing thread injection.");
        }
    }
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

    /// <summary>
    /// Probe budget for the tests that assert a probe SUCCEEDS. Production's 500ms
    /// (<see cref="HeartbeatPublisher.DefaultProbeWait"/>) is a UI-responsiveness budget, not a
    /// latency guarantee: the send completes across several thread-pool hops, and on a pool with
    /// no idle worker each hop waits ~500ms for thread injection. Starving the pool reproduces
    /// the CI failure exactly — the probe gives up at 513ms and the payload lands at 774ms —
    /// and it does so identically with <c>maxNumberOfServerInstances</c> raised to 8, so the
    /// pipe instance count is not involved. Asserting <c>IsTrue</c> on a 500ms wall-clock
    /// deadline is betting on scheduler luck; matching the 5s receive timeout below removes the
    /// bet without weakening what the tests check — they still call the real
    /// <see cref="HeartbeatPublisher.TryReachSupervisor"/>, gate and all.
    ///
    /// Deliberately NOT passed to the tests that assert a probe FAILS: those cannot flake in the
    /// true direction (nothing is listening, or publishing is off and no I/O happens at all), and
    /// leaving them on the production budget keeps them fast.
    /// </summary>
    private static readonly TimeSpan GenerousProbeWait = TimeSpan.FromSeconds(5);

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
    public void ThreadPoolFloor_IsInEffect_ForThisCollection()
    {
        // Drift guard, not a tautology: the floor is what keeps every other test in this file off
        // hill-climbing thread injection (see ThreadPoolFloorFixture). The fixture throws if it
        // cannot apply the floor, but nothing else would notice if the collection stopped
        // declaring ICollectionFixture at all — and the symptom then is a payload that
        // intermittently never arrives, which reads as a product bug rather than a lost fixture.
        ThreadPool.GetMinThreads(out var workers, out _);
        Check.That(workers).IsGreaterOrEqualThan(ThreadPoolFloorFixture.Floor);
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

        using var publisher = new HeartbeatPublisher(
            supervised: true, pipeName: _pipeName, probeWait: GenerousProbeWait);
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

        // Generous budget: this test needs the probe's payload to actually land, so the probe
        // must wait for its own send rather than abandoning it after 500ms and leaving delivery
        // to chance.
        using var publisher = new HeartbeatPublisher(
            supervised: true, pipeName: _pipeName, probeWait: GenerousProbeWait);
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
        using var publisher = new HeartbeatPublisher(
            supervised: false, pipeName: _pipeName, probeWait: GenerousProbeWait);
        Check.That(publisher.TryReachSupervisor()).IsFalse();

        // Note the payload asserted below is the PROBE's. EnablePublishing itself sends nothing:
        // the writer loop's first act is to await _sendSignal, which starts at count 0 and is
        // only released by Stamp/SignalShuttingDown, neither of which this test calls.
        var received = ReceiveOneAsync(TimeSpan.FromSeconds(5));
        publisher.EnablePublishing();

        Check.That(publisher.TryReachSupervisor()).IsTrue();
        Check.That(await received).IsNotNull();
    }

    [Fact]
    public void DefaultProbeWait_StaysAtTheUiResponsivenessBudget()
    {
        // The tests above deliberately run on a wider budget than production, so nothing else
        // would notice if this were tightened — and tightening it makes a live supervisor more
        // likely to read as absent, silently downgrading restarts to the self-restart path.
        Check.That(HeartbeatPublisher.DefaultProbeWait).IsEqualTo(TimeSpan.FromMilliseconds(500));
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
