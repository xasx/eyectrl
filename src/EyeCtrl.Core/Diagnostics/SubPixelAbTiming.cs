namespace EyeCtrl.Core.Diagnostics;

/// <summary>
/// The sub-pixel visibility test's trial timing (test-design amendment A1, 2026-10-13: scripted
/// intervals 5 s, live intervals 6 s, gap unchanged): how long each interval of a presentation
/// runs, and how long the blank gap between the two intervals lasts.
///
/// <para>
/// Core owns these values because the schedule needs them: the drift and live motions travel for
/// the whole interval (<c>0.25 px per 30 ms</c> and <c>20 px/s</c>), so
/// <see cref="SubPixelAbSchedule"/> insets every start position by the block's total travel to
/// keep the whole path inside the middle half of the screen. The state machine in
/// <c>EyeCtrl.ViewModels.SubPixelProbeViewModel</c> consumes the same values for its phase timers —
/// it re-exposes them as its own constants rather than duplicating the numbers.
/// </para>
/// </summary>
public static class SubPixelAbTiming
{
    /// <summary>
    /// Interval 1 and interval 2 duration of the scripted blocks (drift and static), in
    /// milliseconds. 5000, not the original 3000: the owner's legibility look found the rings
    /// shown too briefly ("Rings shown too briefly", A1).
    /// </summary>
    public const int ScriptedIntervalMs = 5000;

    /// <summary>
    /// Interval 1 and interval 2 duration of the live block, in milliseconds. 6000, scaled with
    /// the scripted interval at the unchanged 20 px/s target speed.
    /// </summary>
    public const int LiveIntervalMs = 6000;

    /// <summary>The blank gap between the two intervals, in milliseconds; unchanged by A1.</summary>
    public const int GapMs = 800;
}
