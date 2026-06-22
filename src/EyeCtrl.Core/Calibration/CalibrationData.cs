namespace EyeCtrl.Core.Calibration;

/// <summary>Persistent calibration result containing the transform parameters and metadata.</summary>
public sealed class CalibrationData
{
    /// <summary>Type of transform used for this calibration (currently always Affine).</summary>
    public TransformType Type { get; init; }
    /// <summary>Array of 6 double parameters for the affine transform (a, b, c, d, e, f).</summary>
    public double[] TransformParams { get; init; } = Array.Empty<double>();
    /// <summary>The calibration points used to fit this transform.</summary>
    public CalibrationPoint[] Points { get; init; } = Array.Empty<CalibrationPoint>();
    /// <summary>Timestamp when the calibration was completed.</summary>
    public DateTimeOffset CalibratedAt { get; init; }
    /// <summary>Screen width in physical pixels at calibration time.</summary>
    public int ScreenWidth { get; init; }
    /// <summary>Screen height in physical pixels at calibration time.</summary>
    public int ScreenHeight { get; init; }
    /// <summary>Name of the monitor the calibration was performed on.</summary>
    public string MonitorDeviceName { get; init; } = string.Empty;

    /// <summary>Computes the mean Euclidean error in pixels across all calibration points.</summary>
    /// <returns>Mean error in physical pixels, or positive infinity if no points exist.</returns>
    public double ComputeMeanErrorPixels()
    {
        if (Points.Length == 0) return double.PositiveInfinity;
        return Points.Average(p => p.EuclideanError());
    }

    /// <summary>Computes calibration quality as the ratio of mean error to screen diagonal, as a percentage.</summary>
    /// <returns>Quality percentage (lower is better). Returns 100 if the diagonal is zero.</returns>
    public double ComputeQualityPercent()
    {
        var diagonal = Math.Sqrt(ScreenWidth * ScreenWidth + ScreenHeight * ScreenHeight);
        if (diagonal <= 0) return 100;
        return (ComputeMeanErrorPixels() / diagonal) * 100;
    }
}
