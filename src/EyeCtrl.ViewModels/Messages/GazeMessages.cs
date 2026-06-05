namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>
/// Sent by GazeEngineAdapter at ~30Hz with filtered gaze position.
/// Position is in physical screen pixels (actual hardware resolution).
/// </summary>
public record GazeCursorMovedMessage(PhysicalPoint Position, long TimestampMicroseconds);

/// <summary>
/// Sent by ExternalDwellService when gaze enters (<c>Active = true</c>) or leaves
/// (<c>false</c>) a registered external (cooperating-app) control. While active, the gaze
/// cursor ring is hidden so EyeCtrl's own dwell-progress feedback shows over the control instead.
/// </summary>
public record ExternalDwellActiveMessage(bool Active);
