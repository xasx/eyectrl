namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public readonly record struct CalibrationPoint(
    PhysicalPoint Target,
    PhysicalPoint MeanGaze,
    double StdDevX,
    double StdDevY,
    int SampleCount)
{
    public double EuclideanError() =>
        Math.Sqrt(
            (MeanGaze.X - Target.X) * (MeanGaze.X - Target.X) +
            (MeanGaze.Y - Target.Y) * (MeanGaze.Y - Target.Y));
}
