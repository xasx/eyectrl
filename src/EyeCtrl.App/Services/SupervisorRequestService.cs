namespace EyeCtrl.App.Services;

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;
using EyeCtrl.Platform.Ipc;
using Microsoft.Extensions.Logging;

/// <summary>
/// The App's server for the Supervisor → App request channel (<see cref="SettingRequestChannel"/>).
/// The App is the only process allowed to save the App's settings; the Supervisor asks through
/// this pipe, and the App answers only after the save has completed.
/// </summary>
/// <remarks>
/// <para><b>Why a third pipe.</b> The heartbeat pipe is one-way (the App's client writes, the
/// Supervisor's server only reads), and the dwell pipe is the public SDK contract with no
/// client-exe check. This channel is private to the Supervisor.</para>
/// <para><b>Who may call.</b> The per-user ACL keeps other users out; the client's process must
/// also be the Supervisor exe in this session (<see cref="PipeClientIdentity"/>). Any other client
/// is dropped before its first line is read, so it learns nothing — to it the App is simply not
/// there.</para>
/// <para><b>Threading.</b> Lines arrive on the pipe's read loop (thread pool). Each request is
/// marshalled to the dispatcher, where it is applied and saved; the acknowledgement is then
/// written asynchronously from the pool. A hung dispatcher therefore costs only the
/// acknowledgement — the Supervisor times out — and never a pool thread.</para>
/// </remarks>
public sealed class SupervisorRequestService : IDisposable
{
    /// <summary>The production pipe name, shared with the Supervisor's client.</summary>
    public const string DefaultPipeName = SettingRequestChannel.PipeName;

    private readonly Func<SettingRequest, SettingRequestOutcome> _handle;
    private readonly Func<Func<SettingRequestOutcome>, Task<SettingRequestOutcome>> _runOnUi;
    private readonly ILogger _logger;
    private readonly NewlineJsonPipeServer<Connection> _pipeServer;

    /// <param name="handle">Applies and saves one request; runs on the UI thread. Its outcome's
    /// <see cref="SettingRequestOutcome.AfterAck"/> runs once the acknowledgement is out.</param>
    /// <param name="runOnUi">Runs work on the dispatcher (production:
    /// <c>work =&gt; Dispatcher.InvokeAsync(work).Task</c>).</param>
    /// <param name="logger">Where refusals, bad lines and write failures are logged.</param>
    /// <param name="pipeName">Pipe to serve on; tests pass <see cref="DefaultPipeName"/> +
    /// <c>"_test_" + Guid</c>, because pipe names are machine-global.</param>
    /// <param name="isAuthorizedClient">Decides whether a connected client may send requests.
    /// Defaults to <see cref="IsSupervisorClient(NamedPipeServerStream)"/>; tests whose client is the test host itself
    /// pass their own.</param>
    public SupervisorRequestService(
        Func<SettingRequest, SettingRequestOutcome> handle,
        Func<Func<SettingRequestOutcome>, Task<SettingRequestOutcome>> runOnUi,
        ILogger logger,
        string? pipeName = null,
        Func<NamedPipeServerStream, bool>? isAuthorizedClient = null)
    {
        _handle = handle;
        _runOnUi = runOnUi;
        _logger = logger;
        var name = pipeName ?? DefaultPipeName;
        var authorize = isAuthorizedClient ?? IsSupervisorClient;

        _pipeServer = new NewlineJsonPipeServer<Connection>(
            () => NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0,
                new NamedPipeSecurityBuilder().BuildCurrentUserOnly()),
            createState: pipe =>
            {
                // Throwing here makes the shared server drop the connection unread and log it.
                if (!authorize(pipe))
                    throw new UnauthorizedAccessException("request-channel client is not the Supervisor");
                return new Connection(pipe);
            },
            handleLine: (line, connection) =>
            {
                var request = SettingRequestChannel.ParseRequest(line);
                if (request is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Kind))
                    return false;
                _ = ProcessAsync(request, connection);
                return true;
            },
            onClosed: connection => connection.Dispose(),
            log: (ex, line, context) =>
            {
                if (line is not null)
                    _logger.LogWarning(ex, "Supervisor request pipe: bad message {Line}", line);
                else
                    _logger.LogWarning(ex, "Supervisor request pipe: {Context}", context ?? "connection handler error");
            });
    }

    /// <summary>Starts accepting. Idempotent.</summary>
    public void Start() => _pipeServer.Start();

    private async Task ProcessAsync(SettingRequest request, Connection connection)
    {
        SettingRequestOutcome outcome;
        try
        {
            outcome = await _runOnUi(() => _handle(request)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supervisor request {Id}: handler threw.", request.Id);
            outcome = new SettingRequestOutcome(SettingRequestStatus.Failed);
        }

        var status = outcome.Status;

        try
        {
            await connection.SendAsync(SettingRequestChannel.Serialize(new SettingRequestAck(request.Id, status)))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The Supervisor gave up waiting or went away. If the save succeeded, its parked
            // copy is re-sent and saved again — requests are idempotent.
            _logger.LogInformation(ex, "Supervisor request {Id}: acknowledgement ({Status}) not delivered.", request.Id, status);
        }

        // Hook (b): what must not happen before the acknowledgement is out — a restart would
        // otherwise dispose this channel (OnExit) before the ack leaves, and the Supervisor would
        // park a change that just worked. It runs whether or not the write succeeded, so a dead
        // pipe never costs the action itself.
        if (outcome.AfterAck is { } afterAck)
        {
            try
            {
                await _runOnUi(() =>
                {
                    afterAck();
                    return outcome;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supervisor request {Id}: the after-acknowledgement action threw.", request.Id);
            }
        }
    }

    /// <summary>
    /// The production client check: the connected process is the Supervisor exe (by file name —
    /// the Supervisor may run from a relocated copy, the same rule the heartbeat publisher applies
    /// to the Supervisor's server) in this Windows session. Fails closed.
    /// </summary>
    public static bool IsSupervisorClient(NamedPipeServerStream pipe) =>
        IsSupervisorClient(pipe, SupervisorLauncher.ResolveExePath());

    /// <summary>
    /// The client check against an explicit Supervisor exe: the connected process runs in this
    /// session and its exe has the same FILE NAME as <paramref name="expectedSupervisorExe"/> (any
    /// directory). Null — no Supervisor installed — refuses. Fails closed.
    /// </summary>
    public static bool IsSupervisorClient(NamedPipeServerStream pipe, string? expectedSupervisorExe)
    {
        try
        {
            if (expectedSupervisorExe is null) return false;
            if (!PipeClientIdentity.ClientIsSameSession(pipe)) return false;
            if (!PipeClientIdentity.TryGetClientExePath(pipe, out var exe) || exe is null) return false;
            return string.Equals(
                Path.GetFileName(exe), Path.GetFileName(expectedSupervisorExe), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _pipeServer.Dispose();

    /// <summary>One accepted Supervisor connection: serialised asynchronous writes, so two
    /// acknowledgements on one connection never interleave.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _closed = new();

        public Connection(NamedPipeServerStream pipe) => _pipe = pipe;

        public async Task SendAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _writeLock.WaitAsync(_closed.Token).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(bytes, _closed.Token).ConfigureAwait(false);
                await _pipe.FlushAsync(_closed.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            // Cancels a pending write; the shared server disposes the pipe itself.
            try { _closed.Cancel(); } catch { }
        }
    }
}
