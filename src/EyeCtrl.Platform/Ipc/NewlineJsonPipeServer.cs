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
/// Lines are bounded by <see cref="DefaultMaxLineLength"/> (constructor-overridable): a
/// line that exceeds the cap drops its connection — an unbounded ReadLine would let any
/// client grow the receiving process's memory without bound by streaming bytes with no
/// newline. The server itself keeps serving other connections.
/// </para>
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

    /// <summary>Default cap on one pipe line, in characters. Generous for both servers' real
    /// messages (heartbeat payloads and dwell register lines are a few hundred bytes).</summary>
    public const int DefaultMaxLineLength = 64 * 1024;

    /// <summary>Backoff bounds for pipe-create retries (name squatted / ACL failure): fast
    /// first retry so a transient race heals instantly, capped so a permanent squatter costs at
    /// most one attempt and one log line per interval.</summary>
    private const int CreateRetryMinMs = 500;
    private const int CreateRetryMaxMs = 30_000;

    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly Func<NamedPipeServerStream, TState> _createState;
    private readonly LineHandler _handleLine;
    private readonly Action<TState>? _onClosed;
    private readonly Action<Exception, string?, string?>? _log;
    private readonly int _maxLineLength;
    private readonly object _stateLock = new();
    private readonly List<NamedPipeServerStream> _connections = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <param name="createPipe">Builds each server instance — plain ctor or ACL-secured, caller's choice.</param>
    /// <param name="createState">Builds the per-connection state, once, before the first line can arrive.</param>
    /// <param name="handleLine">Consumes one line against that state; false means "unparseable".</param>
    /// <param name="onClosed">Per-connection teardown, run exactly once when the read loop ends.</param>
    /// <param name="log">(exception, line, context) — <c>line</c> is non-null only for a bad message.</param>
    /// <param name="maxLineLength">Cap on one line in characters; an overlong line drops the connection.</param>
    public NewlineJsonPipeServer(
        Func<NamedPipeServerStream> createPipe,
        Func<NamedPipeServerStream, TState> createState,
        LineHandler handleLine,
        Action<TState>? onClosed = null,
        Action<Exception, string?, string?>? log = null,
        int maxLineLength = DefaultMaxLineLength)
    {
        _createPipe = createPipe;
        _createState = createState;
        _handleLine = handleLine;
        _onClosed = onClosed;
        _log = log;
        _maxLineLength = maxLineLength;
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
        var createBackoffMs = CreateRetryMinMs;
        var createFailures = 0;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = _createPipe();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Pipe creation failed — typically ANOTHER PROCESS OWNS THE NAME (squatter), or
                // an ACL problem. Breaking here (the old behaviour) permanently disabled the
                // channel: a supervisor that loses its pipe once never recovers, and the app it
                // is supposed to watch runs unwatched. Retry with capped backoff instead: if the
                // squatter dies or releases the name, the channel comes back on its own. A
                // genuinely permanent failure still costs at most one log line + one attempt per
                // CreateRetryMaxMs, not a spin.
                createFailures++;
                _log?.Invoke(ex, null,
                    $"pipe create failed (attempt {createFailures}); retrying in {createBackoffMs}ms");
                try { await Task.Delay(createBackoffMs, token); }
                catch (OperationCanceledException) { break; }
                createBackoffMs = Math.Min(createBackoffMs * 2, CreateRetryMaxMs);
                continue;
            }

            if (createFailures > 0)
                _log?.Invoke(null, null, $"pipe create recovered after {createFailures} failed attempts");
            createFailures = 0;
            createBackoffMs = CreateRetryMinMs;

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
            var lineReader = new BoundedLineReader(reader, _maxLineLength);
            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await lineReader.ReadLineAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (LineTooLongException ex)
                {
                    // A line beyond the cap is a protocol violation, not a parse hiccup: the
                    // sender is either hostile or broken. Drop the connection; keep accepting.
                    _log?.Invoke(ex, null, $"pipe line exceeded {_maxLineLength} chars; dropping connection");
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

    /// <summary>
    /// Reads one newline-terminated line, refusing to buffer more than
    /// <paramref name="maxLineLength"/> characters: a client streaming bytes with no newline must
    /// not be able to grow the receiving process's memory without bound. Handles multiple lines
    /// per read chunk (carrying the remainder), the \r of a \r\n terminator (not counted against
    /// the cap), EOF mid-line (returns the partial line, matching StreamReader.ReadLineAsync),
    /// and EOF at a line boundary (returns null). Exceeding the cap throws
    /// <see cref="LineTooLongException"/>.
    /// </summary>
    private sealed class BoundedLineReader(StreamReader reader, int maxLineLength)
    {
        private readonly char[] _chunk = new char[256];
        private readonly StringBuilder _line = new(128);
        // The chunk buffer is aliased as the pending span: a new ReadAsync only happens after the
        // previous chunk was fully consumed, so the reuse never overwrites unread characters.
        private int _pendingLength;
        private int _pendingIndex;
        private bool _pendingCr;

        public async Task<string?> ReadLineAsync(CancellationToken token)
        {
            while (true)
            {
                while (_pendingIndex < _pendingLength)
                {
                    var ch = _chunk[_pendingIndex++];
                    if (ch == '\n')
                    {
                        _pendingCr = false;
                        var result = _line.ToString();
                        _line.Clear();
                        return result;
                    }
                    if (ch == '\r')
                    {
                        // A '\r' may be the first half of a "\r\n" terminator; decide on the next
                        // char. A SECOND consecutive '\r' is content, not a collapsed terminator
                        // (matches StreamReader only for the first CR — see the class doc): it is
                        // appended, which also means an endless "\r\r\r…" stream keeps growing the
                        // line and trips the cap instead of holding the connection open forever.
                        if (_pendingCr)
                        {
                            _line.Append('\r');
                            if (_line.Length > maxLineLength)
                                throw new LineTooLongException();
                        }
                        _pendingCr = true;
                        continue;
                    }
                    if (_pendingCr)
                    {
                        _line.Append('\r');
                        _pendingCr = false;
                    }
                    _line.Append(ch);
                    if (_line.Length > maxLineLength)
                        throw new LineTooLongException();
                }

                var n = await reader.ReadAsync(_chunk.AsMemory(), token);
                if (n == 0)
                {
                    if (_pendingCr)
                    {
                        _line.Append('\r');
                        _pendingCr = false;
                    }
                    if (_line.Length == 0)
                        return null;
                    var tail = _line.ToString();
                    _line.Clear();
                    return tail;
                }
                _pendingLength = n;
                _pendingIndex = 0;
            }
        }
    }

    private sealed class LineTooLongException : Exception
    {
        public LineTooLongException() : base("pipe line exceeded the maximum length") { }
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
