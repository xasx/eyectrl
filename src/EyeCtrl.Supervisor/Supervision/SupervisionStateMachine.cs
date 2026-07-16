namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class SupervisionStateMachine
{
    private readonly CrashWindow _crash;
    private readonly BackoffPolicy _backoff = new();
    private readonly int _windowResetSeconds;
    private readonly List<SupervisorCommand> _commands = new();
    private DateTime? _supervisingSince;

    public SupervisorState State { get; private set; } = SupervisorState.IdleWatch; // spec entry state
    public IReadOnlyList<SupervisorCommand> Commands => _commands;
    public DateTime? LastHeartbeatUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan NextBackoffDelay => _backoff.DelayFor(ConsecutiveFailures + 1);

    public SupervisionStateMachine(int maxRetries, int windowSeconds, int hangThresholdSeconds,
        int startupGraceSeconds, int windowResetSeconds)
    {
        _crash = new CrashWindow(maxRetries, windowSeconds);
        _windowResetSeconds = windowResetSeconds;
    }

    public void Handle(SupervisionInput input)
    {
        _commands.Clear();
        switch (input)
        {
            case LaunchRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Launching;
                _commands.Add(SupervisorCommand.LaunchApp);
                break;

            case AdoptRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            // First heartbeat after a launch (Launching) OR after a recovery relaunch
            // (Recovering — GoRecovering already commanded LaunchApp; the relaunched
            // process's first heartbeat means it is alive -> Supervising).
            case HeartbeatReceived when State is SupervisorState.Launching or SupervisorState.Recovering:
                LastHeartbeatUtc = input.At;
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            case HeartbeatReceived when State is SupervisorState.Supervising or SupervisorState.CleanExitPending:
                LastHeartbeatUtc = input.At;
                break;

            case SurvivedWindow when State == SupervisorState.Supervising:
                // Spec reset rule: surviving a full crash window resets BOTH counters.
                _crash.Reset();
                ConsecutiveFailures = 0;
                _supervisingSince = input.At;
                break;

            case HeartbeatStale when State == SupervisorState.Supervising:
            case GazeStreamDead when State == SupervisorState.Supervising:
                GoRecovering(input.At, forceKill: true);
                break;

            case CleanExitRequested ce when State == SupervisorState.Supervising:
                // Spec: the shuttingDown signal "must come from the verified supervised
                // process, not an untrusted sender." An unverified request is IGNORED —
                // it must not suppress crash recovery.
                if (ce.ClientVerified)
                {
                    State = SupervisorState.CleanExitPending;
                    _commands.Add(SupervisorCommand.NotifyCleanExit);
                }
                break;

            case ProcessExited when State == SupervisorState.CleanExitPending:
                State = SupervisorState.IdleWatch; // authenticated clean exit within deadline
                _supervisingSince = null;
                break;

            case ProcessExited when State is SupervisorState.Supervising or SupervisorState.Recovering or SupervisorState.Launching:
                GoRecovering(input.At, forceKill: false); // unannounced exit / startup crash
                break;

            case DeadlineElapsed when State == SupervisorState.CleanExitPending:
                GoRecovering(input.At, forceKill: true); // claimed shutdown but didn't exit
                break;
        }
    }

    private void GoRecovering(DateTime at, bool forceKill)
    {
        ConsecutiveFailures++;
        _crash.RecordStrike(at);
        if (_crash.ShouldEscalate(at))
        {
            State = SupervisorState.FallenBack;
            _commands.Add(SupervisorCommand.LaunchFallbackAt);
        }
        else
        {
            State = SupervisorState.Recovering;
            if (forceKill) _commands.Add(SupervisorCommand.ForceKill);
            _commands.Add(SupervisorCommand.LaunchApp);
        }
        _supervisingSince = null;
    }
}
