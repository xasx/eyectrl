namespace EyeCtrl.Core.Calibration;

/// <summary>Type of coordinate transform used for calibration.</summary>
public enum TransformType
{
    /// <summary>6-parameter affine transform (x' = a*x + b*y + c, y' = d*x + e*y + f).</summary>
    Affine
}
