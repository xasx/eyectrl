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
        public EyeCtrlSettings Settings { get; }

        /// <summary>Bring-up steps in the order TryBringUpSupervisor performed them.</summary>
        public List<string> BringUp { get; } = new();

        public Harness(bool supervisionEnabled)
        {
            var settings = new EyeCtrlSettings();
            settings.Supervision.Enabled = supervisionEnabled;
            Settings = settings;

            Coordinator = new AppRestartCoordinator(
                Messenger,
                settings,
                tryReachSupervisor: () => { Probes++; return SupervisorReachable; },
                selfRestart: () => { SelfAttempts++; return SelfOutcome; },
                shutdown: () => Log.Add("shutdown"),
                reportUnavailable: reason => Log.Add("report:" + reason),
                runInBackground: work => work(),   // inline: the tests are the background thread
                postToUi: work => work(),
                supervisorInstalled: () => SupervisorInstalled,
                launchSupervisor: () => { Launches++; BringUp.Add("launch"); return SupervisorLaunches; },
                enablePublishing: () => { PublishingEnabled = true; BringUp.Add("publish"); },
                persistSettings: () => { SupervisionPersisted = true; BringUp.Add("persist"); });
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
            reportUnavailable: _ => { },
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => false,
            launchSupervisor: () => false,
            enablePublishing: () => { },
            persistSettings: () => { });

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
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); },
            supervisorInstalled: () => false,
            launchSupervisor: () => false,
            enablePublishing: () => { },
            persistSettings: () => { });

        messenger.Send(new RestartAppRequestedMessage());

        Check.That(order).ContainsExactly("background", "self", "ui", "shutdown");
    }

    [Fact]
    public void SupervisorProbe_RunsOffTheUiThread_AndShutsDownOnIt()
    {
        // The supervised probe can wait up to ProbeWait (500ms). Same rule as the
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
            reportUnavailable: _ => { },
            runInBackground: work => { order.Add("background"); work(); },
            postToUi: work => { order.Add("ui"); work(); },
            supervisorInstalled: () => false,
            launchSupervisor: () => false,
            enablePublishing: () => { },
            persistSettings: () => { });

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
            reportUnavailable: _ => { },
            runInBackground: work => requests.Add(work),   // defer: the request is "in flight"
            postToUi: work => work(),
            supervisorInstalled: () => false,
            launchSupervisor: () => false,
            enablePublishing: () => { },
            persistSettings: () => { });

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
            reportUnavailable: reports.Add,
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => false,
            launchSupervisor: () => false,
            enablePublishing: () => { },
            persistSettings: () => { });

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
        Check.That(h.Settings.Supervision.Enabled).IsTrue();
        Check.That(h.SupervisionPersisted).IsTrue();
        Check.That(h.Launches).IsEqualTo(1);
        Check.That(h.Coordinator.SupervisorRelaunchRequested).IsTrue();
        Check.That(h.Log).ContainsExactly("shutdown");

        // The ORDER, not just the end state. Asserting only the three booleans let the
        // implementation drift from its own doc comment once already: publishing must be on
        // before anything can adopt us, and persist must precede the launch. It is also the
        // only throwing step, so it goes last.
        Check.That(h.BringUp).ContainsExactly("publish", "persist", "launch");
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
