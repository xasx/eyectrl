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
    public async Task SquattedWatchPipe_IsRejected_ProbeReportsUnreachable_AndNothingIsWritten()
    {
        // eyectrl_watch is machine-global: any local process can hold the name. A heartbeat
        // send or probe that reaches a squatter both leaks liveness data and — worse — makes
        // TryReachSupervisor answer "supervisor reachable", corrupting restart decisions. The
        // publisher must treat a server that is not the supervisor exe as ABSENT: probe false,
        // and not one byte written. This test's server lives in the test host, i.e. it is a
        // squatter by identity.
        using var squatter = new NamedPipeServerStream(_pipeName, PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096);
        var drain = Task.Run<string?>(async () =>
        {
            try
            {
                await squatter.WaitForConnectionAsync();
                using var reader = new StreamReader(squatter, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { return await reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }
            }
            catch (Exception)
            {
                // The rejected client disconnects — from the server side that is a broken pipe,
                // which IS "nothing was written". Only a written payload can return a line.
                return null;
            }
        });

        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        pub.EnablePublishing();
        var reached = pub.TryReachSupervisor();

        Check.That(reached).IsFalse();
        Check.That(await drain).IsNull(); // nothing was ever written to the squatter
    }

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
        // Captured outside Task.Run so the listener observes the test's token regardless of how
        // TestContext flows across the pool hop.
        var testToken = TestContext.Current.CancellationToken;
        _ = Task.Run(async () =>
        {
            try
            {
                // Bounded wait: an unbounded WaitForConnectionAsync hangs the run if no client ever
                // connects (e.g. a send that strands before the write). 10s is far beyond any
                // legitimate connect and turns a missing connection into a test failure (the
                // TaskCompletionSource is set to the cancellation exception) rather than a CI hang.
                // Linked to the test token so cancelling the run also unblocks this listener; the
                // 10s bound is preserved via CancelAfter.
                using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
                listenCts.CancelAfter(TimeSpan.FromSeconds(10));
                await server.WaitForConnectionAsync(listenCts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                received.TrySetResult(await reader.ReadLineAsync() ?? "");
            }
            catch (Exception ex) { received.TrySetException(ex); }
        }, testToken);

        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName)
            { TrustAnyServerForTests = true }; // in-process test server, not the supervisor exe
        pub.EnablePublishing();   // the send is now gated on this; the F2 assertion still stands

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

        var completed = await Task.WhenAny(
            received.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Check.That(completed == received.Task).IsTrue(); // payload delivered
        var line = await received.Task;                  // completed above; await avoids a blocking .Result
        Check.That(line).Contains("shuttingDown", "true");
    }

    [Fact]
    public async Task SignalShuttingDown_SendsNothing_WhenNotPublishing()
    {
        // An unsupervised instance's graceful exit used to dial eyectrl_watch anyway. That matters
        // whenever a supervisor is running while the app was launched by hand: a supervisor in
        // adoption mode authenticates on exe path + session and skips the creation-time comparison,
        // so the connection binds as VERIFIED; CleanExitRequested carries no PID, so the supervisor
        // applies the clean-exit to whatever it IS supervising, and that app gets force-killed when
        // it does not exit inside the 5s deadline.
        using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        pub.SignalShuttingDown();

        // Nothing should ever connect. Bounded wait: a pass is "no connection within the budget".
        var finished = await Task.WhenAny(connected, Task.Delay(1500, TestContext.Current.CancellationToken));

        Check.That(finished).Not.IsSameReferenceAs(connected);
        Check.That(pub.IsPublishing).IsFalse();
    }

    [Fact]
    public void IsPublishing_IsFalseWhenNotSupervised_AndTrueAfterEnable()
    {
        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        Check.That(pub.IsPublishing).IsFalse();

        pub.EnablePublishing();
        Check.That(pub.IsPublishing).IsTrue();
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
