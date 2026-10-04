namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The coordinator decides whether the app is allowed to exit. Getting this wrong
/// strands a user whose only input channel is the app being restarted, so every
/// branch is pinned.
/// </summary>
public class AppRestartCoordinatorTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>A bring-up that is never exercised: these tests pin the strategies around it.</summary>
    private static SupervisorBringUp UnusedBringUp(EyeCtrlSettings settings) =>
        new(settings,
            enablePublishing: () => { },
            launchSupervisor: () => false,
            persistEnabled: _ => { },
            reconcileRunKey: () => { },
            tryReachSupervisor: () => false);

    private sealed class Harness
    {
        public bool SupervisorReachable { get; set; }
        public int Probes { get; private set; }
        public RestartOutcome SelfOutcome { get; set; } = RestartOutcome.Started;
        public int SelfAttempts { get; private set; }
        public List<string> Log { get; } = new();
        public IMessenger Messenger { get; } = new WeakReferenceMessenger();
        public AppRestartCoordinator Coordinator { get; }

        public bool SupervisorInstalled { get; set; }
        public bool SupervisorLaunches { get; set; } = true;
        public int Launches { get; private set; }
        public bool PublishingEnabled { get; private set; }
        public bool SupervisionPersisted { get; private set; }
        public bool RunKeyReconciled { get; private set; }
        public EyeCtrlSettings Settings { get; }

        /// <summary>Bring-up steps in the order the shared sequence performed them.</summary>
        public List<string> BringUp { get; } = new();

        /// <summary>
        /// How many "restart is under way" notices were raised. Counted separately from
        /// <see cref="Log"/> so the existing ContainsExactly("shutdown") assertions keep
        /// asserting what they were written to assert.
        /// </summary>
        public int Notices { get; private set; }

        public Harness(bool supervisionEnabled)
        {
            var settings = new EyeCtrlSettings();
            settings.Supervision.Enabled = supervisionEnabled;
            Settings = settings;

            // The reachability budget is real time in production; a fake clock keeps the
            // never-reachable cases instant.
            var clock = new FakeClock();
            var bringUp = new SupervisorBringUp(
                settings,
                enablePublishing: () => { PublishingEnabled = true; BringUp.Add("publish"); },
                launchSupervisor: () => { Launches++; BringUp.Add("launch"); return SupervisorLaunches; },
                persistEnabled: _ => { SupervisionPersisted = true; BringUp.Add("persist"); },
                reconcileRunKey: () => { RunKeyReconciled = true; BringUp.Add("runkey"); },
                tryReachSupervisor: () => { Probes++; return SupervisorReachable; },
                utcNow: () => clock.Now,
                sleep: clock.Advance);

            Coordinator = new AppRestartCoordinator(
                Messenger,
                settings,
                tryReachSupervisor: () => { Probes++; return SupervisorReachable; },
                selfRestart: () => { SelfAttempts++; return SelfOutcome; },
                shutdown: () => Log.Add("shutdown"),
                reportInProgress: () => Notices++,
                reportUnavailable: reason => Log.Add("report:" + reason),
                runInBackground: work => work(),
                postToUi: work => work(),
                supervisorInstalled: () => SupervisorInstalled,
                bringUp: bringUp);
        }

        public void Request(bool startSupervisor = false) =>
            Messenger.Send(new RestartAppRequestedMessage(startSupervisor));
    }

    [Fact]
    public void SupervisorReachable_UsesTheSupervisorAndDoesNotSelfRestart()
    {
        // Preferred path: only the supervisor can recover a relaunch that fails.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = true };
        h.Request();

        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsTrue();
        Check.That(h.SelfAttempts).IsEqualTo(0);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void Unsupervised_SelfRestarts_WithoutProbing()
    {
        // The common case: Supervision.Enabled defaults to false.
        var h = new Harness(supervisionEnabled: false);
        h.Request();

        Check.That(h.Probes).IsEqualTo(0);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SupervisorConfiguredButUnreachable_FallsBackToSelfRestart()
    {
        // Enabled records that a supervisor was configured, not that one is running.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = false };
        h.Request();

        Check.That(h.Probes).IsEqualTo(1);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SelfRestartFailure_ReportsAndStaysRunning()
    {
        // THE load-bearing case. Exiting here would leave the user with nothing.
        var h = new Harness(supervisionEnabled: false) { SelfOutcome = RestartOutcome.LaunchFailed };
        h.Request();

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).StartsWith("report:");
        Check.That(h.Log[0]).Contains("still running");
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
    }

    [Fact]
    public void SelfRestart_NeverSetsTheSupervisorRelaunchFlag()
    {
        // Otherwise a supervisor that is actually alive (transient probe failure) would
        // launch a SECOND instance beside the child we just started.
        var h = new Harness(supervisionEnabled: true) { SupervisorReachable = false };
        h.Request();

        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
    }

    [Fact]
    public void AnAcceptedRestart_AcknowledgesBeforeDoingAnyWork()
    {
        // The self-restart handshake can take 30s. The dispatcher stays free, but silence
        // for that long reads exactly like the hang this feature exists to prevent, so the
        // notice must precede the work rather than accompany its result.
        var settings = new EyeCtrlSettings();
        var order = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => { order.Add("self"); return RestartOutcome.Started; },
            shutdown: () => order.Add("shutdown"),
            reportInProgress: () => order.Add("notice"),
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work(),
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("notice", "self", "shutdown");
    }

    [Fact]
    public void ADroppedSecondRequest_RaisesNoSecondNotice()
    {
        // The in-flight guard drops the second dwell. It must not also re-raise the notice:
        // the one for the running attempt is still on screen, and replacing it would make a
        // dropped request look like it started something.
        var h = new Harness(supervisionEnabled: false) { SelfOutcome = RestartOutcome.Started };
        h.Request();
        h.Request();

        Check.That(h.Notices).IsEqualTo(1);
        Check.That(h.SelfAttempts).IsEqualTo(1);
    }

    [Fact]
    public void EachReArmedAttempt_GetsItsOwnNotice()
    {
        // After a refusal the button re-arms, and the next attempt is a new attempt: it
        // needs its own acknowledgement, or the second dwell looks ignored.
        var h = new Harness(supervisionEnabled: false) { SelfOutcome = RestartOutcome.LaunchFailed };
        h.Request();
        h.Request();

        Check.That(h.Notices).IsEqualTo(2);
        Check.That(h.SelfAttempts).IsEqualTo(2);
    }

    [Fact]
    public void SupervisorFlagIsSetBeforeShutdown_SoOnExitCanSeeIt()
    {
        // OnExit reads the flag when it stamps the final payload. Setting it after
        // Shutdown() would make the payload say "plain quit" and the supervisor would
        // go idle instead of relaunching.
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = true;
        var messenger = new WeakReferenceMessenger();
        bool? seenAtShutdown = null;
        AppRestartCoordinator? coordinator = null;
        coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => true,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => seenAtShutdown = coordinator!.SupervisorRelaunchRequested,
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work(),
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(seenAtShutdown).IsEqualTo(true);
    }

    [Fact]
    public void SelfRestart_RunsOffTheUiThread_AndShutsDownOnIt()
    {
        // Begin() blocks for up to 30s. On the dispatcher that is 30s of no gaze
        // processing — the user loses control of the machine. And Shutdown() must be
        // called back ON the dispatcher.
        var settings = new EyeCtrlSettings();
        var order = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => { order.Add("self"); return RestartOutcome.Started; },
            shutdown: () => order.Add("shutdown"),
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); },
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("background", "self", "ui", "shutdown");
    }

    [Fact]
    public void SupervisorProbe_RunsOffTheUiThread_AndShutsDownOnIt()
    {
        // The supervised probe can wait up to HeartbeatPublisher.DefaultProbeWait (500ms). Same rule as the
        // self-restart: the dispatcher must never block on IPC, or gaze processing
        // freezes while the user is waiting for a restart they asked for.
        var settings = new EyeCtrlSettings();
        settings.Supervision.Enabled = true;
        var order = new List<string>();
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => { order.Add("probe"); return true; },
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => order.Add("shutdown"),
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); },
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("background", "probe", "ui", "shutdown");
    }

    [Fact]
    public void SecondRequest_WhileTheFirstIsInFlight_IsIgnored()
    {
        // Two rapid dwells must not launch two children: the loser would sit on the
        // single-instance mutex for its 30s wait and then exit — one orphaned process.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var requests = new List<Action>();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.ChildNotReady,
            shutdown: () => { },
            reportInProgress: () => { },
            reportUnavailable: _ => { },
            runInBackground: work => requests.Add(work),   // defer: the request is "in flight"
            postToUi: work => work(),
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());  // first: in flight
        messenger.Send(new RestartAppRequestedMessage());  // second: dropped

        Check.That(requests).HasSize(1);

        // The refusal path re-arms: after the failed attempt reports, the button works again.
        requests[0]();
        messenger.Send(new RestartAppRequestedMessage());

        Check.That(requests).HasSize(2);
    }

    [Fact]
    public void AThrowingStrategy_ReportsAndReArms_RatherThanDeadeningTheButton()
    {
        // The background body runs on a thread-pool thread, so an escaping exception would
        // be swallowed as an unobserved faulted Task: no report, and _restartInFlight stays
        // latched forever — the user dwells the one control meant to get them unstuck and
        // nothing happens, silently. Task 13's persistSettings is a real throw source
        // (SettingsService.Save throws on invalid settings), so this is not hypothetical.
        var settings = new EyeCtrlSettings();
        var reports = new List<string>();
        var attempts = 0;
        var messenger = new WeakReferenceMessenger();
        var coordinator = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => { attempts++; throw new InvalidOperationException("boom"); },
            shutdown: () => reports.Add("shutdown"),
            reportInProgress: () => { },
            reportUnavailable: reports.Add,
            runInBackground: work => work(),
            postToUi: work => work(),
                supervisorInstalled: () => false,
                bringUp: UnusedBringUp(settings));

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(reports).HasSize(1);
        Check.That(reports[0]).Contains("still running");
        Check.WithCustomMessage("the app must not exit when the strategy threw")
            .That(reports).Not.Contains("shutdown");

        // Re-armed: a second dwell is honoured rather than silently dropped.
        messenger.Send(new RestartAppRequestedMessage());
        Check.That(attempts).IsEqualTo(2);
    }

    [Fact]
    public void OffersTheSupervisor_WhenInstalledButNotRunning()
    {
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsTrue();
    }

    [Fact]
    public void DoesNotOffer_WhenNoSupervisorIsInstalled()
    {
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = false };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void DoesNotOffer_WhenOneIsAlreadyRunning()
    {
        var h = new Harness(supervisionEnabled: true) { SupervisorInstalled = true, SupervisorReachable = true };
        Check.That(h.Coordinator.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void AcceptingTheOffer_EnablesPublishingAndPersistsBeforeLaunching()
    {
        // Order matters twice over: publishing must be on before the probe can safely
        // adopt us, and Enabled must be persisted before the replacement starts, or the
        // supervisor adopts an instance that never heartbeats and kills it.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        h.SupervisorReachable = true;   // it comes up on the first poll

        h.Request(startSupervisor: true);

        Check.That(h.PublishingEnabled).IsTrue();
        Check.That(h.RunKeyReconciled).IsTrue();
        Check.That(h.Settings.Supervision.Enabled).IsTrue();
        Check.That(h.SupervisionPersisted).IsTrue();
        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsTrue();
        Check.That(h.Log).ContainsExactly("shutdown");

        // The ORDER, not just the end state. Asserting only the three booleans let the
        // implementation drift from its own doc comment once already.
        //
        // Persist now follows the launch rather than preceding it. The original order wrote
        // Supervision.Enabled to disk before anything had been started, so a launch that
        // failed still left the setting latched on — and because persisting also hands
        // launch-at-logon to the supervisor, that deleted the HKCU Run key in favour of a
        // Scheduled Task nothing installs. Requiring a started process first is the smallest
        // change that makes "opt-in" mean opted-in.
        //
        // What has NOT changed, and must not: publishing is still first (nothing can adopt an
        // instance that never heartbeats), the Run key reconcile still follows the persist
        // (that is the old combined persist lambda's order, now explicit in the shared
        // sequence), and persist still precedes the poll and the shutdown, so the replacement
        // the supervisor relaunches still reads Enabled == true.
        Check.That(h.BringUp).ContainsExactly("publish", "launch", "persist", "runkey");
    }

    [Fact]
    public void SupervisorThatFailsToLaunch_DoesNotLatchSupervisionOn()
    {
        // The self-latch. A launch that never started a process must leave no trace: no
        // persisted Enabled, and therefore no handover of launch-at-logon. Otherwise one press
        // of Restart permanently flips a flag documented "opt-in for v1" and deletes the app's
        // autostart, and the user meets the consequence at the next logon with no input channel.
        var h = new Harness(supervisionEnabled: false)
        {
            SupervisorInstalled = true,
            SupervisorLaunches = false,
        };

        h.Request(startSupervisor: true);

        Check.That(h.SupervisionPersisted).IsFalse();
        Check.That(h.Settings.Supervision.Enabled).IsFalse();
        Check.That(h.BringUp).ContainsExactly("publish", "launch");

        // Still restarts — refusing to launch a supervisor is not a reason to strand the user.
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SupervisorThatLaunchesButNeverAnswers_StillPersists()
    {
        // Deliberately NOT symmetric with the failed-launch case. A process was started, so a
        // late-arriving supervisor may still adopt the replacement — and it kills an instance
        // that does not heartbeat. Persisting is what keeps the replacement publishing.
        // Evidence of a started process, not evidence of a reachable one, is the bar here.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        h.SupervisorReachable = false;

        h.Request(startSupervisor: true);

        Check.That(h.SupervisionPersisted).IsTrue();
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
    }

    [Fact]
    public void SupervisorThatNeverComesUp_FallsBackToSelfRestart()
    {
        // Degrades rather than fails: supervision is now enabled, so the replacement
        // publishes heartbeats and the late-starting supervisor adopts it.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };
        h.SupervisorReachable = false;

        h.Request(startSupervisor: true);

        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsFalse();
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void SupervisorThatFailsToLaunch_FallsBackToSelfRestart()
    {
        var h = new Harness(supervisionEnabled: false)
        {
            SupervisorInstalled = true,
            SupervisorLaunches = false,
        };

        h.Request(startSupervisor: true);

        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }

    [Fact]
    public void DecliningTheOffer_RestartsUnsupervised_AndChangesNoSettings()
    {
        // "Restart without" is a real restart, not a cancel. Cancelling is the third
        // choice, and it never reaches the coordinator at all.
        var h = new Harness(supervisionEnabled: false) { SupervisorInstalled = true };

        h.Request(startSupervisor: false);

        Check.That(h.Launches).IsEqualTo(0);
        Check.That(h.Settings.Supervision.Enabled).IsFalse();
        Check.That(h.SupervisionPersisted).IsFalse();
        Check.That(h.SelfAttempts).IsEqualTo(1);
        Check.That(h.Log).ContainsExactly("shutdown");
    }
}
