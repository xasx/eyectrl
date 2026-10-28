namespace EyeCtrl.ViewModels;

/// <summary>Which label the paused/status strip shows (B2a/B2b).</summary>
public enum ResumeStripLabel
{
    /// <summary>No label: the strip is not shown, or shows only its bar.</summary>
    None,

    /// <summary>"Paused", with the Resume disc.</summary>
    Paused,

    /// <summary>Tracking lost (B1: no valid gaze for 3 s). No dwell target.</summary>
    TrackingLost,

    /// <summary>No eye tracker: the mouse is the provider. No dwell target.</summary>
    NoTracker,
}
