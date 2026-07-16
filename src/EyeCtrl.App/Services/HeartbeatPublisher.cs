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
    private const string PipeName = "eyectrl_watch";
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
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatPublisher"/> class.
    /// </summary>
    /// <param name="supervised">
    /// When <c>true</c>, starts the background writer task that drains the pipe. When <c>false</c>
    /// (standalone mode — <c>Supervision.Enabled == false</c>) no writer is spun up: the publisher
    /// no-ops, so a missing server never blocks a thread-pool thread on Connect forever.
    /// </param>
    public HeartbeatPublisher(bool supervised)
    {
        if (supervised) StartWriter();
    }

    /// <summary>Last stamped payload (testability + diagnostics). <c>null</c> until first <see cref="Stamp"/>.</summary>
    public HeartbeatPayload? CurrentPayload => _pending;

    /// <summary>Called on the GAZE thread (GazeEngine.GazeSampled) on every sample, valid or invalid.</summary>
    public void OnGazeSample() => Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

    /// <summary>Called on the UI thread (DispatcherTimer) every ~3s. Stamps; never blocks on IPC.</summary>
    /// <param name="isConnected">Value of <c>IGazeProvider.IsConnected</c> at stamp time.</param>
    /// <param name="isPaused"><c>true</c> if <c>InteractionManager.CurrentState == InteractionState.Paused</c>.</param>
    public void Stamp(bool isConnected, bool isPaused)
    {
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        _pending = new HeartbeatPayload(_pid, _startedAtUtc, isConnected, isPaused, lastFrame, _shuttingDown);
        // Wake the single writer. The semaphore has capacity 1, so if a signal is already pending
        // (a stamp arrived while the writer was mid-send), Release throws SemaphoreFullException —
        // swallow it: the freshest payload is already in _pending, and the pending signal will
        // wake the writer to drain it. Never throw on the Dispatcher thread.
        try { _sendSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled — writer will pick up _pending */ }
    }

    /// <summary>
    /// Stamps a final <c>shuttingDown:true</c> payload (the authenticated clean-shutdown signal the
    /// supervisor reads inside its 5s deadline). Call BEFORE disposing other services in OnExit.
    /// </summary>
    public void SignalShuttingDown()
    {
        _shuttingDown = true;
        Stamp(isConnected: false, isPaused: false); // ensure a final shuttingDown:true payload is queued
    }

    private void StartWriter() => _writerLoop = Task.Run(WriterLoopAsync);

    private async Task WriterLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // Block until a stamp arrives (or shutdown cancels the token). Cancellation surfaces as
            // OperationCanceledException, which exits the loop — the catch below only covers sends.
            try { await _sendSignal.WaitAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }

            var payload = _pending;
            _pending = null;
            if (payload == null) continue;
            try { await SendOneAsync(payload, _cts.Token); }
            catch { /* drop this send — never throw, never block the Dispatcher */ }
        }
    }

    private static async Task SendOneAsync(HeartbeatPayload payload, CancellationToken token)
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        // CANCELLABLE connect with a real timeout — a missing server (standalone) does NOT
        // block a thread-pool thread forever. WhenAny(connect, delay) bounds it to ConnectTimeout.
        var connectTask = client.ConnectAsync(token);
        var timeoutTask = Task.Delay(ConnectTimeout, token);
        if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
        {
            // Server absent or slow — drop this heartbeat. Observe the abandoned connect task's
            // Exception (it may fault later with IOException/TimeoutException) so it never surfaces
            // as an unobserved-task exception tearing down the process. Cancellation is benign here.
            _ = connectTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        await connectTask; // observe exceptions if connect threw
        var json = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
        await client.WriteAsync(Encoding.UTF8.GetBytes(json), token);
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
