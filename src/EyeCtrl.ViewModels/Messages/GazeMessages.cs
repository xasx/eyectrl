namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>Sent by GazeEngineAdapter at ~30Hz with filtered gaze position.</summary>
public record GazeCursorMovedMessage(ScreenPoint Position, long TimestampMicroseconds);
