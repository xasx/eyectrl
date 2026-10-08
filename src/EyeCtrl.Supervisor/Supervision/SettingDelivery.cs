namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using Serilog;

/// <summary>Where one setting's delivery to the App stands.</summary>
internal enum SettingDeliveryState
{
    /// <summary>Nothing asked for yet in this Supervisor session.</summary>
    Idle,

    /// <summary>A request is in flight.</summary>
    Sending,

    /// <summary>The App acknowledged the latest choice as saved.</summary>
    Saved,

    /// <summary>The App was not available (or its save failed); the choice waits for the next
    /// heartbeat from a running App.</summary>
    Parked,

    /// <summary>The App refused the request as sent; re-sending cannot help.</summary>
    Refused,
}

/// <summary>
/// Delivers the user's latest choice for one App-owned setting over the request channel, and
/// keeps it until the App has saved it.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every member is called on the Supervisor's UI thread, and completions
/// are posted back to it through <c>postToUi</c>, so the state needs no lock. Nothing here waits:
/// <see cref="Request"/> and <see cref="OnAppHeartbeat"/> start the send and return; the client
/// bounds it off the UI thread.</para>
/// <para><b>Single-flight.</b> At most one request is in flight. Heartbeats arrive every few
/// seconds and a send may take up to its connect plus acknowledgement bounds, so a heartbeat while
/// one is pending is skipped; a choice made while one is pending replaces the wanted value and is
/// sent when the pending one completes.</para>
/// <para><b>Parking.</b> The wanted value is cleared ONLY by an acknowledgement that it was saved.
/// Anything else — no App, a timeout, a failed save — parks it, and the next verified heartbeat
/// from a running App retries it. A newer choice replaces an older parked one.</para>
/// <para><b>Restart.</b> A choice made while the user watches is "live" and may ask the App to
/// restart (T05b's language). Once it has been parked it is delivered later, while the user may be
/// working, so every retry is sent with <c>restart: false</c> — saved only.</para>
/// </remarks>
/// <typeparam name="T">The setting's value type.</typeparam>
internal sealed class SettingDelivery<T> where T : struct
{
    private readonly string _name;
    private readonly Func<T, bool, SettingRequest> _build;
    private readonly Func<SettingRequest, Task<AppRequestResult>> _send;
    private readonly Action<Action> _postToUi;

    private T? _wanted;
    private bool _wantedIsLive;
    private bool _inFlight;

    /// <param name="name">The setting's name, for the log.</param>
    /// <param name="build">Builds the request for a value; the flag is whether it may restart.</param>
    /// <param name="send">Sends one request; production is <see cref="AppRequestClient.SendAsync"/>.</param>
    /// <param name="postToUi">Runs a completion on the UI thread (production:
    /// <c>Dispatcher.BeginInvoke</c>).</param>
    public SettingDelivery(
        string name,
        Func<T, bool, SettingRequest> build,
        Func<SettingRequest, Task<AppRequestResult>> send,
        Action<Action> postToUi)
    {
        _name = name;
        _build = build;
        _send = send;
        _postToUi = postToUi;
    }

    /// <summary>The choice not yet acknowledged as saved; null when there is none.</summary>
    public T? Pending => _wanted;

    /// <summary>Whether a request is in flight.</summary>
    public bool IsInFlight => _inFlight;

    /// <summary>Where the delivery stands.</summary>
    public SettingDeliveryState State { get; private set; } = SettingDeliveryState.Idle;

    /// <summary>The value the App last acknowledged as saved in this session.</summary>
    public T? LastSaved { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="Pending"/>
    /// changes.</summary>
    public event Action? Changed;

    /// <summary>The user chose <paramref name="value"/> just now.</summary>
    public void Request(T value)
    {
        _wanted = value;
        _wantedIsLive = true;
        if (_inFlight)
        {
            // Single-flight: the pending send's completion sends this newer value.
            Changed?.Invoke();
            return;
        }
        Attempt();
    }

    /// <summary>A verified heartbeat arrived from a running App: retry a parked choice.</summary>
    public void OnAppHeartbeat()
    {
        if (_wanted is null || _inFlight) return;
        Attempt();
    }

    private void Attempt()
    {
        var value = _wanted!.Value;
        var request = _build(value, _wantedIsLive);
        _inFlight = true;
        State = SettingDeliveryState.Sending;
        Changed?.Invoke();

        Task<AppRequestResult> sending;
        try
        {
            sending = _send(request);
        }
        catch (Exception ex)
        {
            sending = Task.FromResult(AppRequestResult.NotAvailable($"send threw: {ex.Message}"));
        }

        _ = sending.ContinueWith(
            t =>
            {
                var result = t.IsCompletedSuccessfully
                    ? t.Result
                    : AppRequestResult.NotAvailable($"send faulted: {t.Exception?.GetBaseException().Message}");
                _postToUi(() => Complete(value, request, result));
            },
            CancellationToken.None,
            // Only posts to the UI thread, so it may run on whichever thread completed the send.
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Complete(T sent, SettingRequest request, AppRequestResult result)
    {
        _inFlight = false;
        var stillWanted = _wanted is { } wanted && EqualityComparer<T>.Default.Equals(wanted, sent);

        if (result.IsSaved)
        {
            LastSaved = sent;
            Log.Information("{Setting}={Value}: the App saved it ({Id}).", _name, sent, request.Id);
            if (stillWanted)
            {
                _wanted = null;
                State = SettingDeliveryState.Saved;
                Changed?.Invoke();
                return;
            }
            Attempt();   // a newer choice arrived meanwhile, and the App is answering: send it now
            return;
        }

        if (result.Status == SettingRequestStatus.Rejected)
        {
            Log.Warning("{Setting}={Value}: the App refused the request ({Id}).", _name, sent, request.Id);
            if (stillWanted)
            {
                _wanted = null;
                State = SettingDeliveryState.Refused;
                Changed?.Invoke();
                return;
            }
            Attempt();
            return;
        }

        // Not available, or the App's save failed: keep the newest choice for the next heartbeat.
        // From here on it is no longer live: a later delivery must never restart the App.
        Log.Information(
            "{Setting}={Value}: parked until the App answers ({Detail}).", _name, _wanted, result.Detail);
        _wantedIsLive = false;
        State = SettingDeliveryState.Parked;
        Changed?.Invoke();
    }
}
