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
/// activation feedback consumes Core's valid-time ledger without recomputing age: 0 before onset, 1.0 on
/// the activation sample. A gap-held sample carries the run's last value (the detector is not fed during
/// a gap, so progress holds rather than resets); consumers that skip gap frames — the activation VM does
/// — never read it. The trailing default keeps every existing construction site compiling and every
/// sample without a fixation reading 0.
/// <para>
/// Since ticket 8 the age signal is replay-only: nothing in normal mode activates from it. It stays
/// on the frame for the replay harness, which compares the rule against its runs.
/// </para>
/// </param>
/// <param name="ButtonDwellProgress">
/// The activation offer's button-dwell progress, 0.0–1.0; 0 when no offer is up (ticket 8). This is
/// what the drawn halo follows now — the feedback is on the one action that fires, the dwell at the
/// button, not on the fixation age. 1.0 while a fired offer waits for its accept or decline. A
/// gap-held sample carries the engine's last value like <paramref name="ActivationProgress"/> does;
/// the VM skips those frames. Trailing and defaulted so every existing construction site compiles.
/// </param>
public readonly record struct GazeFrame(
    PhysicalPoint Position,
    long TimestampMicroseconds,
    bool IsInGap,
    double ActivationProgress = 0.0,
    double ButtonDwellProgress = 0.0);
