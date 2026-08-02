namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent when the user asks for the app to restart (Settings → General → Restart Now).
///
/// The sender does not decide whether a restart is possible — AppRestartCoordinator
/// does, because refusing is the safety-critical half. A settings surface must never
/// call Shutdown directly.
/// </summary>
/// <param name="StartSupervisor">
/// True when the user chose to start the supervisor first. Only ever set by a surface
/// that actually asked — the coordinator does not prompt.
/// </param>
public record RestartAppRequestedMessage(bool StartSupervisor = false);

/// <summary>
/// Sent when a restart attempt has been accepted and is under way.
///
/// The wait is not short: the self-restart handshake allows the replacement up to 30s to
/// announce itself, and the whole point of running it off the dispatcher is that the app
/// stays responsive meanwhile. Responsive but silent is its own failure — half a minute
/// with no acknowledgement is indistinguishable from the hang this feature exists to
/// avoid, and a second dwell in the meantime is dropped by the in-flight guard, so the
/// user gets no answer from trying again either.
///
/// Sent exactly once per accepted attempt: a request dropped by the in-flight guard
/// raises nothing, because the notice for the attempt already running is still up.
/// </summary>
public record RestartInProgressMessage;

/// <summary>
/// Sent when a requested restart was refused, with the reason to show the user.
///
/// A refusal must be visible. The user has just completed a dwell and is entitled to
/// know the app is deliberately still here; silence would read as the dwell not
/// registering, and they would try again.
/// </summary>
/// <param name="Reason">User-facing explanation.</param>
public record RestartUnavailableMessage(string Reason);
