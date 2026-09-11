namespace EyeCtrl.Core.Models;

/// <summary>
/// One processed gaze sample as published to the cursor and dwell surfaces: the filtered position,
/// its timestamp, and whether that position is HELD rather than freshly measured.
/// <para>
/// A struct, and deliberately small: it is published per sample on the gaze thread, and
/// <c>ExternalDwellService</c> receives it inside its provider lock, so carrying it must not allocate.
/// Further per-sample flags belong here rather than in a parallel channel — add them as trailing
/// parameters (a defaulted one keeps every existing construction site compiling).
/// </para>
/// </summary>
/// <param name="Position">Filtered gaze position in physical screen pixels.</param>
/// <param name="TimestampMicroseconds">Sample timestamp in microseconds (tracker clock).</param>
/// <param name="IsInGap">
/// <c>true</c> when <paramref name="Position"/> is the frozen last valid position rather than a
/// measurement — the eyes are closed or the tracker lost them. It carries no evidence about where the
/// user is looking, so dwell surfaces must skip their <c>DwellDetector.Update</c> for such a sample
/// rather than feed it the held point; display surfaces (ring, indicators) may keep showing it.
/// </param>
public readonly record struct GazeFrame(
    PhysicalPoint Position,
    long TimestampMicroseconds,
    bool IsInGap);
