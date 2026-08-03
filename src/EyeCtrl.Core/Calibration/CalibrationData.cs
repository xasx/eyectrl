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
    /// <summary>
    /// GDI display name of the monitor the calibration was performed on (e.g. <c>\\.\DISPLAY1</c>).
    /// Windows renumbers these whenever monitors are attached, detached, or re-enumerated (dock
    /// changes, driver updates, reboots), so this is NOT a durable identity — prefer
    /// <see cref="MonitorHardwareId"/> and keep this for diagnostics and legacy records.
    /// </summary>
    public string MonitorDeviceName { get; init; } = string.Empty;

    /// <summary>
    /// Stable hardware identity of the monitor the calibration was performed on: the EDID PnP ID
    /// (three-letter manufacturer code plus four-hex-digit product code, e.g. <c>DELA1CF</c>).
    /// Unlike <see cref="MonitorDeviceName"/> this survives display renumbering, so it is the
    /// primary key <see cref="CalibrationService.Load"/> matches on.
    /// <para>
    /// Empty when the platform could not determine it, and on records written before this field
    /// existed; <see cref="CalibrationService.Load"/> then falls back to matching
    /// <see cref="MonitorDeviceName"/>. Identifies a monitor <em>model</em>, not an individual
    /// unit — EDID carries a serial number but DisplayConfig does not surface it, so two identical
    /// monitors share an ID.
    /// </para>
    /// </summary>
    public string MonitorHardwareId { get; init; } = string.Empty;

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
