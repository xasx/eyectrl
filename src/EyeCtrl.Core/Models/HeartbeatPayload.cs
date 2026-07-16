namespace EyeCtrl.Core.Models;

/// <summary>
/// Liveness payload stamped by the app's UI thread every few seconds and sent
/// over the <c>eyectrl_watch</c> named pipe to the supervisor process.
/// </summary>
/// <param name="Pid">The app's OS process id.</param>
/// <param name="StartedAtUtc">When the app process started (UTC).</param>
/// <param name="IsConnected"><c>true</c> if the gaze tracker is connected and providing data.</param>
/// <param name="IsPaused"><c>true</c> if the interaction manager is in the paused state (no SendInput).</param>
/// <param name="LastFrameUtc">UTC timestamp of the most recent raw gaze sample, valid or invalid.</param>
/// <param name="ShuttingDown"><c>true</c> on the final payload stamped during clean shutdown.</param>
public record HeartbeatPayload(
    int Pid,
    DateTime StartedAtUtc,
    bool IsConnected,
    bool IsPaused,
    DateTime LastFrameUtc,
    bool ShuttingDown);
