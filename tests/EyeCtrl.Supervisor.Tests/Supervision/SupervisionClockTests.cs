namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The supervisor's clock-tick decisions, extracted from <c>SupervisorApp</c> (ticket 03). A wrong
/// state guard or a <c>&gt;</c> vs <c>&gt;=</c> here could otherwise only be caught live, by a
/// gaze-only user whose app has just hung. Every threshold is pinned just below, exactly at and
/// just above its edge; every check against each state it must NOT fire in; and the tick's order
/// against the real <see cref="SupervisionStateMachine"/>.
/// </summary>
public class SupervisionClockTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);

    private static IEnumerable<SupervisorState> AllStatesExcept(params SupervisorState[] excluded) =>
        Enum.GetValues<SupervisorState>().Where(x => !excluded.Contains(x));

    public static TheoryData<SupervisorState> NotLaunchingOrRecovering() => new(AllStatesExcept(SupervisorState.Launching, SupervisorState.Recovering));
    public static TheoryData<SupervisorState> NotSupervising() => new(AllStatesExcept(SupervisorState.Supervising));
    public static TheoryData<SupervisorState> NotCleanExitPending() => new(AllStatesExcept(SupervisorState.CleanExitPending));

    // ── Startup grace (Launching or Recovering, > StartupGraceSeconds) ───────

    [Theory]
    [InlineData(SupervisorState.Launching, -1, false)]   // just below
    [InlineData(SupervisorState.Launching, 0, false)]    // exactly at: strictly greater is required
    [InlineData(SupervisorState.Launching, 1, true)]     // just above
    [InlineData(SupervisorState.Recovering, -1, false)]  // ticket 06-C: a recovery relaunch is
    [InlineData(SupervisorState.Recovering, 0, false)]   // bounded by the same grace, so a stuck
    [InlineData(SupervisorState.Recovering, 1, true)]    // Recovering is never terminal
    public void StartupGrace_Edge(SupervisorState state, int offsetMs, bool fires) =>
        Check.That(SupervisionClock.StartupGraceElapsed(
                T0.AddSeconds(60).AddMilliseconds(offsetMs), state, T0, 60))
            .IsEqualTo(fires);

    [Theory]
    [MemberData(nameof(NotLaunchingOrRecovering))]
    public void StartupGrace_OnlyWhileLaunchingOrRecovering(SupervisorState state) =>
        Check.That(SupervisionClock.StartupGraceElapsed(T0.AddSeconds(600), state, T0, 60)).IsFalse();

    [Theory]
    [InlineData(SupervisorState.Launching)]
    [InlineData(SupervisorState.Recovering)]
    public void StartupGrace_NoLaunchTimestamp_NeverFires(SupervisorState state) =>
        Check.That(SupervisionClock.StartupGraceElapsed(T0.AddSeconds(600), state, null, 60)).IsFalse();

    // ── Heartbeat stale (Supervising, > HangThresholdSeconds) ────────────────

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void HeartbeatStale_Edge(int offsetMs, bool fires) =>
        Check.That(SupervisionClock.HeartbeatStale(
                T0.AddSeconds(20).AddMilliseconds(offsetMs), SupervisorState.Supervising, T0, 20))
            .IsEqualTo(fires);

    // The Recovering row pins TODAY's behaviour: a known gap (a relaunch that never heartbeats
    // leaves the Supervisor stuck there), tracked as ticket 06 — not a statement that it is correct.
    [Theory]
    [MemberData(nameof(NotSupervising))]
    public void HeartbeatStale_OnlyWhileSupervising(SupervisorState state) =>
        Check.That(SupervisionClock.HeartbeatStale(T0.AddSeconds(600), state, T0, 20)).IsFalse();

    [Fact]
    public void HeartbeatStale_NoHeartbeatYet_NeverFires() =>
        Check.That(SupervisionClock.HeartbeatStale(T0.AddSeconds(600), SupervisorState.Supervising, null, 20)).IsFalse();

    // ── Survived crash window (Supervising, > CrashWindowSeconds) ────────────

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void SurvivedCrashWindow_Edge(int offsetMs, bool fires) =>
        Check.That(SupervisionClock.SurvivedCrashWindow(
                T0.AddSeconds(120).AddMilliseconds(offsetMs), SupervisorState.Supervising, T0, 120))
            .IsEqualTo(fires);

    [Theory]
    [MemberData(nameof(NotSupervising))]
    public void SurvivedCrashWindow_OnlyWhileSupervising(SupervisorState state) =>
        Check.That(SupervisionClock.SurvivedCrashWindow(T0.AddSeconds(6000), state, T0, 120)).IsFalse();

    [Fact]
    public void SurvivedCrashWindow_NoSupervisingStart_NeverFires() =>
        Check.That(SupervisionClock.SurvivedCrashWindow(T0.AddSeconds(6000), SupervisorState.Supervising, null, 120)).IsFalse();

    // ── Clean-exit deadline (CleanExitPending, > 5 s) ────────────────────────

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void CleanExitDeadline_Edge(int offsetMs, bool fires) =>
        Check.That(SupervisionClock.CleanExitDeadlineElapsed(
                T0.AddSeconds(SupervisionClock.CleanExitDeadlineSeconds).AddMilliseconds(offsetMs),
                SupervisorState.CleanExitPending, T0))
            .IsEqualTo(fires);

    [Fact]
    public void CleanExitDeadline_IsFiveSeconds() =>
        Check.That(SupervisionClock.CleanExitDeadlineSeconds).IsEqualTo(5);

    [Theory]
    [MemberData(nameof(NotCleanExitPending))]
    public void CleanExitDeadline_OnlyWhileCleanExitPending(SupervisorState state) =>
        Check.That(SupervisionClock.CleanExitDeadlineElapsed(T0.AddSeconds(600), state, T0)).IsFalse();

    [Fact]
    public void CleanExitDeadline_NoRequestTimestamp_NeverFires() =>
        Check.That(SupervisionClock.CleanExitDeadlineElapsed(T0.AddSeconds(600), SupervisorState.CleanExitPending, null)).IsFalse();

    // ── Deferred relaunch (at or after its time) ─────────────────────────────

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]     // exactly at: due (>=), unlike the four thresholds above
    [InlineData(1, true)]
    public void RelaunchDue_Edge(int offsetMs, bool due) =>
        Check.That(SupervisionClock.RelaunchDue(T0.AddMilliseconds(offsetMs), T0)).IsEqualTo(due);

    [Fact]
    public void RelaunchDue_NothingPending_NeverDue() =>
        Check.That(SupervisionClock.RelaunchDue(T0.AddSeconds(600), null)).IsFalse();

    // ── Which states still expect a launch (ticket 06-A) ─────────────────────
    // A pending relaunch executes only while the machine is Launching (a requested launch) or
    // Recovering (a recovery relaunch). In any other state it is obsolete — the app came up some
    // other way (adoption), or the machine moved on (escalation) — and launching would race a
    // live instance.

    [Theory]
    [InlineData(SupervisorState.Launching, true)]
    [InlineData(SupervisorState.Recovering, true)]
    [InlineData(SupervisorState.IdleWatch, false)]
    [InlineData(SupervisorState.Supervising, false)]
    [InlineData(SupervisorState.CleanExitPending, false)]
    [InlineData(SupervisorState.FallenBack, false)]
    public void LaunchExpected_OnlyWhileLaunchingOrRecovering(SupervisorState state, bool expected) =>
        Check.That(SupervisionClock.LaunchExpected(state)).IsEqualTo(expected);

    // ── The tick, against the real state machine ─────────────────────────────

    /// <summary>A supervisor stand-in over a real <see cref="SupervisionStateMachine"/>, recording
    /// what the tick does. Stamps <see cref="CleanExitRequestedAt"/> as <c>SupervisorApp.HandleInput</c>
    /// does.</summary>
    private sealed class Host : ISupervisionClockHost
    {
        // Deliberately NOT the class defaults (60/20/120): three explicit, pairwise-distinct
        // thresholds so a tick-level test can tell WHICH setting a check reads. A copy-paste that
        // wires the wrong one (mutants W1-W3) changes the outcome at hang+1 s.
        private const int GraceSeconds = 45;
        private const int HangSeconds = 15;
        private const int WindowSeconds = 115;

        public SupervisionSettings Settings { get; } = new()
        {
            StartupGraceSeconds = GraceSeconds,
            HangThresholdSeconds = HangSeconds,
            CrashWindowSeconds = WindowSeconds,
        };
        public SupervisionStateMachine Machine { get; } = new(maxRetries: 3, windowSeconds: WindowSeconds);
        public bool SupervisionEnabled { get; set; } = true;
        public DateTime? CleanExitRequestedAt { get; private set; }
        public DateTime? PendingLaunchAt { get; set; }
        public List<string> Actions { get; } = new();
        public List<DateTime> InputTimes { get; } = new();

        public void Handle(SupervisionInput input)
        {
            Actions.Add(input.GetType().Name);
            InputTimes.Add(input.At);
            Feed(input);
        }

        /// <summary>Setup inputs (not recorded as tick actions).</summary>
        public void Feed(SupervisionInput input)
        {
            var prev = Machine.State;
            Machine.Handle(input);
            if (Machine.State == SupervisorState.CleanExitPending && prev != SupervisorState.CleanExitPending)
                CleanExitRequestedAt = input.At;
            else if (Machine.State != SupervisorState.CleanExitPending)
                CleanExitRequestedAt = null;
        }

        public void ClearPendingLaunch() => PendingLaunchAt = null;

        /// <summary>Models <c>DoLaunchApp</c>: true when a process started, false when it threw.</summary>
        public bool LaunchFails { get; set; }

        public bool TryLaunchPendingNow()
        {
            Actions.Add("LaunchPendingNow");
            return !LaunchFails;
        }

        public void WireLaunchedProcess() => Actions.Add("WireLaunchedProcess");
    }

    private static Host Supervising(DateTime since, DateTime lastHeartbeat)
    {
        var host = new Host();
        host.Feed(new AdoptRequest(since));                  // IdleWatch -> Supervising, SupervisingSince = since
        host.Feed(new HeartbeatReceived(lastHeartbeat));
        Check.That(host.Machine.State).IsEqualTo(SupervisorState.Supervising);
        return host;
    }

    [Fact]
    public void Tick_AStaleHeartbeat_PreemptsTheSurvivedWindowInTheSameTick()
    {
        // Both thresholds are past (supervising 121 s, last heartbeat 121 s ago). The stale check
        // runs first and moves the machine to Recovering, so the survived-window check must NOT
        // fire in the same tick. Mutations: evaluate all checks from one snapshot, or run the
        // survived-window check before the stale one → SurvivedWindow is fed too.
        var host = Supervising(T0, T0);
        var now = T0.AddSeconds(121);

        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).ContainsExactly("HeartbeatStale");
        Check.That(host.InputTimes).ContainsExactly(now);   // the strike is booked at the tick's time
        Check.That(host.Machine.State).IsNotEqualTo(SupervisorState.Supervising);
    }

    [Fact]
    public void Tick_SurvivedWindow_FiresOnce_ThenReArms()
    {
        var host = Supervising(T0, T0.AddSeconds(119));    // heartbeat fresh at the tick

        SupervisionClock.Tick(T0.AddSeconds(121), host);
        Check.That(host.Actions).ContainsExactly("SurvivedWindow");

        SupervisionClock.Tick(T0.AddSeconds(122), host);   // SupervisingSince re-armed to 121 s
        Check.That(host.Actions).ContainsExactly("SurvivedWindow");
    }

    [Fact]
    public void Tick_StartupGrace_ReadsTheLaunchStart()
    {
        // LaunchRequest stamps LaunchingSince from the real clock (state machine, out of scope), so
        // the tick's "now" is taken from the real clock too. Mutation: read SupervisingSince (null
        // while Launching) instead of LaunchingSince → the grace never elapses.
        var host = new Host();
        host.Feed(new LaunchRequest(DateTime.UtcNow));
        Check.That(host.Machine.State).IsEqualTo(SupervisorState.Launching);

        var now = DateTime.UtcNow.AddSeconds(host.Settings.StartupGraceSeconds + 1);
        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).ContainsExactly("StartupGraceElapsed");
        Check.That(host.InputTimes).ContainsExactly(now);
        Check.That(host.Machine.State).IsNotEqualTo(SupervisorState.Launching);
    }

    [Fact]
    public void Tick_CleanExitDeadline_UsesTheHostsRequestTime()
    {
        var host = Supervising(T0, T0);
        host.Feed(new CleanExitRequested(T0.AddSeconds(1), ClientVerified: true));
        Check.That(host.Machine.State).IsEqualTo(SupervisorState.CleanExitPending);

        SupervisionClock.Tick(T0.AddSeconds(6), host);     // exactly 5 s after: not yet
        Check.That(host.Actions).IsEmpty();

        var now = T0.AddSeconds(6).AddMilliseconds(1);
        SupervisionClock.Tick(now, host);
        Check.That(host.Actions).ContainsExactly("DeadlineElapsed");
        Check.That(host.InputTimes).ContainsExactly(now);
    }

    [Fact]
    public void Tick_TheDeferredRelaunchIsCheckedLast()
    {
        // Mutation: check the relaunch before the four thresholds → it launches first. The clear is
        // part of Tick's contract (finding 3), so the final check observes production code: a Tick
        // that launched without clearing (old mutant A1, now in Tick) leaves PendingLaunchAt due.
        var host = new Host();
        host.Feed(new LaunchRequest(DateTime.UtcNow));
        var now = DateTime.UtcNow.AddSeconds(host.Settings.StartupGraceSeconds + 1);
        host.PendingLaunchAt = now;

        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).ContainsExactly("StartupGraceElapsed", "LaunchPendingNow", "LaunchExecuted", "WireLaunchedProcess");
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Tick_NothingDue_DoesNothing()
    {
        var host = Supervising(T0, T0.AddSeconds(100));

        SupervisionClock.Tick(T0.AddSeconds(110), host);

        Check.That(host.Actions).IsEmpty();
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Tick_PendingNotDue_InRecovering_IsKept()
    {
        var host = Supervising(T0, T0);
        host.Feed(new ProcessExited(T0.AddSeconds(1)));
        Check.That(host.Machine.State).IsEqualTo(SupervisorState.Recovering);
        host.PendingLaunchAt = T0.AddSeconds(200);

        SupervisionClock.Tick(T0.AddSeconds(110), host);

        Check.That(host.Actions).IsEmpty();
        Check.That(host.PendingLaunchAt).IsEqualTo(T0.AddSeconds(200));
    }

    [Fact]
    public void Tick_ObsoletePending_InSupervising_IsConsumedNotLaunched()
    {
        // Ticket 06-A: the app was adopted (or came up another way) while a deferred launch was
        // still pending. The machine no longer expects a launch, so the tick consumes it instead
        // of racing a second instance into the app's single-instance mutex.
        var host = Supervising(T0, T0);
        host.PendingLaunchAt = T0;   // already due

        SupervisionClock.Tick(T0.AddSeconds(1), host);

        Check.That(host.Actions).IsEmpty();
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Tick_PendingInLaunching_ExecutesEvenWhenDisabled()
    {
        // Ticket 06-B: the tray "Launch EyeCtrl now" must not become a dead button when
        // supervision is off. Launching is not gated on the enabled read; F1 safety comes from the
        // startup policy, and a disabled launch is stood down by the grace (Launching ->
        // GoRecovering -> disabled -> StandDown) without a kill.
        var host = new Host { SupervisionEnabled = false };
        host.Feed(new LaunchRequest(DateTime.UtcNow));
        var now = DateTime.UtcNow.AddSeconds(host.Settings.HangThresholdSeconds + 1);
        host.PendingLaunchAt = now;

        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).Contains("LaunchPendingNow");
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Tick_PendingInRecovering_WhenEnabled_Executes()
    {
        var host = Supervising(T0, T0);
        host.Feed(new ProcessExited(T0.AddSeconds(1)));
        host.PendingLaunchAt = T0.AddSeconds(10);

        SupervisionClock.Tick(T0.AddSeconds(10), host);

        Check.That(host.Actions).Contains("LaunchPendingNow");
        Check.That(host.PendingLaunchAt).IsNull();
    }

    [Fact]
    public void Tick_PendingInRecovering_WhenDisabled_StandsDownWithoutLaunching()
    {
        // Ticket 06-B deferred-launch race: enabled was true when the strike was booked, but the
        // fresh read at execution time says off. No launch, no kill — stand down.
        var host = Supervising(T0, T0);
        host.Feed(new ProcessExited(T0.AddSeconds(1)));
        host.PendingLaunchAt = T0.AddSeconds(10);
        host.SupervisionEnabled = false;

        SupervisionClock.Tick(T0.AddSeconds(10), host);

        Check.That(host.Actions).Contains("SupervisionDisabled");
        Check.That(host.Actions).Not.Contains("LaunchPendingNow");
        Check.That(host.PendingLaunchAt).IsNull();
        Check.That(host.Machine.State).IsEqualTo(SupervisorState.IdleWatch);
    }

    [Fact]
    public void Tick_FeedsLaunchOutcome_BeforeTheWireGuard()
    {
        // Ticket 06 review F4: a process that already exited at wire time must be seen as
        // launched-then-exited. The outcome feed comes first; only then the race guard.
        var host = Supervising(T0, T0);
        host.Feed(new ProcessExited(T0.AddSeconds(1)));   // Recovering
        host.PendingLaunchAt = T0.AddSeconds(10);

        SupervisionClock.Tick(T0.AddSeconds(10), host);

        Check.That(host.Actions).ContainsExactly("LaunchPendingNow", "LaunchExecuted", "WireLaunchedProcess");
    }

    [Fact]
    public void Tick_FailedLaunch_FeedsLaunchFailed_AndBooksAStrike()
    {
        // Ticket 06-F2: the launch outcome is fed by Tick, not by SupervisorApp glue, so deleting
        // the feeder (mutants P2/P3) is a clock-level mutation covered here and by Scenario 6.
        var host = Supervising(T0, T0);
        host.Feed(new ProcessExited(T0.AddSeconds(1)));   // strike 1, Recovering
        host.PendingLaunchAt = T0.AddSeconds(10);
        host.LaunchFails = true;

        SupervisionClock.Tick(T0.AddSeconds(10), host);

        Check.That(host.Actions).Contains("LaunchPendingNow");
        Check.That(host.Actions).Contains("LaunchFailed");
        Check.That(host.Actions).Not.Contains("LaunchExecuted");
        Check.That(host.Actions).Not.Contains("WireLaunchedProcess");   // no process to guard
        Check.That(host.Machine.ConsecutiveFailures).IsEqualTo(2);   // the throw booked a strike
    }

    [Fact]
    public void Tick_EveryInputCarriesTheTicksTime()
    {
        // The machine re-arms SupervisingSince to input.At; a different stamp would shift the next
        // survived window. Mutation: stamp an input with anything but the tick's now → red.
        var host = Supervising(T0, T0.AddSeconds(119));
        var now = T0.AddSeconds(121);

        SupervisionClock.Tick(now, host);

        Check.That(host.InputTimes).ContainsExactly(now);
        Check.That(host.Machine.SupervisingSince).IsEqualTo(now);
    }

    // ── The tick wires each check to its own setting (W1-W3) ─────────────────
    // The pure-function edge tests above take thresholds as parameters, so they cannot see a
    // copy-paste in Tick that passes the wrong setting. The host's thresholds are pairwise
    // distinct (grace 45, hang 15, window 115); probing at hang+1 lands past the hang threshold
    // but well inside the other two, so a mis-wired check flips the outcome.

    [Fact]
    public void Tick_LaunchingAtHangPlusOne_DoesNotFireStartupGrace()
    {
        var host = new Host();
        host.Feed(new LaunchRequest(DateTime.UtcNow));

        // 16 s into Launching: past HangThresholdSeconds, far inside StartupGraceSeconds. Mutant
        // W1 (grace reads HangThresholdSeconds) fires StartupGraceElapsed here.
        var now = DateTime.UtcNow.AddSeconds(host.Settings.HangThresholdSeconds + 1);
        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).IsEmpty();
    }

    [Fact]
    public void Tick_SupervisingAtHangPlusOne_HeartbeatStale()
    {
        var host = Supervising(T0, T0);

        // 16 s since the last heartbeat: past HangThresholdSeconds, inside StartupGraceSeconds and
        // CrashWindowSeconds. Mutants W2 (stale reads StartupGraceSeconds) and W3 (stale reads
        // CrashWindowSeconds) do not fire HeartbeatStale here.
        var now = T0.AddSeconds(host.Settings.HangThresholdSeconds + 1);
        SupervisionClock.Tick(now, host);

        Check.That(host.Actions).ContainsExactly("HeartbeatStale");
    }
}
