namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// T05b's language delivery, as Program wires it: <c>SetUiLanguage</c> requests, the guarded
/// re-read as the clearing rule (<c>confirmSaveByReRead</c>), the live flag, and the pending choice
/// riding on the Supervisor's next launch as <c>--ui-language</c>. Sends are tasks the test
/// completes; background work is a queue the test drains, so "off the UI thread" is observable.
/// </summary>
public class LanguageDeliveryTests
{
    private readonly List<(SettingRequest Request, TaskCompletionSource<AppRequestResult> Reply)> _sends = new();
    private readonly Queue<Action> _background = new();
    private UiLanguage? _disk = UiLanguage.English;
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly SettingDelivery<UiLanguage> _delivery;

    public LanguageDeliveryTests()
    {
        _delivery = new SettingDelivery<UiLanguage>(
            "UiLanguage",
            (language, restart) => SettingRequest.SetUiLanguage(language, restart),
            request =>
            {
                var reply = new TaskCompletionSource<AppRequestResult>();
                _sends.Add((request, reply));
                return reply.Task;
            },
            work => work(),
            readDisk: () => _disk,
            runInBackground: _background.Enqueue,
            utcNow: () => _now,
            confirmSaveByReRead: true);
    }

    private static AppRequestResult Saved => new(SettingRequestStatus.Saved, "test");

    private static AppRequestResult NotAvailable => AppRequestResult.NotAvailable("test: no App");

    private void Drain()
    {
        while (_background.Count > 0) _background.Dequeue()();
    }

    private void Reply(int send, AppRequestResult result)
    {
        _sends[send].Reply.SetResult(result);
        Drain();
    }

    private void RetryWhenDue()
    {
        if (_delivery.NextRetryAt is { } due && due > _now) _now = due;
        _delivery.OnRetryTick();
        Drain();
    }

    private static string Token(SettingRequest request) => request.Value.GetString()!;

    // ── Hook (a): clear only after a re-read ───────────────────────────────────────────────

    [Fact]
    public void ASavedAcknowledgement_ClearsOnlyOnceTheReReadFindsTheChoice()
    {
        _delivery.Request(UiLanguage.German);
        _sends[0].Reply.SetResult(Saved);

        // Acknowledged, but the confirming re-read has not run: still pending, still in flight.
        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.German);
        Check.That(_delivery.IsInFlight).IsTrue();
        Check.That(_background).HasSize(1);

