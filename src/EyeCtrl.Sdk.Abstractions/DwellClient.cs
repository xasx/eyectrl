namespace EyeCtrl.Sdk.Abstractions;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EyeCtrl.Contracts;

/// <summary>
/// Named-pipe client to the EyeCtrl dwell service. Byte-mode, newline-framed JSON.
/// Dedupes outgoing rects (Register/Update/skip) and routes incoming `activated` to adapters.
/// Reconnects with exponential backoff. All connect/IO failures are silent (EyeCtrl may be absent).
/// </summary>
public sealed class DwellClient : IDisposable
{
    private const int ConnectTimeoutMs = 2000;
    private const int MinBackoffMs = 1000;
    private const int MaxBackoffMs = 30000;

    private readonly ConcurrentDictionary<string, IDwellElementAdapter> _adapters = new();
    private readonly OutboundRectTracker _tracker = new();
    private readonly object _writeLock = new();

    // Outbound messages are queued and written by a single background consumer, so callers
    // (typically the app's UI thread, during register/layout) never block on pipe IO.
    private readonly Channel<WireMessage> _outbox =
        Channel.CreateUnbounded<WireMessage>(new UnboundedChannelOptions { SingleReader = true });

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;

    /// <summary>
    /// Named pipe this client connects to. Defaults to the production <see cref="DwellPipe.Name"/>;
    /// tests inject a unique GUID-suffixed name because pipe names are machine-global (issue #76).
    /// </summary>
    private readonly string _pipeName;

    /// <summary>Creates a client for the production <see cref="DwellPipe.Name"/> pipe.</summary>
    /// <remarks>
    /// Kept as a distinct parameterless constructor rather than an optional parameter on the
    /// overload below: this assembly ships to external SDK consumers on net8.0, and an optional
    /// parameter collapses the two into a single <c>.ctor(string)</c> in metadata, breaking
    /// already-compiled consumers (and XAML/Activator construction) with MissingMethodException.
    /// </remarks>
    public DwellClient() : this(null) { }

    /// <summary>
    /// Creates a client for the EyeCtrl dwell service's named pipe. Production heads use the
    /// parameterless overload; wire-protocol tests pass a GUID-suffixed name to reach a private
    /// server instance instead of racing every other process on the machine for the production pipe.
    /// </summary>
    public DwellClient(string? pipeName) => _pipeName = pipeName ?? DwellPipe.Name;

    /// <summary>Registers an adapter and sends its current rect. Idempotent per adapter id.</summary>
    public void RegisterAdapter(IDwellElementAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
        SendRect(adapter);
    }

    /// <summary>Removes the adapter with this id and tells the service it is no longer dwellable.</summary>
    public void UnregisterAdapter(string id)
    {
        _adapters.TryRemove(id, out _);
        lock (_tracker)
        {
            var msg = _tracker.OnUnregister(id);
            TrySend(msg);
        }
    }

    /// <summary>Re-reads and re-sends the adapter's rect (deduped); call when its layout may have changed.</summary>
    public void OnAdapterLayoutChanged(string id)
    {
        if (_adapters.TryGetValue(id, out var adapter)) SendRect(adapter);
    }

    /// <summary>Starts the background connect/read and send loops. Idempotent and thread-safe.</summary>
    public void Start()
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) != null)
        {
            cts.Dispose();
            return;
        }
        _ = RunAsync(cts.Token);
        _ = SendLoopAsync(cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoffMs;
        while (!_disposed && !token.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(ConnectTimeoutMs, token);
                lock (_writeLock)
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                backoff = MinBackoffMs;

                // Re-send current state after (re)connect. This runs on the background connect loop,
                // so adapters whose TryGetScreenRect is UI-thread-affine (WPF/WinUI) fail here and
                // return false; their rects are re-sent by the head's own UI-thread refresh loop once
                // the tracker is Reset. The resend still covers thread-agnostic adapters directly.
                lock (_tracker) _tracker.Reset();
                foreach (var adapter in _adapters.Values) SendRect(adapter);

                await ReadLoopAsync(_pipe, token);
            }
            catch (OperationCanceledException) { break; }
            catch { /* EyeCtrl not running or pipe broke */ }
            finally
            {
                lock (_writeLock) { _writer?.Dispose(); _writer = null; }
                _pipe?.Dispose(); _pipe = null;
            }

            if (_disposed || token.IsCancellationRequested) break;
            try { await Task.Delay(backoff, token); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null) break;
            try
            {
                if (JsonSerializer.Deserialize<WireMessage>(line, WireJson.Options) is ActivatedMessage a
                    && _adapters.TryGetValue(a.Id, out var adapter))
                {
                    adapter.Activate();
                }
            }
            catch { /* skip malformed */ }
        }
    }

    private void SendRect(IDwellElementAdapter adapter)
    {
        if (!adapter.TryGetScreenRect(out var rect)) return;
        lock (_tracker)
        {
            var msg = _tracker.OnRect(adapter.Id, rect, adapter.WindowHandle);
            if (msg != null) TrySend(msg);
        }
    }

    // Non-blocking enqueue; the actual write happens on the SendLoop consumer.
    private void TrySend(WireMessage msg) => _outbox.Writer.TryWrite(msg);

    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var msg in _outbox.Reader.ReadAllAsync(token))
            {
                var line = JsonSerializer.Serialize(msg, WireJson.Options);
                lock (_writeLock)
                {
                    if (_writer is null) continue;   // disconnected; reconnect re-sends current state
                    try { _writer.WriteLine(line); } catch { /* broke; reconnect handles */ }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Stops the background loops, drops the queue, and disconnects from the service.</summary>
    public void Dispose()
    {
        _disposed = true;
        _outbox.Writer.TryComplete();
        _cts?.Cancel();
        lock (_writeLock) { _writer?.Dispose(); _writer = null; }
        _pipe?.Dispose();
    }
}
