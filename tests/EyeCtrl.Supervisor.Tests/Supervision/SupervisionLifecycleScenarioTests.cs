namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06 scenario tests on the REAL <see cref="SupervisionStateMachine"/> +
/// <see cref="SupervisionClock"/>, with the pending-launch loop, the kill-initiated suppression,
/// the IdleWatch adoption gate and the stand-down modelled exactly as <c>SupervisorApp</c> drives
/// them (the shape of the reviewer's ticket-04 probe). Scenarios 5 and 6 run with the production
/// defaults (grace 60, hang 20, window 120, MaxRetries 3, real backoff curve).
/// </summary>
public class SupervisionLifecycleScenarioTests
{
    private sealed class Harness : ISupervisionClockHost
    {
        public SupervisionSettings Settings { get; } = new();   // spec defaults
        public SupervisionStateMachine Machine { get; }
        public DateTime Now { get; private set; } = DateTime.UtcNow;
        public DateTime? CleanExitRequestedAt { get; private set; }
        public DateTime? PendingLaunchAt { get; set; }
        public bool Enabled { get; set; } = true;
        public bool LaunchThrows { get; set; }
        public bool Bound { get; private set; }
        public bool KillInitiated { get; private set; }
        /// <summary>Ticket 07 review F2: the Supervisor's heartbeat pipe. Closed on FallenBack,
        /// reopened on the dialog's Retry.</summary>
        public bool Listening { get; private set; } = true;
        public int Launches { get; private set; }
        public int Kills { get; private set; }
        public int Fallbacks { get; private set; }
        public int Strikes => Machine.ConsecutiveFailures;
        public SupervisorState State => Machine.State;
        public int TickCount { get; private set; }
        public int? FallenBackAtTick { get; private set; }
        public List<string> Actions { get; } = new();

        private bool _exitPending;
        private bool _fallbackLaunched;

        public Harness()
        {
            Machine = new SupervisionStateMachine(
                Settings.MaxRetries, Settings.CrashWindowSeconds, () => Enabled);
        }

        bool ISupervisionClockHost.SupervisionEnabled => Enabled;

        /// <summary>SupervisorApp.Initialize's startup decision (shared production policy).</summary>
        public void Startup(bool startOnBoot = true, bool appAlreadyRunning = false)
        {
            if (StartupLaunchPolicy.ShouldLaunchAtStartup(Enabled, startOnBoot, appAlreadyRunning))
                Handle(new LaunchRequest(Now));
        }

        /// <summary>SupervisorApp.OnHeartbeatReceived: offer adoption (the MACHINE gates it on
        /// enabled, ticket 06-F2), bind only when accepted, then feed the heartbeat.</summary>
        public void Heartbeat()
        {
            if (Machine.State == SupervisorState.IdleWatch)
            {
                Handle(new AdoptRequest(Now));
                if (Machine.State != SupervisorState.Supervising) return;   // refused: supervision is off
                Bound = true;
                KillInitiated = false;
            }
            Handle(new HeartbeatReceived(Now));
        }

        /// <summary>The FallenBack dialog's "Retry EyeCtrl once": launch paused, then re-arm IdleWatch.</summary>
        public void Retry()
        {
            KillInitiated = false;
            Bound = true;
            Handle(new FallenBackRetry(Now));
        }

        /// <summary>An unannounced exit (crash). Not a supervisor kill.</summary>
        public void Crash() => Handle(new ProcessExited(Now));

        public void Handle(SupervisionInput input)
        {
            var prev = Machine.State;
            Machine.Handle(input);
            Actions.Add(input.GetType().Name);
            Drain(prev);
            if (Machine.State == SupervisorState.CleanExitPending && prev != SupervisorState.CleanExitPending)
                CleanExitRequestedAt = input.At;
            else if (Machine.State != SupervisorState.CleanExitPending)
                CleanExitRequestedAt = null;
            PumpExit();
        }

