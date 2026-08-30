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
/// construction (plain vs ACL-secured) and line parsing.
/// <para>
/// Per-connection state is a first-class parameter rather than something each caller
/// re-invents: <typeparamref name="TState"/> is built once per accepted pipe by
/// <c>createState</c>, handed to every line handler, and released by <c>onClosed</c>. It
/// lives in the read loop's own frame, so there is no pipe-keyed side table to keep in
/// sync and no per-line lookup — the reason <see cref="NamedPipeServerStream"/> appears
/// nowhere in either caller's vocabulary except its own pipe factory.
/// </para>
/// </summary>
/// <typeparam name="TState">
/// Whatever the caller needs alongside a connection — a writer, a bound client identity,
/// registration bookkeeping. May be a nullable reference type when "no state" is a valid
/// outcome (the supervisor binds an unresolvable client to a null identity).
/// </typeparam>
public sealed class NewlineJsonPipeServer<TState> : IDisposable
{
    /// <summary>
    /// Handles one complete line. Return false for a line the caller could not parse — it is
    /// logged with the line and skipped, and the connection is kept.
    /// </summary>
    public delegate bool LineHandler(string line, TState state);

    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly Func<NamedPipeServerStream, TState> _createState;
    private readonly LineHandler _handleLine;
    private readonly Action<TState>? _onClosed;
    private readonly Action<Exception, string?, string?>? _log;
    private readonly object _stateLock = new();
    private readonly List<NamedPipeServerStream> _connections = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <param name="createPipe">Builds each server instance — plain ctor or ACL-secured, caller's choice.</param>
    /// <param name="createState">Builds the per-connection state, once, before the first line can arrive.</param>
    /// <param name="handleLine">Consumes one line against that state; false means "unparseable".</param>
    /// <param name="onClosed">Per-connection teardown, run exactly once when the read loop ends.</param>
    /// <param name="log">(exception, line, context) — <c>line</c> is non-null only for a bad message.</param>
    public NewlineJsonPipeServer(
        Func<NamedPipeServerStream> createPipe,
        Func<NamedPipeServerStream, TState> createState,
        LineHandler handleLine,
        Action<TState>? onClosed = null,
        Action<Exception, string?, string?>? log = null)
    {
        _createPipe = createPipe;
        _createState = createState;
        _handleLine = handleLine;
        _onClosed = onClosed;
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
                // Pipe creation failed (e.g. ACL, or another process already owns the name).
                // Abort the loop: retrying a construction that cannot succeed would spin. This
                // permanently disables the channel, so callers should wire `log` to see it.
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

            TState state;
            try
            {
                state = _createState(pipe);
            }
            catch (Exception ex)
            {
                // A connection whose state could not be built can be neither answered nor
                // bookkept; servicing it would silently swallow every line it sends. Drop it and
                // keep accepting — the client sees the close and can reconnect.
                _log?.Invoke(ex, null, "connection state setup failed");
                pipe.Dispose();
                continue;
            }

            lock (_stateLock)
                _connections.Add(pipe);
            _ = HandleConnectionAsync(pipe, state, token);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, TState state, CancellationToken token)
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
                    if (!_handleLine(line, state))
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
                _onClosed?.Invoke(state);
            }
            catch (Exception ex)
            {
                _log?.Invoke(ex, null, "connection teardown failed");
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
