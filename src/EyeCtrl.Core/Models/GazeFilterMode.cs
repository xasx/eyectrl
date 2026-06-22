namespace EyeCtrl.Core.Models;

/// <summary>Filtering strategy applied to raw gaze samples before cursor positioning.</summary>
public enum GazeFilterMode
{
    /// <summary>No filtering — raw gaze samples are used directly.</summary>
    Direct,
    /// <summary>Exponential moving average (EMA) smoothing — smooths jitter without snapping.</summary>
    Smoothed,
    /// <summary>EMA smoothing with attraction toward the nearest interactive control.</summary>
    SmoothedWithSnapping,
    /// <summary>1€ filter — adaptive low-pass filter that reduces jitter while preserving responsiveness.</summary>
    OneEuro
}
