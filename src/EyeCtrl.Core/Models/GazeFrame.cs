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
/// <param name="ActivationProgress">
/// The fixation rule's dwell progress after onset, 0.0–1.0 (ticket 3). Published on the sample so the
/// activation feedback consumes Core's valid-time ledger without recomputing age: 0 before onset and on
/// gap-held samples, 1.0 on the activation sample. The trailing default keeps every existing
/// construction site compiling and every sample without a fixation reading 0.
/// </param>
public readonly record struct GazeFrame(
    PhysicalPoint Position,
    long TimestampMicroseconds,
    bool IsInGap,
    double ActivationProgress = 0.0);
