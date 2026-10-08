namespace EyeCtrl.Supervisor.Ipc;

using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;

/// <summary>
/// How one request ended. <see cref="Status"/> is the App's answer, or null when the App is not
/// available: no server, a connect or acknowledgement timeout, a server that is not the App, a
/// connection closed before answering, or an answer that does not parse. All of those mean the
/// same thing to the caller.
/// </summary>
/// <param name="Status">The App's status; null = the App is not available.</param>
/// <param name="Detail">Why, for the log.</param>
internal sealed record AppRequestResult(SettingRequestStatus? Status, string Detail)
{
    public static AppRequestResult NotAvailable(string detail) => new(null, detail);

    /// <summary>Whether the App acknowledged the value as saved.</summary>
    public bool IsSaved => Status is SettingRequestStatus.Saved or SettingRequestStatus.RestartInProgress;
}

/// <summary>
/// The Supervisor's client for the request channel (<see cref="SettingRequestChannel"/>): one
/// connection per request, one acknowledgement back.
/// </summary>
/// <remarks>
/// <para><b>Never blocks the Supervisor on the App.</b> The Supervisor exists for the case where
/// the App is hung, and a hung App still accepts the connection (the pipe's read loop runs on the
/// thread pool) and then never answers, because its dispatcher never runs the handler.
/// <see cref="SendAsync"/> therefore runs entirely on the thread pool and is bounded twice: the
/// connect by <see cref="ConnectTimeout"/> and the write-plus-acknowledgement by
/// <see cref="AckTimeout"/>. Each bound is a <c>Task.WhenAny</c> against a delay, not only a token,
/// so a pipe operation that ignores cancellation still cannot hold the result back.</para>
/// <para><b>Server identity.</b> Pipe names are machine-global; a squatter on the name would
/// otherwise collect requests and fake acknowledgements, and the Supervisor would drop a parked
/// value the App never saved. The connected server must be the App exe in this session (the
/// mirror of <c>HeartbeatPublisher.PipeServerIsSupervisor</c>); a mismatch is "not available".</para>
/// </remarks>
internal sealed class AppRequestClient
{
    /// <summary>Default bound on connecting.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Default bound on writing the request and reading the acknowledgement.</summary>
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<NamedPipeClientStream, bool> _serverIsApp;
    private readonly string _pipeName;

    /// <param name="serverIsApp">Decides whether the connected server is the App; production uses
    /// <see cref="ForApp"/>.</param>
    /// <param name="pipeName">Pipe to connect to; tests pass a GUID-suffixed name.</param>
    /// <param name="connectTimeout">Bound on connecting; default <see cref="DefaultConnectTimeout"/>.</param>
    /// <param name="ackTimeout">Bound on write plus acknowledgement; default <see cref="DefaultAckTimeout"/>.</param>
    public AppRequestClient(
        Func<NamedPipeClientStream, bool> serverIsApp,
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? ackTimeout = null)
    {
        _serverIsApp = serverIsApp;
        _pipeName = pipeName ?? SettingRequestChannel.PipeName;
        ConnectTimeout = connectTimeout ?? DefaultConnectTimeout;
        AckTimeout = ackTimeout ?? DefaultAckTimeout;
    }

    /// <summary>The production client: the server must be <paramref name="appExePath"/> in this session.</summary>
    public static AppRequestClient ForApp(string appExePath) => new(pipe => ServerIsApp(pipe, appExePath));

    public TimeSpan ConnectTimeout { get; }

    public TimeSpan AckTimeout { get; }

    /// <summary>
    /// Sends one request and waits, off the caller's thread, for its acknowledgement. Never throws;
    /// completes within <see cref="ConnectTimeout"/> + <see cref="AckTimeout"/> plus scheduling.
    /// </summary>
    public Task<AppRequestResult> SendAsync(SettingRequest request) =>
        Task.Run(async () =>
        {
            try
            {
                return await SendCoreAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return AppRequestResult.NotAvailable($"request failed: {ex.GetType().Name}: {ex.Message}");
            }
        });

    private async Task<AppRequestResult> SendCoreAsync(SettingRequest request)
    {
        using var cts = new CancellationTokenSource();
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // ConfigureAwait(false) throughout: nothing here may need the caller's context — the
            // Supervisor's dispatcher is exactly the thread that must stay free.
            var connectTask = client.ConnectAsync(cts.Token);
            if (await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout)).ConfigureAwait(false) != connectTask)
            {
                cts.Cancel();
                Observe(connectTask);
                return AppRequestResult.NotAvailable(string.Create(
                    CultureInfo.InvariantCulture, $"no server within {ConnectTimeout.TotalSeconds:0.#}s"));
            }
            await connectTask.ConfigureAwait(false);

            if (!_serverIsApp(client))
                return AppRequestResult.NotAvailable("the pipe's server is not the App");

            var exchange = ExchangeAsync(client, request, cts.Token);
            if (await Task.WhenAny(exchange, Task.Delay(AckTimeout)).ConfigureAwait(false) != exchange)
            {
                cts.Cancel();
                Observe(exchange);
                return AppRequestResult.NotAvailable(string.Create(
                    CultureInfo.InvariantCulture, $"no acknowledgement within {AckTimeout.TotalSeconds:0.#}s"));
            }
            return await exchange.ConfigureAwait(false);
        }
        finally
        {
            // Disposing aborts any pipe operation still pending after a timeout.
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<AppRequestResult> ExchangeAsync(
        NamedPipeClientStream client, SettingRequest request, CancellationToken token)
    {
        var line = SettingRequestChannel.Serialize(request) + "\n";
        await client.WriteAsync(Encoding.UTF8.GetBytes(line), token).ConfigureAwait(false);
        await client.FlushAsync(token).ConfigureAwait(false);

        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        var reply = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (reply is null)
            return AppRequestResult.NotAvailable("the App closed the connection before acknowledging");

        SettingRequestAck? ack;
        try { ack = SettingRequestChannel.ParseAck(reply); }
        catch (JsonException) { ack = null; }
        if (ack is null || ack.Id != request.Id)
            return AppRequestResult.NotAvailable("unreadable acknowledgement");

        return new AppRequestResult(ack.Status, $"acknowledged {ack.Status}");
    }

    /// <summary>An abandoned task may fault later; observe it so it never surfaces as an
    /// unobserved-task exception.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>
    /// Accepts the pipe's server only if it is the App exe at <paramref name="appExePath"/> in this
    /// Windows session — the same exact-path rule the heartbeat server's adoption binder applies
    /// to the App as a client. Fails closed.
    /// </summary>
    public static bool ServerIsApp(NamedPipeClientStream pipe, string appExePath)
    {
        try
        {
            if (!PipeServerIdentity.ServerIsSameSession(pipe)) return false;
            if (!PipeServerIdentity.TryGetServerExePath(pipe, out var exe) || exe is null) return false;
            return IdentityChecks.ExePathMatches(exe, appExePath);
        }
        catch
        {
            return false;
        }
    }
}
