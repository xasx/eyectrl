namespace EyeCtrl.Core.Models;

/// <summary>Gaze interaction state machine states representing the current user interaction mode.</summary>
public enum InteractionState
{
    /// <summary>Free gaze movement — no fixation or interaction active.</summary>
    FreeLook,
    /// <summary>User is fixating on a point, possibly triggering dwell-based actions.</summary>
    Fixation,
    /// <summary>The action menu is displayed around the gaze point.</summary>
    ActionMenuVisible,
    /// <summary>Manual (zoom-to-point) selection mode is active.</summary>
    ManualSelectionActive,
    /// <summary>The action menu is displayed during manual selection mode.</summary>
    ManualSelectionActionMenuVisible,
    /// <summary>Gaze tracking is paused (e.g., during calibration or idle timeout).</summary>
    Paused
}
