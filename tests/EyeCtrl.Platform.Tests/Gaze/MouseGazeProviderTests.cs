namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Platform.Gaze;
using System.Threading;

public class MouseGazeProviderTests
{
    [Fact]
    public async Task Stop_NoEventsFireAfterStopReturns()
    {
        var provider = new MouseGazeProvider(pollIntervalMs: 5);
        int count = 0;
        provider.GazePointReceived += _ => Interlocked.Increment(ref count);

        provider.Start();
        await Task.Delay(60);
        provider.Stop();

        int atStop = Volatile.Read(ref count);
        await Task.Delay(120);
        Check.That(Volatile.Read(ref count)).IsEqualTo(atStop);
        Check.That(atStop).IsStrictlyGreaterThan(0);
        provider.Dispose();
    }

    [Fact]
    public void StartStopCycles_DoNotThrow()
    {
        var provider = new MouseGazeProvider(pollIntervalMs: 1);
        for (int i = 0; i < 10; i++)
        {
            provider.Start();
            Thread.Sleep(5);
            provider.Stop();
        }
        provider.Dispose();
        Check.That(provider.IsConnected).IsFalse();
    }
}