        private void Drain(SupervisorState prev)
        {
            foreach (var cmd in Machine.Commands)
            {
                switch (cmd)
                {
                    case SupervisorCommand.LaunchApp:
                        // Production stamps DateTime.UtcNow at drain time, strictly after the tick's
                        // captured now, so a delay-0 relaunch waits for the NEXT tick. The +1 ms
                        // models that gap faithfully.
                        var delay = RelaunchDelayPolicy.For(prev, Machine.State, Machine.NextBackoffDelay);
                        PendingLaunchAt = Now.AddMilliseconds(1) + delay;
                        break;
                    case SupervisorCommand.ForceKill:
                        Kills++;
                        KillInitiated = true;
                        _exitPending = true;               // the kill terminates the process
                        break;
                    case SupervisorCommand.StandDown:
                        PendingLaunchAt = null;
                        Bound = false;
                        break;
                    case SupervisorCommand.LaunchFallbackAt:
                        if (!_fallbackLaunched) { _fallbackLaunched = true; Fallbacks++; }
                        break;
                    case SupervisorCommand.StopListening:
                        Listening = false;                  // ticket 07 review F2
                        break;
                    case SupervisorCommand.StartListening:
                        Listening = true;                   // ticket 07 review F2
                        break;
                }
            }
        }

        /// <summary>The dispatcher pump: a queued Exited callback (Normal priority) runs before the
        /// next tick (Background). A kill-initiated exit is suppressed (F1).</summary>
        private void PumpExit()
        {
            if (!_exitPending) return;
            _exitPending = false;
            if (KillInitiated) return;
            Handle(new ProcessExited(Now));
        }

        void ISupervisionClockHost.Handle(SupervisionInput input) => Handle(input);

        void ISupervisionClockHost.ClearPendingLaunch() => PendingLaunchAt = null;

        bool ISupervisionClockHost.TryLaunchPendingNow()
        {
            // Models DoLaunchApp's try/catch; Tick feeds LaunchExecuted/LaunchFailed from the
            // result (ticket 06-F2), so this decision is covered by clock tests.
            Launches++;
            KillInitiated = false;
            if (LaunchThrows) return false;
            Bound = true;
            return true;
        }

        void ISupervisionClockHost.WireLaunchedProcess()
        {
            // The wire-time race guard (F4). The scenario's processes never exit at wire time.
        }

        void ISupervisionClockHost.TickFallenBackAutoRetry(DateTime now)
        {
            // A3: the scenarios drive the machine, never the FallenBack dialog's countdown (which
            // SupervisionClockTests pins through the real FallenBackAutoRetry).
        }

        public void Tick()
        {
            var now = Now;
            SupervisionClock.Tick(now, this);
            Now = Now.AddSeconds(1);
            TickCount++;
            if (Machine.State == SupervisorState.FallenBack && FallenBackAtTick is null)
                FallenBackAtTick = TickCount;
            PumpExit();
        }

        public void Run(int seconds)
        {
            for (var i = 0; i < seconds; i++) Tick();
        }
    }

    [Fact]
    public void Scenario1_SwitchOnWithTheAppAlreadyRunning_AdoptsWithZeroStrikes()
    {
        var h = new Harness();

        // The row's flow: StartOnBoot on, EyeCtrl already running (mutex held). The startup policy
        // must skip the launch; heartbeats every 3 s adopt it.
        h.Startup(startOnBoot: true, appAlreadyRunning: true);
        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Launches).IsEqualTo(0);

