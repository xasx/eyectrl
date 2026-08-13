namespace EyeCtrl.App.Services;

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;

/// <summary>
/// Stamps a <see cref="HeartbeatPayload"/> on the UI thread (DispatcherTimer) and sends it over
/// the <c>eyectrl_watch</c> named pipe to the supervisor process via a single bounded async writer.
/// </summary>
/// <remarks>
/// <para><b>Threading split (critical):</b> the Dispatcher (UI) thread MUST NEVER block on IPC.
/// <see cref="Stamp"/> only refreshes an in-memory slot and signals a long-lived writer task — it
/// performs no pipe I/O. A single writer task drains a one-slot queue: when a new stamp arrives
/// before the previous one has been sent, the newest overwrites the stale one (only the freshest
/// liveness data ever goes on the wire).</para>
/// <para><b>Standalone mode:</b> when <c>supervised == false</c> (the default, no supervisor running)
/// no writer task is spun up at all — the publisher no-ops. A missing server must never block a
/// thread-pool thread forever, so the writer's <c>ConnectAsync</c> is bounded by a 2s timeout via
/// <see cref="Task.WhenAny(Task, Task)"/>.</para>
/// <para><b>Cross-thread correctness:</b> <see cref="_lastFrameTicks"/> is a <see cref="long"/>
/// swapped with <see cref="Interlocked"/> — never a plain <see cref="DateTime"/> field read/written
/// across the gaze and UI threads.</para>
/// </remarks>
public sealed class HeartbeatPublisher : IDisposable
{
    /// <summary>
    /// The named pipe the supervisor listens on. Injected so tests can use a unique
    /// GUID-suffixed name per class — the production name (<c>eyectrl_watch</c>) is
    /// machine-global and the server is constructed with <c>maxInstances: 1</c>, so two
    /// test classes binding it concurrently contend (<c>IOException: All pipe instances
    /// busy</c>) regardless of xUnit's collection serialization. A per-class unique name
    /// removes the contention at the source. Defaults to the production name.
    /// </summary>
    private readonly string _pipeName;

    /// <summary>
    /// How long <see cref="TryReachSupervisor"/> may block the caller. See
    /// <see cref="DefaultProbeWait"/> for why production uses 500ms and why tests must not.
    /// </summary>
    private readonly TimeSpan _probeWait;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    // I1: camelCase JSON to match every other emitter in the codebase AND the supervisor's deserializer.
    // Load-bearing: without this, the record serializes PascalCase ({"LastFrameUtc":...}); the server
    // happens to tolerate it via PropertyNameCaseInsensitive, but if that leniency were ever tightened,
    // LastFrameUtc would deserialize to DateTime.MinValue -> false GazeStreamDead -> crash loop.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly int _pid = Environment.ProcessId;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    // Single-slot writer: the newest stamp overwrites any unsent one. One long-lived
    // writer task drains the slot — no per-stamp Task.Run, no thread-pool starvation.
    // Cross-thread access (gaze thread writes lastFrameTicks, Dispatcher reads/writes)
    // uses Interlocked on a long (ticks), never a plain DateTime field.
    private long _lastFrameTicks = DateTime.UtcNow.Ticks;
    private HeartbeatPayload? _pending;          // next payload to send
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private volatile bool _shuttingDown;
    private int _publishing;   // 0 = off, 1 = on — swapped with Interlocked, see EnablePublishing
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerLoop;

