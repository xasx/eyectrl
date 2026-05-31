namespace EyeCtrl.Core.Models;

/// <summary>Selection mode for gaze-based click targeting.</summary>
public enum SelectionMode
{
    /// <summary>Raw pixel coordinates.</summary>
    None,
    /// <summary>Zoom-and-dwell via ManualSelection overlay.</summary>
    ManualSelection,
    /// <summary>Snap-to-control via UIA/Win32.</summary>
    AutoSelection
}
