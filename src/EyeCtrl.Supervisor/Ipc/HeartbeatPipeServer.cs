namespace EyeCtrl.Supervisor.Ipc;

using System;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;
using EyeCtrl.Platform.Ipc;

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
/// <para><b>Threading:</b> delegated to <see cref="NewlineJsonPipeServer"/> —
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
    private readonly ConditionalWeakTable<NamedPipeServerStream, ClientIdentity> _identityByPipe = new();
    private readonly NewlineJsonPipeServer _pipeServer;

    /// <param name="binder">
    /// Used to bind each connected client's identity (PID/session/exe/creation-time) for the
    /// authenticated clean-shutdown check. Injected so the supervisor can wire the same binder the
    /// ProcessController captured at launch.
    /// </param>
    public HeartbeatPipeServer(ClientIdentityBinder binder, string? pipeName = null)
    {
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        _pipeName = pipeName ?? PipeName;

        _pipeServer = new NewlineJsonPipeServer(
            _pipeName,
            () => NamedPipeServerStreamAcl.Create(
                _pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0,
                new NamedPipeSecurityBuilder().BuildCurrentUserOnly()),
            onConnected: pipe =>
            {
                try { _identityByPipe.Add(pipe, _binder.Resolve(pipe.SafePipeHandle)!); }
                catch { /* unresolvable identity = null identity, same as today's latch */ }
            },
            handleLine: (line, pipe) =>
            {
                var payload = HeartbeatPayloadParser.Parse(line);
                if (payload is null)
                    return false;
                _identityByPipe.TryGetValue(pipe, out var identity);
                HeartbeatReceived?.Invoke(payload, identity);
                return true;
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
