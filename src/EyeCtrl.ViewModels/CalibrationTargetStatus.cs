namespace EyeCtrl.ViewModels;

/// <summary>
/// What a calibration target is doing right now, for an overlay that shows the whole set at once.
/// </summary>
/// <remarks>
/// The overlay used to render a single dot that teleported on every advance, so the user had to search
/// the screen each time — the exact burden calibration exists to avoid for someone with poor gaze
/// control. Showing every target needs a per-target state, and it has to be unambiguous: exactly one
/// target is <see cref="Current"/>, and only that one accepts gaze.
/// </remarks>
public enum CalibrationTargetStatus
{
    /// <summary>Not reached yet. Visible so the next location is known before the current one finishes.</summary>
    Upcoming,

    /// <summary>The one being collected. The only one that progresses, and the only one with the band ring.</summary>
    Current,

    /// <summary>Already collected. Shown in a settled state so progress through the set is visible.</summary>
    Done,
}
