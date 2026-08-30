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
}
