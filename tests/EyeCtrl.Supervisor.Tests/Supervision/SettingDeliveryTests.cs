namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The parked-choice delivery, driven deterministically: every send is a task the test completes,
/// and completions run inline (the UI-thread marshal is the identity here; the dispatcher version
/// is <see cref="StartOnBootDeliveryIntegrationTests"/>).
/// </summary>
public class SettingDeliveryTests
{
    private readonly List<(SettingRequest Request, bool Value, TaskCompletionSource<AppRequestResult> Reply)> _sends = new();
    private readonly SettingDelivery<bool> _delivery;

    public SettingDeliveryTests()
    {
        _delivery = new SettingDelivery<bool>(
            "StartOnBoot",
            (value, restart) => new SettingRequest(
                SettingRequest.NewId(), SettingRequestKinds.SetStartOnBoot,
                System.Text.Json.JsonSerializer.SerializeToElement(value), restart),
            request =>
            {
                var reply = new TaskCompletionSource<AppRequestResult>();
                _sends.Add((request, request.Value.GetBoolean(), reply));
                return reply.Task;
            },
            work => work());
    }

    private static AppRequestResult Ack(SettingRequestStatus status) => new(status, "test");

    private static AppRequestResult NotAvailable => AppRequestResult.NotAvailable("test: no App");

    private void Reply(int send, AppRequestResult result) => _sends[send].Reply.SetResult(result);

    [Fact]
    public void AnAcknowledgedChoice_IsCleared_AndReportedSaved()
    {
        _delivery.Request(true);
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Sending);
        Check.That(_delivery.Pending).IsEqualTo(true);

        Reply(0, Ack(SettingRequestStatus.Saved));

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.LastSaved).IsEqualTo(true);
    }

    [Fact]
    public void RestartInProgress_CountsAsSaved()
    {
        _delivery.Request(true);
        Reply(0, Ack(SettingRequestStatus.RestartInProgress));

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
        Check.That(_delivery.Pending).IsNull();
    }

    [Fact]
    public void AppDown_ParksTheChoice()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Parked);
        Check.That(_delivery.Pending).IsEqualTo(true);
        Check.That(_delivery.IsInFlight).IsFalse();
    }

    [Fact]
    public void AParkedChoice_IsDeliveredOnTheNextHeartbeat_AndClearedOnlyByTheAcknowledgement()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);

        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(2);
        Check.That(_sends[1].Value).IsTrue();
        Check.That(_delivery.Pending).IsEqualTo(true);   // still pending while in flight

        Reply(1, Ack(SettingRequestStatus.Saved));
        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Saved);
    }

    [Fact]
    public void AFailedSave_KeepsTheChoiceParked()
    {
        _delivery.Request(true);
        Reply(0, Ack(SettingRequestStatus.Failed));

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Parked);
        Check.That(_delivery.Pending).IsEqualTo(true);

        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(2);
    }

    [Fact]
    public void ARefusedRequest_IsDropped()
    {
        _delivery.Request(true);
        Reply(0, Ack(SettingRequestStatus.Rejected));

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Refused);
        Check.That(_delivery.Pending).IsNull();
        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(1);
    }

    [Fact]
    public void HeartbeatsWhileARequestIsInFlight_AreSkipped()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);

        _delivery.OnAppHeartbeat();               // starts the retry
        for (var i = 0; i < 5; i++)
            _delivery.OnAppHeartbeat();           // heartbeats faster than the acknowledgement bound

        Check.That(_sends).HasSize(2);
        Check.That(_delivery.IsInFlight).IsTrue();
    }

    [Fact]
    public void AHeartbeatWithNothingParked_SendsNothing()
    {
        _delivery.OnAppHeartbeat();
        _delivery.Request(false);
        Reply(0, Ack(SettingRequestStatus.Saved));
        _delivery.OnAppHeartbeat();

        Check.That(_sends).HasSize(1);
    }

    [Fact]
    public void ANewerChoice_ReplacesAnOlderParkedOne()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);
        _delivery.Request(false);
        Reply(1, NotAvailable);

        Check.That(_delivery.Pending).IsEqualTo(false);
        _delivery.OnAppHeartbeat();
        Check.That(_sends[2].Value).IsFalse();
    }

    [Fact]
    public void AChoiceMadeWhileOneIsInFlight_IsSentAfterIt_NotAlongsideIt()
    {
        _delivery.Request(true);
        _delivery.Request(false);                 // single-flight: waits for the first
        Check.That(_sends).HasSize(1);
        Check.That(_delivery.Pending).IsEqualTo(false);

        Reply(0, Ack(SettingRequestStatus.Saved));  // the App answers for the OLD value
        Check.That(_sends).HasSize(2);              // ...so the newer one goes now
        Check.That(_sends[1].Value).IsFalse();
        Check.That(_delivery.Pending).IsEqualTo(false);
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Sending);

        Reply(1, Ack(SettingRequestStatus.Saved));
        Check.That(_delivery.Pending).IsNull();
        Check.That(_delivery.LastSaved).IsEqualTo(false);
    }

    [Fact]
    public void AChoiceMadeWhileOneIsInFlight_IsParkedWhenTheAppGoesAway()
    {
        _delivery.Request(true);
        _delivery.Request(false);
        Reply(0, NotAvailable);

        Check.That(_sends).HasSize(1);
        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Parked);
        Check.That(_delivery.Pending).IsEqualTo(false);
    }

    [Fact]
    public void OnlyALiveChoice_MayAskForARestart_NeverAParkedRetry()
    {
        _delivery.Request(true);
        Check.That(_sends[0].Request.Restart).IsTrue();   // the user just chose it

        Reply(0, NotAvailable);
        _delivery.OnAppHeartbeat();
        Check.That(_sends[1].Request.Restart).IsFalse();  // delivered later: saved only
    }

    [Fact]
    public void ASendThatThrows_ParksTheChoice()
    {
        var delivery = new SettingDelivery<bool>(
            "StartOnBoot",
            (value, _) => SettingRequest.SetStartOnBoot(value),
            _ => throw new InvalidOperationException("boom"),
            work => work());

        delivery.Request(true);

        Check.That(delivery.State).IsEqualTo(SettingDeliveryState.Parked);
        Check.That(delivery.Pending).IsEqualTo(true);
        Check.That(delivery.IsInFlight).IsFalse();
    }

    [Fact]
    public void EveryStateChange_IsAnnounced()
    {
        var states = new List<SettingDeliveryState>();
        _delivery.Changed += () => states.Add(_delivery.State);

        _delivery.Request(true);
        Reply(0, NotAvailable);
        _delivery.OnAppHeartbeat();
        Reply(1, Ack(SettingRequestStatus.Saved));

        Check.That(states).ContainsExactly(
            SettingDeliveryState.Sending, SettingDeliveryState.Parked,
            SettingDeliveryState.Sending, SettingDeliveryState.Saved);
    }
}
