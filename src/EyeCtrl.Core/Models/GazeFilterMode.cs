namespace EyeCtrl.Core.Models;

/// <summary>Filtering strategy applied to raw gaze samples before cursor positioning.</summary>
public enum GazeFilterMode
{
    /// <summary>No filtering — raw gaze samples are used directly.</summary>
    Direct,
    /// <summary>Exponential moving average (EMA) smoothing — smooths jitter without snapping.</summary>
    Smoothed,
    /// <summary>
    /// Historical alias of <see cref="Smoothed"/>. Filter-level control snapping was never
    /// implemented — "snapping" in EyeCtrl is snap-to-control via Auto Selection
    /// (SelectionMode.Auto + ControlFinder), which is independent of the gaze filter.
    /// Kept because the value is persisted in settings JSON; removing it would break
    /// existing settings files without a migration.
    /// </summary>
    SmoothedWithSnapping,
    /// <summary>1€ filter — adaptive low-pass filter that reduces jitter while preserving responsiveness.</summary>
    OneEuro
}
