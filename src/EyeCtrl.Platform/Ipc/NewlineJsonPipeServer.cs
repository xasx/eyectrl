namespace EyeCtrl.Platform.Ipc;

using System.IO;
using System.IO.Pipes;
using System.Text;

/// <summary>
/// The shared shape of EyeCtrl's two pipe servers (ExternalDwellService in the App,
/// HeartbeatPipeServer in the Supervisor): an idempotent Start, an accept loop over
/// multi-instance pipes, per-connection UTF-8 newline-JSON read loops that skip
/// malformed lines without dropping the connection, connection tracking under a lock,
/// and a Dispose that cancels, drains and disposes every connection. Callers own pipe
/// construction (plain vs ACL-secured), line parsing, and any per-connection teardown.
/// </summary>
public sealed class NewlineJsonPipeServer : IDisposable
{
    public delegate bool LineHandler(string line, NamedPipeServerStream pipe);

    private readonly string _pipeName;
    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly LineHandler _handleLine;
    private readonly Action<NamedPipeServerStream>? _onConnected;
    private readonly Action<NamedPipeServerStream>? _onDisconnected;
    private readonly Action<Exception, string?, string?>? _log;
    private readonly object _stateLock = new();
    private readonly List<NamedPipeServerStream> _connections = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public NewlineJsonPipeServer(
        string pipeName,
        Func<NamedPipeServerStream> createPipe,
        LineHandler handleLine,
        Action<NamedPipeServerStream>? onConnected = null,
        Action<NamedPipeServerStream>? onDisconnected = null,
        Action<Exception, string?, string?>? log = null)
    {
        _pipeName = pipeName;
        _createPipe = createPipe;
        _handleLine = handleLine;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _log = log;
    }

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = _createPipe();
            }
            catch (Exception ex)
            {
                // Pipe creation failed (e.g. ACL). Abort the loop, matching
                // HeartbeatPipeServer: retrying a construction that cannot succeed
                // would spin.
                _log?.Invoke(ex, null, "pipe create failed");
                break;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null, "pipe accept failed");
                pipe.Dispose();
                continue;
            }

            lock (_stateLock)
                _connections.Add(pipe);
            try
            {
                // The caller builds its per-connection state (writer, identity, bookkeeping)
                // here — before the first line can arrive. Without this seam the line handler
                // could never map a pipe to its state.
                _onConnected?.Invoke(pipe);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null, "onConnected failed");
            }
            _ = HandleConnectionAsync(pipe, token);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break; // read failure: drop this connection
                }
                if (line == null)
                    break;

                try
                {
                    // Malformed line -> handler returns false -> logged with the line, skipped,
                    // connection kept.
                    if (!_handleLine(line, pipe))
                        _log?.Invoke(new FormatException("malformed pipe line"), line, "bad pipe message");
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex, line, "bad pipe message");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null, "connection handler error");
            }
        finally
        {
            lock (_stateLock)
                _connections.Remove(pipe);
            try
            {
                _onDisconnected?.Invoke(pipe);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null, "onDisconnected failed");
            }
            try
            {
                pipe.Dispose();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        lock (_stateLock)
        {
            foreach (var c in _connections)
            {
                try
                {
                    c.Dispose();
                }
                catch
                {
                }
            }
            _connections.Clear();
        }
        _cts?.Dispose();
    }
}
