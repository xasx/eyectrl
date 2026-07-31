namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent when the user asks for the app to restart (Settings → General → Restart Now).
///
/// The sender does not decide whether a restart is possible — AppRestartCoordinator
/// does, because refusing is the safety-critical half. A settings surface must never
/// call Shutdown directly.
/// </summary>
public record RestartAppRequestedMessage;

/// <summary>
/// Sent when a requested restart was refused, with the reason to show the user.
///
/// A refusal must be visible. The user has just completed a dwell and is entitled to
/// know the app is deliberately still here; silence would read as the dwell not
/// registering, and they would try again.
/// </summary>
/// <param name="Reason">User-facing explanation.</param>
public record RestartUnavailableMessage(string Reason);