    /// <summary>
    /// The production pipe name the supervisor listens on.
    /// </summary>
    public const string DefaultPipeName = "eyectrl_watch";

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatPublisher"/> class.
    /// </summary>
    /// <param name="supervised">
    /// When <c>true</c>, starts the background writer task that drains the pipe. When <c>false</c>
    /// (standalone mode — <c>Supervision.Enabled == false</c>) no writer is spun up: the publisher
    /// no-ops, so a missing server never blocks a thread-pool thread on Connect forever.
    /// </param>
    /// <param name="pipeName">
    /// Named pipe to publish on. Defaults to <see cref="DefaultPipeName"/>; tests pass a unique
    /// GUID-suffixed name so concurrent test classes never contend for the machine-global
    /// single-instance production name.
    /// </param>
    /// <param name="probeWait">
    /// How long <see cref="TryReachSupervisor"/> may block its caller. Defaults to
    /// <see cref="DefaultProbeWait"/>; tests that assert a *successful* probe pass a generous
    /// value, because 500ms is a UI-responsiveness budget and not a latency guarantee — see
    /// <see cref="DefaultProbeWait"/>.
    /// </param>
    public HeartbeatPublisher(bool supervised, string? pipeName = null, TimeSpan? probeWait = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
        _probeWait = probeWait ?? DefaultProbeWait;
        if (supervised) EnablePublishing();
    }

    /// <summary>Last stamped payload (testability + diagnostics). <c>null</c> until first <see cref="Stamp"/>.</summary>
    public HeartbeatPayload? CurrentPayload => _pending;

    /// <summary>
    /// Whether this publisher is permitted to put anything on the wire. Exposed for the app's
    /// phase-2 instance-scope check, which asserts a scoped instance never publishes.
    /// </summary>
    public bool IsPublishing => Volatile.Read(ref _publishing) != 0;

    /// <summary>Called on the GAZE thread (GazeEngine.GazeSampled) on every sample, valid or invalid.</summary>
    public void OnGazeSample() => Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

    /// <summary>Called on the UI thread (DispatcherTimer) every ~3s. Stamps; never blocks on IPC.</summary>
    /// <param name="isConnected">Value of <c>IGazeProvider.IsConnected</c> at stamp time.</param>
    /// <param name="isPaused"><c>true</c> if <c>InteractionManager.CurrentState == InteractionState.Paused</c>.</param>
    public void Stamp(bool isConnected, bool isPaused)
    {
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        _pending = new HeartbeatPayload(_pid, _startedAtUtc, isConnected, isPaused, lastFrame, _shuttingDown);
        // Nothing to wake in standalone mode: the timer stamps unconditionally (App.xaml.cs)
        // but no writer loop exists, so the semaphore would saturate at its max of 1 and every
        // subsequent Release would throw into the catch below — every 3s, for the life of the
        // process. The payload still lands in _pending, which is all CurrentPayload needs, and
        // EnablePublishing's writer drains it on its first pass.
        if (Volatile.Read(ref _publishing) == 0) return;

        // Wake the single writer. The semaphore has capacity 1, so if a signal is already pending
        // (a stamp arrived while the writer was mid-send), Release throws SemaphoreFullException —
        // swallow it: the freshest payload is already in _pending, and the pending signal will
        // wake the writer to drain it. Never throw on the Dispatcher thread.
        try { _sendSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled — writer will pick up _pending */ }
    }

    /// <summary>
    /// Stamps and FLUSHES a final <c>shuttingDown:true</c> payload (the authenticated clean-shutdown
    /// signal the supervisor reads inside its 5s deadline). Call BEFORE disposing other services in
    /// OnExit/OnSessionEnding, and BEFORE Dispose (which cancels the writer loop). F2: this performs
    /// a SYNCHRONOUS bounded send (not via the async loop) so that a subsequent Dispose/CTS-cancel
    /// cannot abort the final payload mid-flight. When not publishing (<see cref="IsPublishing"/> is
    /// <c>false</c>) the payload is still stamped for diagnostics but nothing goes on the wire — the
    /// old wording, "if not supervised (no server), this is a bounded no-op", only held when no
    /// server existed, and a user's own supervisor listening on <c>eyectrl_watch</c> would otherwise
    /// receive an unsupervised instance's clean-exit signal and apply it to the app it supervises.
    /// </summary>
    public void SignalShuttingDown(bool restartRequested = false)
    {
        _shuttingDown = true;
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        var final = new HeartbeatPayload(
            _pid, _startedAtUtc, false, false, lastFrame,
            ShuttingDown: true, RestartRequested: restartRequested);
        _pending = final;

        // Guard the SEND, not the method. An early return here would skip _shuttingDown and
        // _pending above and silently empty CurrentPayload, which exists for diagnostics and is
        // asserted by SignalShuttingDown_StampsFinalPayload.
        //
        // The guard itself: this method's doc comment claimed "if not supervised (no server), this
        // is a bounded no-op", which holds only when no server exists. When the user's own
        // supervisor IS listening, an unsupervised instance's exit reaches it. TryReachSupervisor
        // has always had this check; this method never did.
        if (!IsPublishing) return;

        // Synchronous bounded flush — a fresh CancellationTokenSource (NOT _cts) so Dispose's cancel
        // can't race it. If the send can't complete in 2s (no server / slow server), give up; the
        // supervisor's heartbeat-stale path will then treat it as a crash, which is the safe default.
        try { SendOneAsync(final, CancellationToken.None).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort — never block OnExit on the final send */ }
    }

