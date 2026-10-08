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

    /// <summary>The choice is on disk: the App acknowledged it, or a re-read found it there.</summary>
    Saved,

    /// <summary>The App was not available (or its save failed); the choice waits for a retry.</summary>
    Parked,

    /// <summary>The App refused the request as sent; re-sending cannot help.</summary>
    Refused,

    /// <summary>The value on disk changed after the choice parked — someone chose again in the App —
    /// so the newer choice wins and the parked one was dropped.</summary>
    Superseded,
}

/// <summary>
/// Delivers the user's latest choice for one App-owned setting over the request channel, and
/// keeps it until the App has saved it.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every member is called on the Supervisor's UI thread, and completions
/// are posted back to it through <c>postToUi</c>, so the state needs no lock. Nothing here waits:
/// sends and disk re-reads run in the background and post their outcome back.</para>
/// <para><b>Single-flight.</b> At most one request — or pre-send re-read — is in flight. A retry
/// trigger while one is pending is skipped; a choice made while one is pending replaces the wanted
/// value and is sent when the pending one completes.</para>
/// <para><b>Parking and retries.</b> The wanted value is cleared only when it is known to be on
/// disk. Anything else — no App, a timeout, a failed save — parks it. A parked choice is retried on
/// a verified heartbeat AND on the Supervisor's own clock tick, because an App running with
/// supervision off publishes no heartbeats. Retries back off exponentially from
/// <see cref="FirstRetryDelay"/> to <see cref="MaxRetryDelay"/>, for heartbeat and tick alike, and
/// the backoff resets on a new choice and on a save. Each failed attempt logs one line, so a
/// persistent failure logs once per backoff step.</para>
/// <para><b>The stale-park guard</b> (when <c>readDisk</c> is given). The disk value is recorded
/// when a choice parks, and re-read before every retry: a disk that already holds the choice
/// clears the park without a send; a disk that changed since the park means someone chose again in
/// the App, so the newer choice wins and the park is dropped. This is the reusable "clear only
/// after a re-read" step.</para>
/// <para><b>Restart.</b> A choice made while the user watches is "live" and may ask the App to
/// restart (T05b's language). Once it has been parked it is delivered later, while the user may be
/// working, so every retry is sent with <c>restart: false</c> — saved only.</para>
/// <para><b>Memory only.</b> A parked choice lives in this object, so a Supervisor exit or a reboot
/// loses it; the row's note says so.</para>
/// </remarks>
/// <typeparam name="T">The setting's value type.</typeparam>
internal sealed class SettingDelivery<T> where T : struct
{
    /// <summary>The wait before the first retry of a parked choice.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The cap the retry backoff doubles up to.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly string _name;
    private readonly Func<T, bool, SettingRequest> _build;
    private readonly Func<SettingRequest, Task<AppRequestResult>> _send;
    private readonly Action<Action> _postToUi;
    private readonly Func<T?>? _readDisk;
    private readonly Action<Action> _runInBackground;
    private readonly Func<DateTime> _utcNow;

    private T? _wanted;
    private bool _wantedIsLive;
    private bool _inFlight;
    private T? _diskAtPark;
    private int _failedAttempts;
    private DateTime? _nextRetryAt;

