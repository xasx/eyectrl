namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public interface ICalibrationTransform
{
    PhysicalPoint Apply(PhysicalPoint rawGaze);
    double[] GetParameters();
    void SetParameters(double[] parameters);
}
