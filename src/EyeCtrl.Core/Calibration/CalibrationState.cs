namespace EyeCtrl.Core.Calibration;

/// <summary>States of the calibration session lifecycle.</summary>
public enum CalibrationState
{
    /// <summary>No calibration session is active.</summary>
    Idle,
    /// <summary>Gaze samples are being collected at the current target.</summary>
    Collecting,
    /// <summary>The transform is being computed from collected data.</summary>
    Training,
    /// <summary>Calibration is complete and the transform is applied.</summary>
    Active,
    /// <summary>Calibration failed due to poor data or degenerate fit.</summary>
    Failed,
    /// <summary>Calibration was cancelled by the user.</summary>
    Cancelled
}
