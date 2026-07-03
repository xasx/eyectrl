namespace EyeCtrl.Core.Models;

/// <summary>
/// A single gaze sample from the eye tracker. Coordinates are in the tracker's native coordinate space
/// and must be mapped to screen coordinates via calibration transforms before use.
/// </summary>
/// <param name="X">Horizontal position in the tracker's native coordinate space.</param>
/// <param name="Y">Vertical position in the tracker's native coordinate space.</param>
/// <param name="TimestampMicroseconds">Timestamp of the gaze sample in microseconds since an arbitrary epoch.</param>
/// <param name="IsValid"><c>true</c> if the tracker reports this sample as valid (eyes detected); <c>false</c> if eyes were lost.</param>
public readonly record struct GazePoint(double X, double Y, long TimestampMicroseconds, bool IsValid);
