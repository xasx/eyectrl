namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>
/// Sent by GazeEngineAdapter at ~30Hz with filtered gaze position.
/// Position is in physical screen pixels (actual hardware resolution).
/// </summary>
public record GazeCursorMovedMessage(PhysicalPoint Position, long TimestampMicroseconds);
