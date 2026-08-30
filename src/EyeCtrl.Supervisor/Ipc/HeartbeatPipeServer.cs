namespace EyeCtrl.Supervisor.Ipc;

using System;
using System.IO.Pipes;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;
using EyeCtrl.Platform.Ipc;
using Serilog;

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
/// <para><b>Threading:</b> delegated to <see cref="NewlineJsonPipeServer{TState}"/> —
/// a single fire-and-forget accept loop creates one
/// <see cref="NamedPipeServerStream"/> per client; each connection is serviced by its own
/// read-loop task. No background thread is consumed when zero clients are
/// connected (WaitForConnectionAsync is async).</para>
/// </summary>
public sealed class HeartbeatPipeServer : IDisposable
{
    /// <summary>Distinct from the existing <c>eyectrl_dwell</c> channel (ExternalDwellService).</summary>
    public const string PipeName = "eyectrl_watch";

    private readonly ClientIdentityBinder _binder;
    private readonly string _pipeName;
    // The bound identity IS this channel's per-connection state — resolved once per pipe and
    // carried by the read loop itself, so there is no pipe-keyed table to keep in sync.
    private readonly NewlineJsonPipeServer<ClientIdentity?> _pipeServer;

    /// <param name="binder">
    /// Used to bind each connected client's identity (PID/session/exe/creation-time) for the
    /// authenticated clean-shutdown check. Injected so the supervisor can wire the same binder the
    /// ProcessController captured at launch.
    /// </param>
    public HeartbeatPipeServer(ClientIdentityBinder binder, string? pipeName = null)
    {
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        _pipeName = pipeName ?? PipeName;

        _pipeServer = new NewlineJsonPipeServer<ClientIdentity?>(
            () => NamedPipeServerStreamAcl.Create(
                _pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0,
                new NamedPipeSecurityBuilder().BuildCurrentUserOnly()),
            createState: pipe =>
            {
                // Bound ONCE per connection: PID/exe/creation-time cannot change over the life of
                // a single named-pipe connection. The binder is documented never to throw; the
                // catch is defence-in-depth, and must yield a null identity rather than propagate
                // — a throw here would drop the connection and cost the supervisor its heartbeats.
                try { return _binder.Resolve(pipe.SafePipeHandle); }
                catch { return null; }
            },
            handleLine: (line, identity) =>
            {
                var payload = HeartbeatPayloadParser.Parse(line);
                if (payload is null)
                    return false;
                // Contain subscriber faults here rather than letting them surface through the
                // line-handler catch as "unparseable line": the line parsed fine, and a throwing
                // subscriber must never cost us the heartbeat channel.
                try { HeartbeatReceived?.Invoke(payload, identity); }
                catch (Exception ex) { Log.Warning(ex, "Heartbeat pipe: subscriber threw"); }
                return true;
            },
            log: (ex, line, context) =>
            {
                // "pipe create failed" aborts the accept loop for good — the supervisor then
                // never receives a heartbeat and silently stops adopting or supervising, while
                // Program still reports the pipe as started. That is the case this seam exists
                // for; per-line parse noise stays at Debug.
                if (line is not null)
                    Log.Debug(ex, "Heartbeat pipe: discarding unparseable line {Line}", line);
                else
                    Log.Warning(ex, "Heartbeat pipe: {Context}", context ?? "connection handler error");
            });
    }

    /// <summary>
    /// Raised on every received heartbeat. Second arg is the bound client identity, or <c>null</c>
    /// if binding failed (the subscriber should drop unbound heartbeats). Never throws back at the
    /// pipe server — handler exceptions are swallowed.
    /// </summary>
    public event Action<HeartbeatPayload, ClientIdentity?>? HeartbeatReceived;

    public void Start() => _pipeServer.Start();

    public void Dispose() => _pipeServer.Dispose();
}
