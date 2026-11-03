namespace EyeCtrl.Supervisor.Supervision;

using System;

public abstract record SupervisionInput(DateTime At);
public sealed record LaunchRequest(DateTime At) : SupervisionInput(At);     // logon+StartOnBoot / tray "Launch now"
public sealed record AdoptRequest(DateTime At) : SupervisionInput(At);      // manual-launch first heartbeat
public sealed record HeartbeatReceived(DateTime At) : SupervisionInput(At);
public sealed record HeartbeatStale(DateTime At) : SupervisionInput(At);
public sealed record GazeStreamDead(DateTime At) : SupervisionInput(At);
/// <summary>The supervised process exited (an unannounced crash, or the retried app's startup
/// death).</summary>
/// <param name="Pid">
/// The OS process the exit was observed on, when the feeder knows it (A7 fixup-01/S1). The
/// supervisor drops an exit whose pid is no longer the BOUND process: after a FallenBack retry
/// rebinds, an <c>Exited</c> callback marshalled for the previous instance would otherwise strike
/// the fresh launch for a crash it did not have.
/// </param>
public sealed record ProcessExited(DateTime At, int? Pid = null) : SupervisionInput(At)
{
    /// <summary>True when this exit may count against the process the supervisor is bound to.
    /// Untagged exits (<see cref="Pid"/> null) keep the pre-existing behaviour for probes and
    /// machine tests; a tagged exit is accepted only for the pid currently bound.</summary>
    public bool Matches(int? boundPid) => Pid is null || Pid == boundPid;
}

/// <param name="RestartRequested">
/// <c>true</c> when the app asked to be relaunched after it exits (user-requested
/// restart). Books no strike and skips backoff; see RelaunchDelayPolicy.
/// </param>
public sealed record CleanExitRequested(DateTime At, bool ClientVerified, bool RestartRequested = false)
    : SupervisionInput(At);
public sealed record DeadlineElapsed(DateTime At) : SupervisionInput(At);
public sealed record SurvivedWindow(DateTime At) : SupervisionInput(At);    // survived a full crash window -> reset
public sealed record StartupGraceElapsed(DateTime At) : SupervisionInput(At); // 60s in LAUNCHING with no heartbeat -> RECOVERING (spec failure mode #2)
public sealed record FallenBackRetry(DateTime At) : SupervisionInput(At);    // dialog Retry / countdown -> Launching (the app is already launched; grace + exit arms apply)
/// <summary>A launch attempt actually started a process (ticket 06-C). Stamps the launch clock the
/// startup grace measures, so the grace covers the process's start, not the decision to recover.</summary>
public sealed record LaunchExecuted(DateTime At) : SupervisionInput(At);
/// <summary>A launch attempt threw before starting a process (ticket 06-C). Books a strike instead
/// of waiting forever for a grace that can never fire.</summary>
/// <param name="Unrecoverable">
/// C9 fixup-01: the failure cannot heal on a retry (Win32 8235, a broken signature or trust chain,
/// and Win32 740, a CreateProcess of the uiAccess exe). The state machine escalates to FallenBack
/// on this FIRST failure instead of booking the ordinary retries. The FallenBack dialog's Retry
/// and its one 60 s auto-retry remain the healing path for a transient case.
/// </param>
public sealed record LaunchFailed(DateTime At, bool Unrecoverable = false) : SupervisionInput(At);
/// <summary>supervision.enabled re-read as false (ticket 06-B): stand down to IdleWatch without a
/// kill or a relaunch.</summary>
public sealed record SupervisionDisabled(DateTime At) : SupervisionInput(At);

/// <summary>
/// What a deferred launch attempt did, as the clock host reports it. Three values rather than a
/// bool so every host must answer the unrecoverable case at compile time — a default interface
/// member would let a host silently drop it and stay green (C9 fixup-01).
/// </summary>
public enum LaunchAttemptResult
{
    /// <summary>A process started; the startup grace is armed.</summary>
    Started,

    /// <summary>The launch threw; book a strike and retry with the backoff.</summary>
    Failed,

    /// <summary>The launch hit a failure that cannot heal on retry (Win32 8235/740): escalate to
    /// FallenBack on this first attempt.</summary>
    FailedUnrecoverable,
}
