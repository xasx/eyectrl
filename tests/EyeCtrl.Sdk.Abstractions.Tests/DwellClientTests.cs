namespace EyeCtrl.Sdk.Abstractions.Tests;

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
}