    /// <param name="name">The setting's name, for the log.</param>
    /// <param name="build">Builds the request for a value; the flag is whether it may restart.</param>
    /// <param name="send">Sends one request; production is <see cref="AppRequestClient.SendAsync"/>.</param>
    /// <param name="postToUi">Runs a completion on the UI thread (production:
    /// <c>Dispatcher.BeginInvoke</c>).</param>
    /// <param name="readDisk">The guarded disk read of this setting (null = cannot tell); enables
    /// the stale-park guard. Called off the UI thread.</param>
    /// <param name="runInBackground">Runs the pre-retry re-read off the UI thread; default
    /// <c>Task.Run</c>.</param>
    /// <param name="utcNow">The clock the backoff reads; default <see cref="DateTime.UtcNow"/>.</param>
    public SettingDelivery(
        string name,
        Func<T, bool, SettingRequest> build,
        Func<SettingRequest, Task<AppRequestResult>> send,
        Action<Action> postToUi,
        Func<T?>? readDisk = null,
        Action<Action>? runInBackground = null,
        Func<DateTime>? utcNow = null)
    {
        _name = name;
        _build = build;
        _send = send;
        _postToUi = postToUi;
        _readDisk = readDisk;
        _runInBackground = runInBackground ?? (work => Task.Run(work));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The choice not yet known to be saved; null when there is none.</summary>
    public T? Pending => _wanted;

    /// <summary>Whether a request or its pre-send re-read is in flight.</summary>
    public bool IsInFlight => _inFlight;

    /// <summary>Where the delivery stands.</summary>
    public SettingDeliveryState State { get; private set; } = SettingDeliveryState.Idle;

    /// <summary>The value last known to be saved in this session (acknowledged or re-read).</summary>
    public T? LastSaved { get; private set; }

    /// <summary>After <see cref="SettingDeliveryState.Superseded"/>: the newer value found on disk.</summary>
    public T? SupersededBy { get; private set; }

    /// <summary>When a parked choice may next be retried; null when no retry is waiting.</summary>
    public DateTime? NextRetryAt => _wanted is null ? null : _nextRetryAt;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="Pending"/>
    /// changes.</summary>
    public event Action? Changed;

    /// <summary>The user chose <paramref name="value"/> just now. A new choice is sent at once and
    /// resets the backoff and the stale-park guard.</summary>
    public void Request(T value)
    {
        _wanted = value;
        _wantedIsLive = true;
        ResetRetry();
        _diskAtPark = null;
        if (_inFlight)
        {
            // Single-flight: the pending send's completion sends this newer value.
            Changed?.Invoke();
            return;
        }
        Send();
    }

    /// <summary>A verified heartbeat arrived from a running App: retry a parked choice if due.</summary>
    public void OnAppHeartbeat() => RetryIfDue();

    /// <summary>The Supervisor's clock ticked: retry a parked choice if due. This is what delivers
    /// to an App running with supervision off, which publishes no heartbeats.</summary>
    public void OnRetryTick() => RetryIfDue();

    private void RetryIfDue()
    {
        if (_wanted is not { } wanted || _inFlight) return;
        if (_nextRetryAt is { } due && _utcNow() < due) return;
        if (_readDisk is null)
        {
            Send();
            return;
        }

        // The stale-park guard re-reads the disk first, off the UI thread; it holds the flight so
        // no second retry starts meanwhile.
        _inFlight = true;
        _runInBackground(() =>
        {
            var disk = ReadDiskSafely();
            _postToUi(() => GuardThenSend(wanted, disk));
        });
    }

    private void GuardThenSend(T guarded, T? disk)
    {
        _inFlight = false;
        if (_wanted is not { } wanted) return;
        if (!Same(wanted, guarded))
        {
            Send();   // a new choice arrived during the re-read: it is live, send it now
            return;
        }

        if (disk is { } onDisk && Same(onDisk, wanted))
        {
            Log.Information("{Setting}={Value}: already on disk; the parked choice is cleared without a send.", _name, wanted);
            _wanted = null;
            LastSaved = wanted;
            ResetRetry();
            State = SettingDeliveryState.Saved;
            Changed?.Invoke();
            return;
        }

        if (disk is { } now && _diskAtPark is { } atPark && !Same(now, atPark))
        {
            Log.Warning(
                "{Setting}={Value}: dropped — the disk changed from {AtPark} to {Now} since it parked, so the newer choice stays.",
                _name, wanted, atPark, now);
            _wanted = null;
            SupersededBy = now;
            ResetRetry();
            State = SettingDeliveryState.Superseded;
            Changed?.Invoke();
            return;
        }

        Send();
    }

    private void Send()
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
                // A choice that is about to park records the disk as it is now, still off the UI
                // thread: the stale-park guard compares later re-reads against it.
                T? disk = result.IsSaved || result.Status == SettingRequestStatus.Rejected ? null : ReadDiskSafely();
                _postToUi(() => Complete(value, request, result, disk));
            },
            CancellationToken.None,
            // Only posts to the UI thread, so it may run on whichever thread completed the send.
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Complete(T sent, SettingRequest request, AppRequestResult result, T? diskAfter)
    {
        _inFlight = false;
        var stillWanted = _wanted is { } wanted && Same(wanted, sent);

        if (result.IsSaved)
        {
            LastSaved = sent;
            ResetRetry();
            Log.Information("{Setting}={Value}: the App saved it ({Id}).", _name, sent, request.Id);
            if (stillWanted)
            {
                _wanted = null;
                State = SettingDeliveryState.Saved;
                Changed?.Invoke();
                return;
            }
            Send();   // a newer choice arrived meanwhile, and the App is answering: send it now
            return;
        }

        if (result.Status == SettingRequestStatus.Rejected)
        {
            Log.Warning("{Setting}={Value}: the App refused the request ({Id}).", _name, sent, request.Id);
            if (stillWanted)
            {
                _wanted = null;
                ResetRetry();
                State = SettingDeliveryState.Refused;
                Changed?.Invoke();
                return;
            }
            Send();
            return;
        }

        // Not available, or the App's save failed: keep the newest choice and retry it later,
        // backing off. From here on it is no longer live: a later delivery must never restart the
        // App. The disk as it was when the choice first parked is the guard's reference.
        _diskAtPark ??= diskAfter;
        _wantedIsLive = false;
        _failedAttempts++;
        var delay = RetryDelayAfter(_failedAttempts);
        _nextRetryAt = _utcNow() + delay;
        Log.Information(
            "{Setting}={Value}: parked; next try in {Delay:0}s ({Detail}).",
            _name, _wanted, delay.TotalSeconds, result.Detail);
        State = SettingDeliveryState.Parked;
        Changed?.Invoke();
    }

    /// <summary>The backoff after <paramref name="failedAttempts"/> failures: doubling from
    /// <see cref="FirstRetryDelay"/>, capped at <see cref="MaxRetryDelay"/>.</summary>
    internal static TimeSpan RetryDelayAfter(int failedAttempts)
    {
        var delay = FirstRetryDelay;
        for (var i = 1; i < failedAttempts && delay < MaxRetryDelay; i++)
            delay += delay;
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private void ResetRetry()
    {
        _failedAttempts = 0;
        _nextRetryAt = null;
    }

    private T? ReadDiskSafely()
    {
        if (_readDisk is null) return null;
        try { return _readDisk(); }
        catch { return null; }
    }

    private static bool Same(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);
}
