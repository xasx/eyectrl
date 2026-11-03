namespace EyeCtrl.Supervisor.Tests.Ipc;

using System.IO;
using System.IO.Pipes;
using System.Text;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;

public class HeartbeatPipeServerTests
{
    private static string BuildValidHeartbeatJson(int pid)
    {
        return $"{{\"pid\":{pid},\"startedAtUtc\":\"2025-01-01T00:00:00Z\",\"isConnected\":true,\"isPaused\":false,\"lastFrameUtc\":\"2025-01-01T00:00:01Z\",\"shuttingDown\":false,\"restartRequested\":false}}";
    }

    [Fact]
    public async Task HeartbeatPipeServer_ReceivesHeartbeat_AndCachesIdentityPerConnection()
    {
        var pipeName = "eyectrl_watch_test_" + Guid.NewGuid().ToString("N");
        var binder = ClientIdentityBinder.ForAdoption(Environment.ProcessPath!);
        using var server = new HeartbeatPipeServer(binder, pipeName);

        HeartbeatPayload? firstPayload = null;
        ClientIdentity? firstIdentity = null;
        HeartbeatPayload? secondPayload = null;
        ClientIdentity? secondIdentity = null;
        int receivedCount = 0;

        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HeartbeatReceived += (payload, identity) =>
        {
            var count = Interlocked.Increment(ref receivedCount);
            if (count == 1)
            {
                firstPayload = payload;
                firstIdentity = identity;
                firstReceived.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondPayload = payload;
                secondIdentity = identity;
                secondReceived.TrySetResult(true);
            }
        };

        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

        var json1 = BuildValidHeartbeatJson(Environment.ProcessId);
        await writer.WriteLineAsync(json1);
        await writer.FlushAsync();

        var firstCompleted = await Task.WhenAny(firstReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Check.That(firstCompleted == firstReceived.Task).IsTrue();
        Check.That(firstPayload).IsNotNull();
        Check.That(firstIdentity).IsNotNull();

        var json2 = BuildValidHeartbeatJson(Environment.ProcessId);
        await writer.WriteLineAsync(json2);
        await writer.FlushAsync();

        var secondCompleted = await Task.WhenAny(secondReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Check.That(secondCompleted == secondReceived.Task).IsTrue();
        Check.That(secondPayload).IsNotNull();
        Check.That(secondIdentity).IsNotNull();
        Check.That(secondIdentity).IsSameReferenceAs(firstIdentity);
    }

    // One malformed line from a version-skewed or glitched App must be SKIPPED, not drop the
    // connection: if the skip-not-drop contract regresses, heartbeat ingestion ends and a hung
    // gaze stream is never detected — recovery never fires. The valid heartbeat sent after the
    // bad one on the SAME stream is the usability proof.
    [Fact]
    public async Task UnparseableLine_IsSkipped_AndTheConnectionStaysUsable()
    {
        var pipeName = "eyectrl_watch_test_" + Guid.NewGuid().ToString("N");
        var binder = ClientIdentityBinder.ForAdoption(Environment.ProcessPath!);
        using var server = new HeartbeatPipeServer(binder, pipeName);

        HeartbeatPayload? payload = null;
        ClientIdentity? identity = null;
        var receivedCount = 0;
        var validReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HeartbeatReceived += (p, i) =>
        {
            var count = Interlocked.Increment(ref receivedCount);
            payload = p;
            identity = i;
            validReceived.TrySetResult(true);
        };

        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("not-json");
        await writer.FlushAsync();
        await writer.WriteLineAsync(BuildValidHeartbeatJson(Environment.ProcessId));
        await writer.FlushAsync();

        var completed = await Task.WhenAny(validReceived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Check.That(completed == validReceived.Task).IsTrue();
        Check.That(receivedCount).IsEqualTo(1); // the bad line booked nothing
        Check.That(payload).IsNotNull();
        Check.That(payload!.Pid).IsEqualTo(Environment.ProcessId);
        Check.That(identity).IsNotNull();
    }

    // A throwing subscriber must never cost the heartbeat channel: dispatch survives the fault and
    // later heartbeats still reach the recorder — that INTEGRATED contract is what this test pins.
    // The dedicated subscriber catch in HeartbeatPipeServer.cs (:83-84) additionally keeps the
    // fault out of NewlineJsonPipeServer's line-handler catch (a "subscriber threw" Warning instead
    // of a misleading "unparseable line" Debug); that layering is only observable through the log
    // and is deliberately not pinned here (review r1 F3).
    [Fact]
    public async Task ThrowingSubscriber_IsContained_AndLaterHeartbeatsStillReachTheRecorder()
    {
        var pipeName = "eyectrl_watch_test_" + Guid.NewGuid().ToString("N");
        var binder = ClientIdentityBinder.ForAdoption(Environment.ProcessPath!);
        using var server = new HeartbeatPipeServer(binder, pipeName);

        var subscriberCalls = 0;
        server.HeartbeatReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref subscriberCalls) == 1)
                throw new InvalidOperationException("simulated one-time dispatcher fault");
        };

        var recorded = new List<HeartbeatPayload>();
        var twoRecorded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HeartbeatReceived += (payload, _) =>
        {
            lock (recorded)
            {
                recorded.Add(payload);
                if (recorded.Count == 2)
                    twoRecorded.TrySetResult(true);
            }
        };

        server.Start();

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

        for (var i = 0; i < 3; i++)
        {
            await writer.WriteLineAsync(BuildValidHeartbeatJson(Environment.ProcessId));
            await writer.FlushAsync();
        }

        var completed = await Task.WhenAny(twoRecorded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Check.That(completed == twoRecorded.Task).IsTrue();
        lock (recorded)
        {
            Check.That(recorded).HasSize(2); // heartbeat #2 and #3; #1 was shadowed by the throw
            Check.That(recorded[0].Pid).IsEqualTo(Environment.ProcessId);
            Check.That(recorded[1].Pid).IsEqualTo(Environment.ProcessId);
        }
        // The poisoned subscriber was invoked again — dispatch really continued through it.
        Check.That(subscriberCalls).IsGreaterOrEqualThan(2);
    }
}
