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
        var firstEvent = new TaskCompletionSource();
        provider.GazePointReceived += _ =>
        {
            Interlocked.Increment(ref count);
            firstEvent.TrySetResult();
        };

        provider.Start();

        // Wait for the provider to actually emit at least one event before stopping. A fixed
        // Task.Delay flakes on CI: when the thread pool is saturated the timer's first callback
        // can land after the window, leaving count == 0. Gate on the first real event with a
        // generous timeout so the check is deterministic regardless of scheduling latency.
        var completed = await Task.WhenAny(
            firstEvent.Task,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Check.That(completed).IsSameReferenceAs(firstEvent.Task);

        provider.Stop();

        // Stop() waits for any in-flight tick, so the count is frozen once it returns.
        int atStop = Volatile.Read(ref count);
        Check.That(atStop).IsStrictlyGreaterThan(0);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Check.That(Volatile.Read(ref count)).IsEqualTo(atStop);
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
