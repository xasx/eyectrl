namespace EyeCtrl.Core.Calibration;

public enum CalibrationState
{
    Idle,
    Collecting,
    Training,
    Active,
    Failed,
    Cancelled
}
