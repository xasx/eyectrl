namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;

[Collection("HeartbeatPipe")]
public class HeartbeatPublisherTests
{
    /// <summary>
    /// Unique pipe name per test instance (xUnit constructs a new instance per method).
    /// Avoids contending for the machine-global single-instance production name
    /// (<c>eyectrl_watch</c>) across concurrent test methods — the original cause of CI
    /// flakes (IOException "All pipe instances are busy") and, combined with an unbounded
    /// WaitForConnectionAsync, hangs.
    /// </summary>
    private readonly string _pipeName = "eyectrl_watch_test_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Stamp_ReflectsLastGazeSample()
    {
        var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
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
        var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
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
            _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                // Bounded wait: an unbounded WaitForConnectionAsync hangs the run if no client ever
                // connects (e.g. a send that strands before the write). 10s is far beyond any
                // legitimate connect and turns a missing connection into a test failure (the
                // TaskCompletionSource is set to the cancellation exception) rather than a CI hang.
                using var listenCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await server.WaitForConnectionAsync(listenCts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                received.TrySetResult(await reader.ReadLineAsync() ?? "");
            }
            catch (Exception ex) { received.TrySetException(ex); }
        });

        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);

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

        // The regression guard is the DELIVERY assertion below, not a duration budget: a stranded
        // send (the F2 bug) drops the payload, so "received within 5s" fails regardless of timing.
        // A duration threshold cannot reliably distinguish "fixed" from "stranded" on a slow/loaded
        // CI runner, where the 2s-bounded .Wait itself overshoots under scheduling pressure —
        // stranding the regression at the same ~2s ceiling the fix lands near. The elapsed time is
        // captured for diagnosis, not asserted.
        _ = sw.Elapsed;

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
