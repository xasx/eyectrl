namespace EyeCtrl.Core.Enums;

/// <summary>
/// Flags describing which optional data streams a gaze provider can supply.
/// Used by the Track Status box to decide what to show and whether to appear at all.
/// </summary>
[Flags]
public enum GazeProviderCapabilities
{
    /// <summary>No optional streams are available (e.g. mouse simulation provider).</summary>
    None = 0,
    /// <summary>Normalized eye position within the track box (per-eye XYZ + validity).</summary>
    EyePosition = 1,
    /// <summary>Head pose (position + rotation), used for head-distance-from-screen.</summary>
    HeadPose = 2,
    /// <summary>User presence detection (whether a user is in front of the tracker).</summary>
    UserPresence = 4
}
