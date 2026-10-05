namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class SupervisionStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

    // Spec: IDLE_WATCH is the entry state, not Launching — so supervised + StartOnBoot-off
    // stays idle instead of launching. Only a LaunchRequest (logon+StartOnBoot, or tray
    // "Launch EyeCtrl now") or an AdoptRequest (manually-launched app's first heartbeat)
    // leaves idle.
    [Fact]
    public void StartsIn_IdleWatch()
    {
        var sm = NewMachine();
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void LaunchRequest_FromIdle_GoesLaunching_CommandsLaunchApp()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Launching);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Launching_FirstHeartbeat_GoesSupervising()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(10)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void AdoptRequest_FromIdle_GoesSupervising_NoLaunchCommand()
    {
        // Manual-launch adopt path: app we didn't launch sends a heartbeat.
        var sm = NewMachine();
        sm.Handle(new AdoptRequest(T0));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Supervising_HeartbeatStale_GoesRecovering_CommandsForceKill()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new HeartbeatStale(T0.AddSeconds(22)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void CrashLoop_BeyondMaxRetries_GoesFallenBack()
    {
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        for (int i = 1; i <= 4; i++)
            sm.Handle(new ProcessExited(T0.AddSeconds(i))); // strikes 1..4 (> maxRetries 3 -> escalate)
        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.ForceKill); // a crash already ended the process
    }

    // ── Ticket 06 review F1: escalation kills a hung process ──────────────────

    [Fact]
    public void Escalation_FromAHang_KillsBeforeFallingBack()
    {
        // A hang/gaze-death/grace escalation must kill the still-running app: FallenBack is
        // terminal for the session, and a hung process that keeps holding the app mutex makes the
        // dialog's Retry launch an instance that dies on it (review 06 F1).
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new StartupGraceElapsed(T0.AddSeconds(1)));   // strike 1 (force-kill recovery)
        sm.Handle(new StartupGraceElapsed(T0.AddSeconds(2)));   // strike 2
        sm.Handle(new StartupGraceElapsed(T0.AddSeconds(3)));   // strike 3

        sm.Handle(new StartupGraceElapsed(T0.AddSeconds(4)));   // strike 4 -> escalation

        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).ContainsExactly(SupervisorCommand.ForceKill, SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void Escalation_FromACrash_DoesNotKill()
    {
        // A crash already ended the process; escalation only launches the fallback AT.
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        for (int i = 1; i <= 4; i++) sm.Handle(new ProcessExited(T0.AddSeconds(i)));

        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void AdoptRequest_WhenDisabled_IsIgnored()
    {
        // Ticket 06-B/F2: the adoption gate lives in the machine, so it is covered by machine
        // tests rather than by untested SupervisorApp glue.
        var sm = NewMachine(isSupervisionEnabled: () => false);

        sm.Handle(new AdoptRequest(T0));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).IsEmpty();
    }

    [Fact]
    public void SurvivedWindow_ResetsStrikeAndBackoffCounters()
    {
        // Spec reset rule (no infinite-restart hole): surviving a FULL crash window (120s)
        // in Supervising resets both the strike counter and the backoff counter.
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));   // strike 1, backoff -> immediate (DelayFor(1)=Curve[0])
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero); // strike 1 relaunch is immediate (spec worked example)
        sm.Handle(new LaunchRequest(T0.AddSeconds(10)));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(11)));
        // Now survive > 120s cleanly:
        sm.Handle(new SurvivedWindow(T0.AddSeconds(200)));
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero); // backoff reset to immediate
        // And a subsequent single crash should not escalate (strikes reset):
        sm.Handle(new ProcessExited(T0.AddSeconds(201)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void CleanExit_AuthenticatedWithinDeadline_GoesIdleWatch_NoRelaunch()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new ProcessExited(T0.AddSeconds(6))); // exits within deadline
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void CleanExit_NotExitedWithinDeadline_GoesRecovering()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true));
        sm.Handle(new DeadlineElapsed(T0.AddSeconds(11))); // > 5s, still alive
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void UnannouncedProcessExit_GoesRecovering_NotIdle()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new ProcessExited(T0.AddSeconds(5))); // no CleanExitRequested
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void CleanExit_UnverifiedClient_Ignored_DoesNotSuppressRecovery()
    {
        // Spec: an unverified/unbound sender's shuttingDown:true must NOT
        // suppress crash recovery. HeartbeatPipeServer only sets ClientVerified after
        // ClientIdentityBinder succeeds. An unverified request leaves us Supervising.
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: false));
        Check.That(sm.State).IsEqualTo(SupervisorState.Supervising); // unchanged
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.NotifyCleanExit);
    }

    [Fact]
    public void Backoff_IsImmediateAfterStrike1_Then5s_15s_60s()
    {
        // F3 regression: the spec worked example has strike 1 -> IMMEDIATE relaunch (curve[0]),
        // strike 2 -> 5s, strike 3 -> 15s, strike 4+ -> 60s. NextBackoffDelay = DelayFor(ConsecutiveFailures).
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        // 0 strikes -> immediate (DelayFor(0) guards idx<0 -> Zero)
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero);
        sm.Handle(new ProcessExited(T0.AddSeconds(1))); // strike 1
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.Zero); // strike 1 relaunch is IMMEDIATE (spec)
        sm.Handle(new ProcessExited(T0.AddSeconds(2))); // strike 2
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.FromSeconds(5));
        sm.Handle(new ProcessExited(T0.AddSeconds(3))); // strike 3
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.FromSeconds(15));
        sm.Handle(new ProcessExited(T0.AddSeconds(4))); // strike 4 -> escalate, but delay still computed
        Check.That(sm.NextBackoffDelay).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void GazeStreamDead_FromSupervising_GoesRecovering_NotRevivedBySameHeartbeat()
    {
        // F4 regression: a single dead heartbeat must NOT both kill AND revive. The orchestrator
        // (Program.OnHeartbeatReceived) returns after GazeStreamDead; this test pins the machine-side
        // half — that once Recovering, a HeartbeatReceived (the would-be "same payload") flips to
        // Supervising only if it is a GENUINE fresh first-beat after relaunch, not a continuation.
        // The machine's contract: GazeStreamDead from Supervising -> GoRecovering (strike). A subsequent
        // HeartbeatReceived in Recovering DOES promote to Supervising (the relaunched app's first beat) —
        // but the orchestrator must not feed the SAME payload. This asserts the machine's half is correct
        // and documents the orchestrator's responsibility (the return-after-GazeStreamDead).
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1))); // -> Supervising
        sm.Handle(new GazeStreamDead(T0.AddSeconds(30)));   // -> Recovering (strike 1), ForceKill commanded
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.Commands).Contains(SupervisorCommand.ForceKill);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(1); // exactly one strike for one incident
    }

    // ── Ticket 06-C: the recovery launch clock and a failing relaunch ─────────

    [Fact]
    public void LaunchExecuted_InRecovering_StampsTheLaunchClock()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new ProcessExited(T0.AddSeconds(5)));       // -> Recovering, clears the clock
        Check.That(sm.LaunchingSince).IsNull();               // no launch attempt is in flight yet

        sm.Handle(new LaunchExecuted(T0.AddSeconds(10)));

        Check.That(sm.LaunchingSince).IsEqualTo(T0.AddSeconds(10));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    [Fact]
    public void LaunchExecuted_InLaunching_StampsTheLaunchClock()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));

        sm.Handle(new LaunchExecuted(T0.AddSeconds(10)));

        Check.That(sm.LaunchingSince).IsEqualTo(T0.AddSeconds(10));
    }

    [Fact]
    public void LaunchFailed_InRecovering_BooksAStrike_AndCommandsRelaunch()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));        // strike 1, Recovering

        sm.Handle(new LaunchFailed(T0.AddSeconds(2)));         // the relaunch threw

        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(2);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void LaunchFailed_WhenDisabled_StandsDown_NoStrike()
    {
        var enabled = true;
        var sm = NewMachine(isSupervisionEnabled: () => enabled);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));        // strike 1 while enabled
        enabled = false;

        sm.Handle(new LaunchFailed(T0.AddSeconds(2)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(1);       // only the original strike
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Escalation_ConsecutiveFailuresBeyondMaxRetries_EvenOutsideTheWindow()
    {
        // Ticket 06-C: the window rule alone never escalates a crash loop slower than the window
        // (e.g. a 60s grace per failed relaunch). Four failures in a row with no survived window
        // in between must escalate too.
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0));                        // strike 1 (~t0)
        sm.Handle(new ProcessExited(T0.AddSeconds(100)));        // strike 2: window holds 2
        sm.Handle(new ProcessExited(T0.AddSeconds(200)));        // strike 3: window holds 1
        sm.Handle(new ProcessExited(T0.AddSeconds(300)));        // strike 4: window holds 1, consecutive 4 > 3

        Check.That(sm.State).IsEqualTo(SupervisorState.FallenBack);
        Check.That(sm.Commands).Contains(SupervisorCommand.LaunchFallbackAt);
    }

    [Fact]
    public void SurvivedWindow_ResetsConsecutiveFailures_SoLaterCrashesDoNotEscalate()
    {
        var sm = NewMachine(maxRetries: 3, windowSeconds: 120);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0));                        // strike 1
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));          // strike 2
        sm.Handle(new ProcessExited(T0.AddSeconds(2)));          // strike 3 (at the limit)
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(3);

        sm.Handle(new HeartbeatReceived(T0.AddSeconds(10)));     // Recovering -> Supervising
        sm.Handle(new SurvivedWindow(T0.AddSeconds(200)));       // full window survived -> reset

        Check.That(sm.ConsecutiveFailures).IsEqualTo(0);
        sm.Handle(new ProcessExited(T0.AddSeconds(201)));        // strike 1 again
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);
    }

    // ── Ticket 06-B: stand down when supervision is off ───────────────────────

    [Fact]
    public void Recovery_WhenDisabled_StandsDown_NoKillNoRelaunchNoStrike()
    {
        var sm = NewMachine(isSupervisionEnabled: () => false);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));

        sm.Handle(new HeartbeatStale(T0.AddSeconds(22)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(0);
        Check.That(sm.Commands).Contains(SupervisorCommand.StandDown);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.ForceKill);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void Recovery_ReReadsThePredicateAtEachDecision()
    {
        var enabled = true;
        var sm = NewMachine(isSupervisionEnabled: () => enabled);
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new HeartbeatStale(T0.AddSeconds(22)));        // enabled -> recovers
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);

        enabled = false;
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(30)));     // relaunched app beats -> Supervising
        sm.Handle(new HeartbeatStale(T0.AddSeconds(60)));        // now disabled -> stands down

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.ConsecutiveFailures).IsEqualTo(1);         // the second incident booked nothing
    }

    [Fact]
    public void SupervisionDisabled_FromSupervising_GoesIdle_EmitsStandDown_ClearsTimers()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        Check.That(sm.SupervisingSince).IsNotNull();

        sm.Handle(new SupervisionDisabled(T0.AddSeconds(5)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(sm.SupervisingSince).IsNull();
        Check.That(sm.LaunchingSince).IsNull();
        Check.That(sm.Commands).Contains(SupervisorCommand.StandDown);
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.ForceKill);
    }

    [Fact]
    public void SupervisionDisabled_FromRecovering_GoesIdle()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new ProcessExited(T0.AddSeconds(1)));
        Check.That(sm.State).IsEqualTo(SupervisorState.Recovering);

        sm.Handle(new SupervisionDisabled(T0.AddSeconds(5)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void SupervisionDisabled_FromLaunching_GoesIdle()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));

        sm.Handle(new SupervisionDisabled(T0.AddSeconds(5)));

        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void SupervisionDisabled_FromCleanExitPending_ClearsRestartIntent()
    {
        var sm = NewMachine();
        sm.Handle(new LaunchRequest(T0));
        sm.Handle(new HeartbeatReceived(T0.AddSeconds(1)));
        sm.Handle(new CleanExitRequested(T0.AddSeconds(5), ClientVerified: true, RestartRequested: true));
        Check.That(sm.State).IsEqualTo(SupervisorState.CleanExitPending);

        sm.Handle(new SupervisionDisabled(T0.AddSeconds(6)));
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);

        sm.Handle(new ProcessExited(T0.AddSeconds(7)));
        Check.That(sm.State).IsEqualTo(SupervisorState.IdleWatch);   // no relaunch from IdleWatch
        Check.That(sm.Commands).Not.Contains(SupervisorCommand.LaunchApp);
    }

    [Fact]
    public void SupervisionDisabled_InIdleWatchAndFallenBack_IsANoOp()
    {
        var idle = NewMachine();
        idle.Handle(new SupervisionDisabled(T0));
        Check.That(idle.State).IsEqualTo(SupervisorState.IdleWatch);
        Check.That(idle.Commands).IsEmpty();

        var fallen = NewMachine();
        fallen.Handle(new LaunchRequest(T0));
        for (int i = 1; i <= 4; i++) fallen.Handle(new ProcessExited(T0.AddSeconds(i)));
        Check.That(fallen.State).IsEqualTo(SupervisorState.FallenBack);

        fallen.Handle(new SupervisionDisabled(T0.AddSeconds(5)));

        Check.That(fallen.State).IsEqualTo(SupervisorState.FallenBack);
    }

    private static SupervisionStateMachine NewMachine(int maxRetries = 3, int windowSeconds = 120, Func<bool>? isSupervisionEnabled = null)
        => new(maxRetries, windowSeconds, isSupervisionEnabled);
}
