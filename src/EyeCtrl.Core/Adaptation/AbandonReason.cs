namespace EyeCtrl.Core.Adaptation;

/// <summary>Why a dwell was abandoned.</summary>
public enum AbandonReason
{
    /// <summary>Not an abandonment (used for activations).</summary>
    None = 0,
    /// <summary>No target was under gaze when accumulation stopped (gaze left the target).</summary>
    GazeLeft,
    /// <summary>Another target became the nearest candidate before this one completed.</summary>
    NearestChanged,
}
