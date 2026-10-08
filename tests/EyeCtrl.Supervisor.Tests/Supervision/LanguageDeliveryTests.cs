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
        Reply(0, Saved);                          // the re-read still finds English

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
}
