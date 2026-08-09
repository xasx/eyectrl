namespace EyeCtrl.Supervisor.Ipc;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;

/// <summary>
/// Pipe SERVER (supervisor side) for the <c>eyectrl_watch</c> channel. Accepts connections from
/// the app's <c>HeartbeatPublisher</c> (Task 8), reads newline-delimited JSON
/// <see cref="HeartbeatPayload"/> lines, binds each client's identity via
/// <see cref="ClientIdentityBinder"/> (security — Task 16), and raises
/// <see cref="HeartbeatReceived"/> per heartbeat. Tolerates zero connections (app not running /
/// standalone) — the accept loop simply has no clients.
/// </summary>
/// <remarks>
/// <para>Modeled closely on <c>EyeCtrl.App.Services.ExternalDwellService</c>'s accept-loop /
/// per-connection read-loop / connection-tracking / IDisposable shape, with two deliberate
/// departures from that template:</para>
/// <list type="bullet">
/// <item><b>Security:</b> the pipe is constructed with <see cref="NamedPipeServerStreamAcl.Create"/>
/// + <c>new NamedPipeSecurityBuilder().BuildCurrentUserOnly()</c> (per-user ACL — Task 16). The
/// template used the plain unsecured <see cref="NamedPipeServerStream"/> constructor; do NOT copy
/// that part.</item>
/// <item><b>JSON casing:</b> deserialization uses <see cref="JsonNamingPolicy.CamelCase"/> to match
/// the app's emitter. Without it, camelCase properties (<c>isPaused</c>/<c>lastFrameUtc</c>/
/// <c>shuttingDown</c>) would deserialize to defaults (false/MinValue). <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
/// is also set as a safety net so either casing on the wire deserializes correctly.</item>
/// </list>
/// <para><b>Threading:</b> a single fire-and-forget accept loop creates one
/// <see cref="NamedPipeServerStream"/> per client; each connection is serviced by its own
/// <see cref="HandleConnectionAsync"/> task. No background thread is consumed when zero clients are
/// connected (WaitForConnectionAsync is async).</para>
/// </summary>
public sealed class HeartbeatPipeServer : IDisposable
{
    /// <summary>Distinct from the existing <c>eyectrl_dwell</c> channel (ExternalDwellService).</summary>
    public const string PipeName = "eyectrl_watch";

    private readonly ClientIdentityBinder _binder;
    private readonly object _stateLock = new();
    private readonly List<NamedPipeServerStream> _connections = new();

    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <param name="binder">
    /// Used to bind each connected client's identity (PID/session/exe/creation-time) for the
    /// authenticated clean-shutdown check. Injected so the supervisor can wire the same binder the
    /// ProcessController captured at launch.
    /// </param>
    public HeartbeatPipeServer(ClientIdentityBinder binder)
    {
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
    }

    /// <summary>
    /// Raised on every received heartbeat. Second arg is the bound client identity, or <c>null</c>
    /// if binding failed (the subscriber should drop unbound heartbeats). Never throws back at the
    /// pipe server — handler exceptions are swallowed.
    /// </summary>
    public event Action<HeartbeatPayload, ClientIdentity?>? HeartbeatReceived;

    /// <summary>
    /// Idempotent on a CTS: starts the accept loop fire-and-forget. Safe to call when no app is
    /// running — the loop simply blocks on WaitForConnectionAsync with no clients.
    /// </summary>
    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Per-user ACL (Task 16): construct via NamedPipeServerStreamAcl.Create — NOT the plain
            // NamedPipeServerStream ctor used by ExternalDwellService.
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    new NamedPipeSecurityBuilder().BuildCurrentUserOnly());
            }
            catch (Exception)
            {
                // ACL/creation failure (e.g. another supervisor already owns the name): no point
                // retrying in a tight spin — abort the accept loop. The supervisor process itself
                // keeps running; it just won't receive heartbeats.
                break;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (Exception) { pipe.Dispose(); continue; }

            lock (_stateLock) _connections.Add(pipe);
            _ = HandleConnectionAsync(pipe, token);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        // Bind the client identity ONCE per connection (the client PID/exe/creation-time don't
        // change over the lifetime of a single named-pipe connection). null = unbound — heartbeats
        // are still read and the event still fires, but with a null identity so the subscriber can
        // drop them. The binder is documented null-safe (never throws); the outer try/catch is
        // defense-in-depth.
        ClientIdentity? identity = null;
        try { identity = _binder.Resolve(pipe.SafePipeHandle); }
        catch { /* tolerate any unexpected binder failure */ }

        try
        {
            using var reader = new StreamReader(
                pipe, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);

            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try { line = await reader.ReadLineAsync(token); }
                catch (OperationCanceledException) { break; }
                catch { break; } // read failure: drop this connection
                if (line == null) break;

                HeartbeatPayload? payload;
                try { payload = HeartbeatPayloadParser.Parse(line); }
                catch { continue; } // malformed JSON line: skip, keep the connection alive
                if (payload == null) continue;

                try { HeartbeatReceived?.Invoke(payload, identity); }
                catch { /* subscriber threw — never crash the pipe server */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* connection handler error: drop silently */ }
        finally
        {
            lock (_stateLock) _connections.Remove(pipe);
            try { pipe.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        lock (_stateLock)
        {
            foreach (var c in _connections) { try { c.Dispose(); } catch { } }
            _connections.Clear();
        }
        _cts?.Dispose();
    }
}