        _disk = UiLanguage.German;               // the App's save is on disk
        Drain();

        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
        Check.That(_delivery.LastSaved).IsEqualTo(UiLanguage.German);
    }

    [Fact]
    public void ASavedAcknowledgementTheReReadCannotConfirm_KeepsTheChoicePending_AndRetriesWithoutRestart()
    {
        _delivery.Request(UiLanguage.German);
        _disk = null;                             // the re-read cannot tell (writer contended)
        Reply(0, Saved);

        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.German);
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Parked);

        RetryWhenDue();
        Check.That(_sends).HasSize(2);
        Check.That(Token(_sends[1].Request)).IsEqualTo("german");
        Check.That(_sends[1].Request.Restart).IsFalse();
    }

    // ── The live flag (review-02's G5) ─────────────────────────────────────────────────────

    [Fact]
    public void ALiveChoiceMadeDuringTheReRead_IsSentLive_EvenWhenTheDiskAlreadyHoldsIt()
    {
        // Parked German; the App picked it up some other way (a flagged launch), so the disk says
        // German. The caretaker now chooses German again, live, during the guard's re-read. Value
        // equality would clear it as "already saved" and swallow the restart the caretaker asked
        // for; the live flag sends it.
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        _disk = UiLanguage.German;
        if (_delivery.NextRetryAt is { } due) _now = due;
        _delivery.OnRetryTick();                  // the re-read is queued, not run

        _delivery.Request(UiLanguage.German);     // live, same value
        Drain();

        Check.That(_sends).HasSize(2);
        Check.That(Token(_sends[1].Request)).IsEqualTo("german");
        Check.That(_sends[1].Request.Restart).IsTrue();
    }

    [Fact]
    public void ALiveRePickOfTheValueInFlight_GoesOutAgainLive_AfterTheAck()
    {
        // A parked retry (restart:false) is in flight when the caretaker picks the same language
        // live. Its acknowledgement must not clear the live choice: it is sent again, restart:true.
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        RetryWhenDue();                           // retry in flight, restart:false
        Check.That(_sends[1].Request.Restart).IsFalse();

        _delivery.Request(UiLanguage.German);     // live, same value, during the flight
        Reply(1, Saved);

        Check.That(_sends).HasSize(3);
        Check.That(_sends[2].Request.Restart).IsTrue();
    }

    // ── App down: the next launch, and a flagged launch that loses the guard ───────────────

    [Fact]
    public void AppDown_TheNextLaunchCarriesThePendingChoice_AndItClearsOnlyAfterTheReRead()
    {
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.German);

        // Program's launch: _pc.Launch(_appExePath, paused, uiLanguage: _languageDelivery.Pending).
        var started = new List<ProcessStartInfo>();
        var controller = new ProcessController(
            psi => { started.Add(psi); return null; },
            _ => null);
        controller.Launch(@"C:\EyeCtrl\EyeCtrl.App.exe", paused: false, uiLanguage: _delivery.Pending);

        Check.That(started).HasSize(1);
        Check.That(started[0].ArgumentList).ContainsExactly("--ui-language", "de");
        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.German);   // the launch clears nothing

        _disk = UiLanguage.German;                // the App applied the flag and saved it
        RetryWhenDue();

        Check.That(_sends).HasSize(1);            // cleared by the re-read, no send
        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
    }

    [Fact]
    public void AFlaggedLaunchThatLosesTheGuard_LeavesThePendingChoice_DeliveredSavedOnlyOnTheNextHeartbeat()
    {
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);

        // The flagged launch lost the single-instance guard to a still-running App and exited
        // before reading the flag: the disk still says English. That App's heartbeat arrives.
        if (_delivery.NextRetryAt is { } due) _now = due;
        _delivery.OnAppHeartbeat();
        Drain();

        Check.That(_sends).HasSize(2);
        Check.That(Token(_sends[1].Request)).IsEqualTo("german");
        Check.That(_sends[1].Request.Restart).IsFalse();   // saved, never a restart while working

        _sends[1].Reply.SetResult(Saved);
        _disk = UiLanguage.German;
        Drain();
        Check.That(_delivery.Pending).IsNull();
    }

    [Fact]
    public void ALaunchWithNoPendingChoice_CarriesNoLanguageFlag()
    {
        var started = new List<ProcessStartInfo>();
        var controller = new ProcessController(psi => { started.Add(psi); return null; }, _ => null);

        controller.Launch(@"C:\EyeCtrl\EyeCtrl.App.exe", paused: true, uiLanguage: null);

        Check.That(started[0].ArgumentList).ContainsExactly("--paused");
    }

    [Theory]
    [InlineData(UiLanguage.System, "system")]
    [InlineData(UiLanguage.German, "de")]
    [InlineData(UiLanguage.English, "en")]
    public void TheLaunchFlag_UsesTheSingleTokenMapping(UiLanguage language, string token)
    {
        var started = new List<ProcessStartInfo>();
        var controller = new ProcessController(psi => { started.Add(psi); return null; }, _ => null);

        controller.Launch(@"C:\EyeCtrl\EyeCtrl.App.exe", paused: false, uiLanguage: language);

        Check.That(started[0].ArgumentList).ContainsExactly("--ui-language", token);
    }

    [Fact]
    public void TheRequest_CarriesTheSettingsJsonToken()
    {
        Check.That(SettingRequestChannel.Serialize(SettingRequest.SetUiLanguage(UiLanguage.German, restart: true) with { Id = "x" }))
            .IsEqualTo("""{"id":"x","kind":"setUiLanguage","value":"german","restart":true}""");
    }

    // ── T05b fixup-01 F1: a stale --ui-language never overrides a newer choice ─────────────

    private (ProcessController Controller, List<ProcessStartInfo> Started) Launcher()
    {
        var started = new List<ProcessStartInfo>();
        return (new ProcessController(psi => { started.Add(psi); return null; }, _ => null), started);
    }

    /// <summary>Program's launch, as wired at both call sites.</summary>
    private void Launch(ProcessController controller)
    {
        controller.Launch(@"C:\EyeCtrl\EyeCtrl.App.exe", paused: false, uiLanguage: _delivery.LaunchArgument());
        _delivery.AppLaunched();
    }

    [Fact]
    public void TheReviewScenario_ARelaunchAfterTheUserRevertedInTheApp_CarriesNoFlag_AndTheRevertStays()
    {
        // 1. The caretaker picks Deutsch while the App is down.
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        // 2. The App starts with --ui-language de and saves it.
        var (controller, started) = Launcher();
        Launch(controller);
        Check.That(started[0].ArgumentList).ContainsExactly("--ui-language", "de");
        _disk = UiLanguage.German;
        // Its first heartbeat settles the choice at once, well inside the backoff window.
        _delivery.OnAppHeartbeat();
        Drain();
        Check.That(_delivery.Pending).IsNull();
        // 3. Within the backoff window the user switches back to English in the App...
        _disk = UiLanguage.English;
        // 4. ...whose supervised language restart is a Supervisor relaunch.
        Launch(controller);

        // 5. The relaunch passes nothing, and English stays.
        Check.That(started[1].ArgumentList).IsEmpty();
        Check.That(_disk).IsEqualTo(UiLanguage.English);
        Check.That(_sends).HasSize(1);
    }

    [Fact]
    public void TheLaunchArgument_IsPassedAtMostOncePerChoice()
    {
        // Part 2 on its own: no heartbeat arrives between the two launches, so the choice is still
        // pending at the relaunch — and still must not ride on it.
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        var (controller, started) = Launcher();

        Launch(controller);
        _disk = UiLanguage.English;               // the App applied de; the user reverted
        Launch(controller);

        Check.That(started[0].ArgumentList).ContainsExactly("--ui-language", "de");
        Check.That(started[1].ArgumentList).IsEmpty();
        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.German);   // still delivered by the channel

        _delivery.Request(UiLanguage.English);    // a NEW choice may ride on a launch again
        Reply(1, NotAvailable);
        Launch(controller);
        Check.That(started[2].ArgumentList).ContainsExactly("--ui-language", "en");
    }

    [Fact]
    public void TheFirstHeartbeatAfterALaunch_RetriesAtOnce_IgnoringTheBackoff()
    {
        // Part 1 on its own: the retry is 5 s away when the launched App's first heartbeat comes.
        // An App was heartbeating a second ago (no gap), so only the launch makes this immediate.
        _delivery.OnAppHeartbeat();
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);
        Check.That(_delivery.NextRetryAt).IsEqualTo(_now + TimeSpan.FromSeconds(5));
        var (controller, _) = Launcher();
        Launch(controller);
        _disk = UiLanguage.German;

        _now += TimeSpan.FromSeconds(1);          // the backoff has not passed
        _delivery.OnAppHeartbeat();
        Drain();

        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
    }

    [Fact]
    public void TheFirstHeartbeatAfterAGap_RetriesAtOnce_IgnoringTheBackoff()
    {
        var t0 = _now;
        _delivery.OnAppHeartbeat();               // t=0, nothing pending
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);                   // retry due at 5
        _now = t0 + TimeSpan.FromSeconds(3);
        _delivery.OnAppHeartbeat();               // t=3: steady heartbeat, not due
        Check.That(_sends).HasSize(1);
        _now = t0 + TimeSpan.FromSeconds(5);
        _delivery.OnRetryTick();
        Drain();
        Reply(1, NotAvailable);                   // t=5: fails again, due at 15
        Check.That(_delivery.NextRetryAt).IsEqualTo(t0 + TimeSpan.FromSeconds(15));

        _now = t0 + TimeSpan.FromSeconds(14);     // 11 s without a heartbeat: the App went away
        _delivery.OnAppHeartbeat();               // and is back; the backoff is still 1 s off
        Drain();

        Check.That(_sends).HasSize(3);
        Check.That(_sends[2].Request.Restart).IsFalse();
    }

    [Fact]
    public void TheImmediateRetry_SurvivesAHeartbeatThatArrivesWhileASendIsInFlight()
    {
        _delivery.OnAppHeartbeat();
        _delivery.Request(UiLanguage.German);     // live send in flight
        var (controller, _) = Launcher();
        Launch(controller);

        _now += TimeSpan.FromSeconds(1);
        _delivery.OnAppHeartbeat();               // first heartbeat after the launch: in flight, skipped
        Check.That(_sends).HasSize(1);
        Reply(0, NotAvailable);                   // parks, retry due in 5 s

        _now += TimeSpan.FromSeconds(1);
        _delivery.OnAppHeartbeat();               // the next heartbeat still retries at once
        Drain();

        Check.That(_sends).HasSize(2);
        Check.That(_sends[1].Request.Restart).IsFalse();
    }

    // ── T05b fixup-01 F3 / F4 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AChoiceMadeDuringTheConfirmingReRead_IsSentLive_NotCleared()
    {
        // T3: the saved ack for German starts its confirming re-read; the caretaker picks English
        // before it completes. The re-read finds German on disk — which must not clear English.
        _delivery.Request(UiLanguage.German);
        _sends[0].Reply.SetResult(Saved);         // the confirm read is queued, not run
        Check.That(_background).HasSize(1);

        _delivery.Request(UiLanguage.English);
        _disk = UiLanguage.German;
        Drain();

        Check.That(_sends).HasSize(2);
        Check.That(Token(_sends[1].Request)).IsEqualTo("english");
        Check.That(_sends[1].Request.Restart).IsTrue();
        Check.That(_delivery.Pending).IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void AReadableReReadThatDisagreesWithTheAck_SupersedesTheChoice()
    {
        // F4: the App saved German, and the disk already says System — the user chose again in
        // the App right after. The newer choice stays; German is not parked to be resent over it.
        _delivery.Request(UiLanguage.German);
        _disk = UiLanguage.System;
        Reply(0, Saved);

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Superseded);
        Check.That(_delivery.SupersededBy).IsEqualTo(UiLanguage.System);
        Check.That(_delivery.Pending).IsNull();
        RetryWhenDue();
        Check.That(_sends).HasSize(1);
    }

    [Fact]
    public void TheFirstHeartbeatEver_RetriesAtOnce_IgnoringTheBackoff()
    {
        // An App the Supervisor has never heard from (it was already running, or started by hand)
        // counts as freshly come up: its first heartbeat retries a pending choice at once.
        _delivery.Request(UiLanguage.German);
        Reply(0, NotAvailable);                   // retry due in 5 s; no heartbeat seen yet, no launch
        _disk = UiLanguage.German;

        _delivery.OnAppHeartbeat();               // same instant
        Drain();

        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
    }
}
