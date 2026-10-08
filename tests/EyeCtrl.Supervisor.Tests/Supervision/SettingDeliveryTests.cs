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
    private DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

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
            work => work(),
            utcNow: () => _now);
    }

    /// <summary>Moves the clock to the delivery's next retry time, then delivers a heartbeat.</summary>
    private void RetryWhenDue()
    {
        if (_delivery.NextRetryAt is { } due && due > _now) _now = due;
        _delivery.OnAppHeartbeat();
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

        RetryWhenDue();
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

        RetryWhenDue();
        Check.That(_sends).HasSize(2);
    }

    [Fact]
    public void ARefusedRequest_IsDropped()
    {
        _delivery.Request(true);
        Reply(0, Ack(SettingRequestStatus.Rejected));

        Check.That(_delivery.State).IsEqualTo(SettingDeliveryState.Refused);
        Check.That(_delivery.Pending).IsNull();
        RetryWhenDue();
        Check.That(_sends).HasSize(1);
    }

    [Fact]
    public void HeartbeatsWhileARequestIsInFlight_AreSkipped()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);

        RetryWhenDue();               // starts the retry
        for (var i = 0; i < 5; i++)
            RetryWhenDue();           // heartbeats faster than the acknowledgement bound

        Check.That(_sends).HasSize(2);
        Check.That(_delivery.IsInFlight).IsTrue();
    }

    [Fact]
    public void AHeartbeatWithNothingParked_SendsNothing()
    {
        RetryWhenDue();
        _delivery.Request(false);
        Reply(0, Ack(SettingRequestStatus.Saved));
        RetryWhenDue();

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
        RetryWhenDue();
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
        RetryWhenDue();
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
        RetryWhenDue();
        Reply(1, Ack(SettingRequestStatus.Saved));

        Check.That(states).ContainsExactly(
            SettingDeliveryState.Sending, SettingDeliveryState.Parked,
            SettingDeliveryState.Sending, SettingDeliveryState.Saved);
    }

    // ── F1: the timer retry ────────────────────────────────────────────────────────────────

    [Fact]
    public void AClockTick_RetriesAParkedChoice_WithNoHeartbeatAtAll()
    {
        // Supervision off: the App runs but publishes no heartbeats. The tick alone delivers.
        _delivery.Request(true);
        Reply(0, NotAvailable);

        _now = _delivery.NextRetryAt!.Value;
        _delivery.OnRetryTick();

        Check.That(_sends).HasSize(2);
        Check.That(_sends[1].Request.Restart).IsFalse();
        Reply(1, Ack(SettingRequestStatus.Saved));
        Check.That(_delivery.Pending).IsNull();
    }

    [Fact]
    public void AClockTick_WhileARequestIsInFlight_IsSkipped()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);
        _now = _delivery.NextRetryAt!.Value;

        _delivery.OnRetryTick();
        _delivery.OnRetryTick();
        _delivery.OnAppHeartbeat();

        Check.That(_sends).HasSize(2);
    }

    // ── F3: backoff ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBackoff_DoublesFromFiveSeconds_UpToTheSixtySecondCap()
    {
        var delays = Enumerable.Range(1, 7).Select(SettingDelivery<bool>.RetryDelayAfter).ToList();

        Check.That(delays).ContainsExactly(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void RetriesBeforeTheBackoffHasPassed_AreSkipped_ForTickAndHeartbeatAlike()
    {
        var parkedAt = _now;
        _delivery.Request(true);
        Reply(0, NotAvailable);
        Check.That(_delivery.NextRetryAt).IsEqualTo(parkedAt + TimeSpan.FromSeconds(5));

        _now = parkedAt + TimeSpan.FromSeconds(4.9);
        _delivery.OnRetryTick();
        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(1);

        _now = parkedAt + TimeSpan.FromSeconds(5);
        _delivery.OnRetryTick();
        Check.That(_sends).HasSize(2);

        // The second failure doubles the wait.
        var secondParkAt = _now;
        Reply(1, Ack(SettingRequestStatus.Failed));
        Check.That(_delivery.NextRetryAt).IsEqualTo(secondParkAt + TimeSpan.FromSeconds(10));
        _now = secondParkAt + TimeSpan.FromSeconds(9.9);
        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(2);
        _now = secondParkAt + TimeSpan.FromSeconds(10);
        _delivery.OnAppHeartbeat();
        Check.That(_sends).HasSize(3);
    }

    [Fact]
    public void ANewChoice_IsSentAtOnce_AndResetsTheBackoff()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);
        RetryWhenDue();
        Reply(1, NotAvailable);                 // second failure: 10 s
        Check.That(_delivery.NextRetryAt).IsEqualTo(_now + TimeSpan.FromSeconds(10));

        _delivery.Request(false);               // no waiting for the backoff
        Check.That(_sends).HasSize(3);
        Reply(2, NotAvailable);

        Check.That(_delivery.NextRetryAt).IsEqualTo(_now + TimeSpan.FromSeconds(5));
    }

    // The observable property: a park after a save starts again at the first delay. Removing the
    // reset on the save path alone cannot fail this — every later park follows a new choice, and
    // Request resets too — so that reset is defensive, and this pins the behaviour, not the line.
    [Fact]
    public void AParkAfterASave_StartsAgainAtTheFirstDelay()
    {
        _delivery.Request(true);
        Reply(0, NotAvailable);
        RetryWhenDue();
        Reply(1, NotAvailable);
        RetryWhenDue();
        Reply(2, Ack(SettingRequestStatus.Saved));

        _delivery.Request(false);
        Reply(3, NotAvailable);

        Check.That(_delivery.NextRetryAt).IsEqualTo(_now + TimeSpan.FromSeconds(5));
    }

    // ── F1: the stale-park guard (T05b's "clear only after a re-read") ──────────────────────

    /// <summary>A three-valued setting, because a stale park that differs from both the park-time
    /// disk value and the newer disk value needs three values to exist (T05b's language has three).
    /// The background runner is a queue the test drains, so "off the UI thread" is observable.</summary>
    private sealed class GuardedDelivery
    {
        public readonly List<(SettingRequest Request, int Value, TaskCompletionSource<AppRequestResult> Reply)> Sends = new();
        public readonly Queue<Action> Background = new();
        public int? Disk;
        public DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        public readonly SettingDelivery<int> Delivery;

        public GuardedDelivery(int? disk)
        {
            Disk = disk;
            Delivery = new SettingDelivery<int>(
                "ThreeWay",
                (value, restart) => new SettingRequest(
                    SettingRequest.NewId(), "setThreeWay",
                    System.Text.Json.JsonSerializer.SerializeToElement(value), restart),
                request =>
                {
                    var reply = new TaskCompletionSource<AppRequestResult>();
                    Sends.Add((request, request.Value.GetInt32(), reply));
                    return reply.Task;
                },
                work => work(),
                readDisk: () => Disk,
                runInBackground: Background.Enqueue,
                utcNow: () => Now);
        }

        /// <summary>Answers a send and runs the background work it queued (the park-time read).</summary>
        public void Reply(int send, AppRequestResult result)
        {
            Sends[send].Reply.SetResult(result);
            Drain();
        }

        public void Drain()
        {
            while (Background.Count > 0) Background.Dequeue()();
        }

        /// <summary>Lets the clock reach the retry, triggers it and runs the background re-read.</summary>
        public void RetryNow()
        {
            if (Delivery.NextRetryAt is { } due && due > Now) Now = due;
            Delivery.OnRetryTick();
            Drain();
        }
    }

    [Fact]
    public void AParkedChoiceTheDiskAlreadyHolds_IsClearedWithoutASend()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);

        g.Disk = 2;                               // the App saved it some other way
        g.RetryNow();

        Check.That(g.Sends).HasSize(1);
        Check.That(g.Delivery.Pending).IsNull();
        Check.That(g.Delivery.State).IsEqualTo(SettingDeliveryState.Saved);
        Check.That(g.Delivery.LastSaved).IsEqualTo(2);
    }

    [Fact]
    public void AParkedChoice_IsDropped_WhenTheDiskChangedSinceItParked()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);   // parks with the disk at 0

        g.Disk = 1;                               // someone chose 1 in the App since
        g.RetryNow();

        Check.That(g.Sends).HasSize(1);           // the stale 2 is never sent over the newer 1
        Check.That(g.Delivery.Pending).IsNull();
        Check.That(g.Delivery.State).IsEqualTo(SettingDeliveryState.Superseded);
        Check.That(g.Delivery.SupersededBy).IsEqualTo(1);
    }

    [Fact]
    public void AParkedChoice_IsSent_WhenTheDiskIsUnchanged()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);

        g.RetryNow();

        Check.That(g.Sends).HasSize(2);
        Check.That(g.Sends[1].Value).IsEqualTo(2);
        Check.That(g.Sends[1].Request.Restart).IsFalse();
    }

    [Fact]
    public void AParkedChoice_IsSent_WhenTheDiskCannotBeRead()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);

        g.Disk = null;                            // contended or damaged: cannot tell
        g.RetryNow();

        Check.That(g.Sends).HasSize(2);
    }

    [Fact]
    public void TheGuardsReReadRunsOffTheUiThread_AndHoldsTheFlight()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);
        if (g.Delivery.NextRetryAt is { } due) g.Now = due;

        g.Delivery.OnRetryTick();                 // queues the re-read; nothing ran inline
        Check.That(g.Background).HasSize(1);
        Check.That(g.Delivery.IsInFlight).IsTrue();
        g.Delivery.OnRetryTick();
        g.Delivery.OnAppHeartbeat();
        Check.That(g.Background).HasSize(1);      // single-flight across the re-read

        g.Background.Dequeue()();
        Check.That(g.Sends).HasSize(2);
    }

    [Fact]
    public void ANewChoiceMadeDuringTheReRead_IsSentLive()
    {
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);
        if (g.Delivery.NextRetryAt is { } due) g.Now = due;
        g.Delivery.OnRetryTick();

        g.Delivery.Request(1);                    // during the re-read
        g.Background.Dequeue()();

        Check.That(g.Sends).HasSize(2);
        Check.That(g.Sends[1].Value).IsEqualTo(1);
        Check.That(g.Sends[1].Request.Restart).IsTrue();
    }

    // ── R2-1 / R2-2: the guard's reference value ───────────────────────────────────────────

    [Fact]
    public void ANewChoice_StartsAFreshParkReference_SoItIsNeverSupersededByTheOldOne()
    {
        // G4: without the reset in Request, the old reference (0) would make the guard drop the
        // caretaker's NEWEST choice as superseded once the disk moved on to 1.
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);                 // parks 2 with the disk at 0
        g.Disk = 1;                               // the user changes it in the App

        g.Delivery.Request(0);                    // the caretaker chooses again, live
        g.Reply(1, NotAvailable);                 // parks 0 with the disk at 1
        g.RetryNow();

        Check.That(g.Delivery.State).IsNotEqualTo(SettingDeliveryState.Superseded);
        Check.That(g.Sends).HasSize(3);
        Check.That(g.Sends[2].Value).IsEqualTo(0);
    }

    [Fact]
    public void TheParkReference_IsRecordedOnce_NotOverwrittenByAFailedRetry()
    {
        // G3: a disk change during a failed retry's send must still be seen as a change. A
        // reference re-recorded at every park would re-baseline to the newer value and send the
        // stale choice over it.
        var g = new GuardedDelivery(disk: 0);
        g.Delivery.Request(2);
        g.Reply(0, NotAvailable);                 // parks 2 with the disk at 0
        g.RetryNow();                             // the retry is sent...
        Check.That(g.Sends).HasSize(2);

        g.Disk = 1;                               // ...the user changes it while it is in flight
        g.Reply(1, NotAvailable);                 // ...and the retry fails
        g.RetryNow();

        Check.That(g.Delivery.State).IsEqualTo(SettingDeliveryState.Superseded);
        Check.That(g.Delivery.SupersededBy).IsEqualTo(1);
        Check.That(g.Sends).HasSize(2);
    }

    [Fact]
    public void ThePark_TimeDiskRead_RunsOffTheUiThread_EvenWhenTheSendCompletedInline()
    {
        // R2-2: a throwing send completes synchronously, so the continuation runs on the caller —
        // the UI thread. The disk read it does must still go through the background runner.
        var background = new Queue<Action>();
        var reads = 0;
        var delivery = new SettingDelivery<int>(
            "ThreeWay",
            (value, restart) => new SettingRequest(
                SettingRequest.NewId(), "setThreeWay",
                System.Text.Json.JsonSerializer.SerializeToElement(value), restart),
            _ => throw new InvalidOperationException("send threw"),
            work => work(),
            readDisk: () => { reads++; return 0; },
            runInBackground: background.Enqueue,
            utcNow: () => _now);

        delivery.Request(2);

        Check.That(reads).IsEqualTo(0);           // nothing read inline
        Check.That(background).HasSize(1);
        Check.That(delivery.State).IsEqualTo(SettingDeliveryState.Sending);

        background.Dequeue()();
        Check.That(reads).IsEqualTo(1);
        Check.That(delivery.State).IsEqualTo(SettingDeliveryState.Parked);
    }

    // ── T05b: choices are told apart by when they were made, not by their value ───────────

    [Fact]
    public void ALiveRePickOfTheValueInFlight_IsSentAgainLive_AfterTheAck()
    {
        // A parked retry (restart:false) is in flight when the user re-picks the same value live.
        // Its acknowledgement must not swallow the live choice; it goes out again, live.
        _delivery.Request(true);
        Reply(0, NotAvailable);
        RetryWhenDue();
        Check.That(_sends[1].Request.Restart).IsFalse();

        _delivery.Request(true);
        Reply(1, Ack(SettingRequestStatus.Saved));

        Check.That(_sends).HasSize(3);
        Check.That(_sends[2].Request.Restart).IsTrue();
    }
}

