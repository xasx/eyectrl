namespace EyeCtrl.Core.Models;

/// <summary>
/// One eye-position / distance / presence sample from the tracker, for the Track Status box.
/// Separate from <see cref="GazePoint"/> so the hot gaze path is not widened.
/// </summary>
/// <param name="Left">Left eye position normalized in the track box (0..1 XYZ) + validity.</param>
/// <param name="Right">Right eye position normalized in the track box (0..1 XYZ) + validity.</param>
/// <param name="HeadDistanceMm">Head distance from the tracker in millimeters (from head_pose position z).</param>
/// <param name="HeadDistanceValid">Whether <paramref name="HeadDistanceMm"/> is valid.</param>
/// <param name="UserPresent">Whether the tracker reports a user present.</param>
/// <param name="TimestampMicroseconds">Tracker timestamp in microseconds.</param>
public readonly record struct TrackStatusSample(
    EyeSample Left,
    EyeSample Right,
    double HeadDistanceMm,
    bool HeadDistanceValid,
    bool UserPresent,
    long TimestampMicroseconds);

/// <summary>
/// One eye's position normalized in the track box (0..1 on each axis) + validity.
/// </summary>
/// <param name="X">Normalized horizontal position (0=left edge, 1=right edge of track box).</param>
/// <param name="Y">Normalized vertical position (0=top, 1=bottom).</param>
/// <param name="Z">Normalized depth (0=too close, 1=too far; ~0.5 is optimal).</param>
/// <param name="IsValid">True only when ALL three coordinates are valid (Tobii reports per-coordinate validity).</param>
public readonly record struct EyeSample(double X, double Y, double Z, bool IsValid);
