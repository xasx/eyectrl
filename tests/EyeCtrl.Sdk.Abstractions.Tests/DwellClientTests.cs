namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

public class DwellClientTests
{
    [Fact]
    public async Task Start_CalledConcurrently_ThenDispose_DoesNotThrow()
    {
        var client = new DwellClient();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => { await gate.Task; client.Start(); }))
            .ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);
        client.Dispose();
    }

    [Fact]
    public void Dispose_WhileConnectLoopInBackoffDelay_DoesNotThrow()
    {
        var client = new DwellClient();
        client.Start();
        Thread.Sleep(100);
        client.Dispose();
    }

    [Fact]
    public async Task SquattedDwellPipe_FabricatedActivation_IsIgnored()
    {
        // Pipe names are machine-global: any local process can serve eyectrl_dwell and push
        // fabricated `activated` lines that would become attacker-timed clicks in the host app.
        // With VerifyServerIdentity on (the default), a server that is not the EyeCtrl process
        // is rejected BEFORE the read loop starts, so its lines are never delivered. This test's
        // server lives in the test host — exactly a squatter by identity.
        var pipeName = "eyectrl_dwell_test_" + Guid.NewGuid().ToString("N");
        // Explicit buffers: with the default zero-size buffers a named-pipe WRITE blocks until
        // the peer reads — and this client, by design, never reads from a squatter. The fake
        // line must land in the buffer so the server task can complete.
        using var squatter = new System.IO.Pipes.NamedPipeServerStream(pipeName,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096);
        var serverTask = Task.Run(async () =>
        {
            await squatter.WaitForConnectionAsync();
            using var writer = new System.IO.StreamWriter(squatter, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"type":"activated","id":"btn"}""");
            await Task.Delay(TimeSpan.FromSeconds(2)); // hold open: the client would read if it trusted us
        });

        var adapter = new RecordingAdapter("btn");
        using var client = new DwellClient(pipeName); // VerifyServerIdentity defaults true
        client.RegisterAdapter(adapter);
        client.Start();

        await Task.Delay(TimeSpan.FromMilliseconds(2500)); // connect attempt + reject + one backoff cycle

        Check.That(adapter.Activations).IsEqualTo(0);
        await serverTask;
    }

    private sealed class RecordingAdapter(string id) : IDwellElementAdapter
    {
        public string Id { get; } = id;
        public DwellMode Mode => DwellMode.Invoke;
        public long WindowHandle => 0;
        public int Activations { get; private set; }
        public bool TryGetScreenRect(out SdkRect rect)
        {
            rect = new SdkRect(0, 0, 100, 100);
            return true;
        }
        public void Activate() => Activations++;
    }
}