        for (var i = 0; i < 120; i++)
        {
            if (i % 3 == 0) h.Heartbeat();
            h.Tick();
        }

        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(h.Launches).IsEqualTo(0);      // no duplicate ever launched (F1)
        Check.That(h.Kills).IsEqualTo(0);
        Check.That(h.Strikes).IsEqualTo(0);
        Check.That(h.Bound).IsTrue();
    }

    [Fact]
    public void Scenario2_DisabledThenCrash_StandsDown_NoRelaunchNoKill()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);
        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
        var launchesBefore = h.Launches;

        h.Enabled = false;
        h.Crash();
        h.Run(3);

        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Kills).IsEqualTo(0);
        Check.That(h.Launches).IsEqualTo(launchesBefore);   // no relaunch
        Check.That(h.Strikes).IsEqualTo(0);
        Check.That(h.PendingLaunchAt).IsNull();
        Check.That(h.Bound).IsFalse();                       // unbound (the handler is detached)

        // A later exit of the released process books nothing (ProcessControllerTests pins the detach).
        h.Crash();
        h.Run(2);
        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Strikes).IsEqualTo(0);
    }

    [Fact]
    public void Scenario3_DisabledThenHang_StandsDownWithoutKilling()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);

        h.Enabled = false;
        h.Run(25);                                           // hang threshold 20 -> stale

        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Kills).IsEqualTo(0);                    // Off means the hang is left alone
        Check.That(h.Strikes).IsEqualTo(0);
    }

    [Fact]
    public void Scenario4_DisabledDuringTheBackoff_DoesNotLaunch()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);
        var launchesBefore = h.Launches;

        h.Crash();                                           // strike 1 -> Recovering, pending relaunch
        Check.That(h.State).IsEqualTo(SupervisorState.Recovering);
        h.Enabled = false;
        h.Run(3);

        Check.That(h.Launches).IsEqualTo(launchesBefore);    // the deferred launch never ran
        Check.That(h.Kills).IsEqualTo(0);
        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Scenario5_RecoveringHangLoop_EscalatesInBoundedTime_WithDefaults()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);

        h.Run(240);                                          // no heartbeats: the relaunch hangs every time

        Check.That(h.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(h.Fallbacks).IsEqualTo(1);
        Check.That(h.Strikes).IsEqualTo(4);
        Check.That(h.Kills).IsEqualTo(4);                    // F1: every hang strike kills the hung app
        Check.That(h.Listening).IsFalse();                   // F2: a FallenBack supervisor stops listening
        Check.That(h.FallenBackAtTick).IsNotNull();
        // Measured: tick 230 (~230 s), each stuck relaunch costs one 60 s grace + its backoff.
        Check.That(h.FallenBackAtTick!.Value).IsStrictlyGreaterThan(200);
        Check.That(h.FallenBackAtTick!.Value).IsStrictlyLessThan(240);
    }

    [Fact]
    public void Scenario6_ThrowingRelaunch_EscalatesInBoundedTime_WithDefaults()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);
        h.LaunchThrows = true;                               // the exe is gone: Process.Start throws

        h.Crash();
        h.Run(80);

        Check.That(h.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(h.Fallbacks).IsEqualTo(1);
        Check.That(h.FallenBackAtTick).IsNotNull();
        // Measured: tick 28 (~28 s): each throw strikes immediately and retries on the 0/5/15 backoff.
        Check.That(h.FallenBackAtTick!.Value).IsStrictlyGreaterThan(20);
        Check.That(h.FallenBackAtTick!.Value).IsStrictlyLessThan(60);
    }

    [Fact]
    public void Scenario7_SupervisorMediatedRestart_StillLaunchesAfterTheOldAppExits()
    {
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);
        var launchesBefore = h.Launches;

        h.Handle(new CleanExitRequested(h.Now, ClientVerified: true, RestartRequested: true));
        Check.That(h.State).IsEqualTo(SupervisorState.CleanExitPending);
        h.Crash();                                           // the old app exits
        Check.That(h.State).IsEqualTo(SupervisorState.Launching);

        h.Run(3);
        Check.That(h.Launches).IsEqualTo(launchesBefore + 1); // the successor was launched
        Check.That(h.State).IsEqualTo(SupervisorState.Launching);

        h.Heartbeat();
        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void Scenario8_AdoptedWithADuePendingLaunch_DropsTheLaunch()
    {
        var h = new Harness();

        h.Handle(new LaunchRequest(h.Now));                  // a launch is scheduled (logon/tray race)
        h.Heartbeat();                                       // but the app is already up and beats
        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);

        h.Run(3);

        Check.That(h.Launches).IsEqualTo(0);                 // obsolete pending consumed, not launched
        Check.That(h.PendingLaunchAt).IsNull();
        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void Scenario9_TrayLaunchWhileDisabled_LaunchesThenStandsDownWithoutKilling()
    {
        var h = new Harness { Enabled = false };

        h.Handle(new LaunchRequest(h.Now));                  // tray "Launch EyeCtrl now"
        h.Run(70);                                           // the disabled app never publishes; grace fires

        Check.That(h.Launches).IsEqualTo(1);                 // the button is not dead
        Check.That(h.Kills).IsEqualTo(0);                    // standing down never kills
        Check.That(h.Strikes).IsEqualTo(0);
        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Bound).IsFalse();
    }

    [Fact]
    public void Scenario10_TurnOn_EnabledLandsAfterStartup_AdoptsOnTheFirstHeartbeat()
    {
        // Ticket 04's turn-on flow: the App starts the Supervisor and persists enabled=true a few
        // ms later, while the app is already running. The enabled predicate is evaluated at the
        // adoption heartbeat, not cached at startup, so the running app is adopted on its first
        // heartbeat after the persist: Supervising, zero strikes, no launch.
        var h = new Harness { Enabled = false };
        h.Startup(startOnBoot: true, appAlreadyRunning: true);
        Check.That(h.Launches).IsEqualTo(0);

        h.Enabled = true;                                    // the App's persist lands

        for (var i = 0; i < 120; i++)
        {
            if (i % 3 == 0) h.Heartbeat();
            h.Tick();
        }

        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(h.Launches).IsEqualTo(0);                 // no duplicate (F1)
        Check.That(h.Kills).IsEqualTo(0);
        Check.That(h.Strikes).IsEqualTo(0);
    }

    [Fact]
    public void Scenario11_TurnOnRollbackToFalseWhileStartingUp_NeverAdoptsLaunchesOrKills()
    {
        // Ticket 04's StartedNotReachable rollback: the Supervisor read enabled=true while the app
        // runs, then the App rolls the flag back to false before the first heartbeat. The late
        // Supervisor must not adopt the running app, launch a duplicate, or kill anything.
        var h = new Harness { Enabled = true };
        h.Startup(startOnBoot: true, appAlreadyRunning: true);   // app running: the startup policy skips the launch
        Check.That(h.Launches).IsEqualTo(0);

        h.Enabled = false;                                    // the rollback lands while still coming up

        for (var i = 0; i < 70; i++)
        {
            h.Heartbeat();                                    // the app keeps publishing
            h.Tick();
        }

        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch); // never adopts
        Check.That(h.Launches).IsEqualTo(0);                      // never launches
        Check.That(h.Kills).IsEqualTo(0);                         // never kills
        Check.That(h.Strikes).IsEqualTo(0);
        Check.That(h.Bound).IsFalse();
    }

    [Fact]
    public void Scenario12_RetryThenGazeDeath_EscalatesWithAKill()
    {
        // Review 06 F1: after the dialog's Retry the failure count stays at 4, so the retried
        // app's next hang/gaze-death escalates immediately — and that escalation must kill the
        // app, not leave it holding the mutex with the one-shot Retry spent.
        var h = new Harness();
        h.Startup();
        h.Run(2);
        h.Heartbeat();
        h.Run(2);
        h.Run(240);                                          // hang loop -> FallenBack
        Check.That(h.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(h.Listening).IsFalse();                   // F2: the pipe closed
        var killsBefore = h.Kills;

        h.Retry();
        Check.That(h.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(h.Listening).IsTrue();                    // F2: the pipe reopened before the first heartbeat
        h.Heartbeat();
        Check.That(h.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(h.Strikes).IsEqualTo(4);                  // the budget does NOT reset on retry

        h.Handle(new GazeStreamDead(h.Now));

        Check.That(h.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(h.Kills).IsEqualTo(killsBefore + 1);      // F1: the retried app is killed
        Check.That(h.Strikes).IsEqualTo(5);
    }
}