    /// <summary>
    /// Sends one ordinary payload and reports whether it was actually delivered — i.e.
    /// whether a supervisor is listening on the pipe *right now*.
    /// <c>Supervision.Enabled</c> only records that the user configured one; the app can
    /// still be launched by hand with no supervisor running, and in that case a restart
    /// would exit into nothing.
    ///
    /// Deliberately inert: it does NOT set <see cref="_shuttingDown"/>. That field latches,
    /// so a probe that set it and then aborted would make every later <see cref="Stamp"/>
    /// announce a shutdown that never arrives — arming the supervisor's 5s clean-exit
    /// deadline and getting a healthy app force-killed.
    ///
    /// The caller's wait is bounded by <see cref="DefaultProbeWait"/>, not by the 2s connect
    /// timeout: <see cref="AppRestartCoordinator.ShouldOfferSupervisor"/> runs this on the
    /// dispatcher, and a live supervisor answers in single-digit milliseconds. Anything
    /// slower is not a supervisor worth freezing the user's gaze control for — a stale
    /// probe simply reads false and the restart falls through to the self-restart
    /// strategy, which is the safe direction.
    /// </summary>
    /// <returns><c>true</c> if the payload reached the supervisor within the timeout.</returns>
    public bool TryReachSupervisor()
    {
        // Not publishing? Then do not probe. A successful probe adopts this app into the
        // supervisor's Supervising state, after which the absence of periodic heartbeats
        // gets it force-killed within HangThresholdSeconds. Probing without publishing is
        // a delayed kill, not a harmless question.
        if (Volatile.Read(ref _publishing) == 0) return false;

        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        var probe = new HeartbeatPayload(
            _pid, _startedAtUtc, false, false, lastFrame,
            ShuttingDown: false, RestartRequested: false);

        try
        {
            var send = SendOneAsync(probe, CancellationToken.None);
            if (send.Wait(_probeWait))
            {
                return send.Result;
            }

            // The probe gave up before the send completed. The send may still fault in the
            // background (e.g. WriteAsync throwing after connect succeeded but past the wait);
            // observe its exception so it can never surface as an unobserved-task exception.
            // SendOneAsync's own connect-timeout branch already observes a connect fault; this
            // covers a fault on the write path that races past the probe's wait. Matches the
            // doc comment on DefaultProbeWait ("its faulted outcome is observed").
            _ = send.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How long a supervisor probe may block the caller before the supervisor is treated
    /// as absent. A live supervisor answers in milliseconds; 500ms also matches the
    /// bring-up poll interval (Task 13). The probe's connect may run past this in the
    /// background — it is bounded by <see cref="ConnectTimeout"/> and its faulted outcome
    /// is observed there — but the caller never waits on it.
    ///
    /// <para>This is a UI-responsiveness budget, NOT a latency guarantee, and the difference
    /// is load-bearing for tests. <see cref="SendOneAsync"/> completes across several
    /// thread-pool hops (the work item inside <c>ConnectAsync</c>, the <c>WhenAny</c>
    /// continuation, the write completion); when the pool has no idle worker, each hop waits
    /// on hill-climbing thread injection — roughly 500ms apiece. Under a starved pool the
    /// probe therefore reads false while the very same payload still lands ~270ms later
    /// (diagnostic run, 2026-08-09: probe gave up at 513ms, payload arrived at 774ms). That is
    /// correct production behaviour — a supervisor that slow is not worth freezing gaze
    /// control for, and false falls through to self-restart, the safe direction — but it means
    /// a test asserting a *successful* probe must pass its own generous <c>probeWait</c>
    /// rather than betting on this number. Two such tests flaked on loaded CI runners before
    /// the budget was made injectable.</para>
    /// </summary>
    public static readonly TimeSpan DefaultProbeWait = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Starts publishing heartbeats, if not already. Idempotent.
    ///
    /// Called from the constructor when <c>Supervision.Enabled</c>, and again if the user
    /// starts a supervisor mid-session. Turning this on BEFORE probing is mandatory: a
    /// probe that reaches a supervisor adopts this app into its Supervising state, and an
    /// adopted app that then sends nothing is force-killed once HangThresholdSeconds
    /// elapses.
    /// </summary>
    public void EnablePublishing()
    {
        // CompareExchange, not a check-then-set: two callers (the constructor and Task 13's
        // mid-session enable) must not both spin up a writer loop.
        if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0) return;
        _writerLoop = Task.Run(WriterLoopAsync);
    }

    private async Task WriterLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // Block until a stamp arrives (or shutdown cancels the token). Cancellation surfaces as
            // OperationCanceledException, which exits the loop — the catch below only covers sends.
            try { await _sendSignal.WaitAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }

            // Atomic grab-and-clear: a Stamp that lands between reading _pending and nulling it would
            // otherwise be silently dropped (its payload overwritten with null). Interlocked.Exchange
            // takes the current payload and clears the slot in one step, so a concurrent Stamp either
            // is captured now or remains for the next iteration.
            var payload = Interlocked.Exchange(ref _pending, null);
            if (payload == null) continue;
            try { _ = await SendOneAsync(payload, _cts.Token); }
            catch { /* drop this send — never throw, never block the Dispatcher */ }
        }
    }

    /// <summary>
    /// Sends one payload. Returns <c>true</c> only if the write completed — the
    /// connect-timeout path returns <c>false</c> rather than succeeding silently,
    /// because <see cref="TryReachSupervisor"/> stakes the restart decision on the
    /// difference between "delivered" and "dropped".
    /// </summary>
    private async Task<bool> SendOneAsync(HeartbeatPayload payload, CancellationToken token)
    {
        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        // CANCELLABLE connect with a real timeout — a missing server (standalone) does NOT
        // block a thread-pool thread forever. WhenAny(connect, delay) bounds it to ConnectTimeout.
        var connectTask = client.ConnectAsync(token);
        var timeoutTask = Task.Delay(ConnectTimeout, token);
        // ConfigureAwait(false) on every await: SignalShuttingDown blocks on this task with .Wait()
        // from the WPF dispatcher thread (OnExit/OnSessionEnding), which has a SynchronizationContext.
        // Without this, the post-await continuations would be posted back to that blocked thread and
        // strand — the send never completes, .Wait() burns its full 2s timeout (a stall on every
        // exit), and on a real dispatcher the strand can precede the write and drop delivery. Running
        // continuations on the thread pool keeps the send independent of the caller's context.
        if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) == timeoutTask)
        {
            // Server absent or slow — drop this heartbeat. Observe the abandoned connect task's
            // Exception (it may fault later with IOException/TimeoutException) so it never surfaces
            // as an unobserved-task exception tearing down the process. Cancellation is benign here.
            _ = connectTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }
        await connectTask.ConfigureAwait(false); // observe exceptions if connect threw
        var json = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
        await client.WriteAsync(Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Cancels the writer loop, waits (bounded) for it to drain, and disposes resources.
    /// Safe to call even when not supervised (no writer was started).
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _writerLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _sendSignal.Dispose();
        _cts.Dispose();
    }
}
