namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;

public class HeartbeatPublisherTests
{
    [Fact]
    public void Stamp_ReflectsLastGazeSample()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.OnGazeSample(); // gaze thread would call this per sample
        pub.Stamp(isConnected: true, isPaused: false);
        var snapshot = pub.CurrentPayload;
        Check.That(snapshot).IsNotNull();
        Check.That(snapshot!.IsConnected).IsTrue();
        Check.That(snapshot.IsPaused).IsFalse();
        Check.That(snapshot.ShuttingDown).IsFalse();
        Check.That(snapshot.LastFrameUtc).IsCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SignalShuttingDown_StampsFinalPayload()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.SignalShuttingDown();
        Check.That(pub.CurrentPayload).IsNotNull();
        Check.That(pub.CurrentPayload!.ShuttingDown).IsTrue();
    }

    [Fact]
    public async Task SignalShuttingDown_DeliversFinalPayload_EvenUnderBlockingSyncContext()
    {
        // Regression (F2): OnExit / OnSessionEnding call SignalShuttingDown on the WPF dispatcher
        // thread, which has a SynchronizationContext, and the call blocks on the final send. If that
        // send's awaits capture the dispatcher context (no ConfigureAwait / not offloaded), the
        // continuation can never run while the thread is blocked in .Wait() -> the shuttingDown
        // payload is never delivered -> the supervisor treats an intentional Exit as a crash and
        // relaunches it. This installs exactly such a non-pumping context and asserts delivery anyway.
        using var server = new NamedPipeServerStream(
            "eyectrl_watch", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                received.TrySetResult(await reader.ReadLineAsync() ?? "");
            }
            catch (Exception ex) { received.TrySetException(ex); }
        });

        using var pub = new HeartbeatPublisher(supervised: false);

        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NonPumpingSyncContext());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            pub.SignalShuttingDown(); // synchronous, bounded — must complete WITHOUT this thread pumping
        }
        finally
        {
            sw.Stop();
            SynchronizationContext.SetSynchronizationContext(original);
        }

        // The bug: SendOneAsync's post-write continuation is posted to the (blocked, non-pumping)
        // context, so the send task never completes and .Wait() burns its full 2s timeout — a 2s
        // stall on every exit/logoff (and on a real WPF dispatcher the send can strand before the
        // write, dropping delivery entirely). The fix offloads the send off the caller's context, so
        // it completes promptly. Well under the 2s .Wait ceiling:
        Check.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(1500));

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Check.That(completed == received.Task).IsTrue(); // payload delivered
        var line = await received.Task;                  // completed above; await avoids a blocking .Result
        Check.That(line).Contains("shuttingDown", "true");
    }

    /// <summary>
    /// A SynchronizationContext that only QUEUES posted callbacks and never runs them — modelling a
    /// UI thread blocked in a synchronous .Wait(). Any async continuation posted back to it is
    /// stranded, so the F2 deadlock bug (send captures this context) would never deliver.
    /// </summary>
    private sealed class NonPumpingSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* stranded on purpose */ }
    }
}
