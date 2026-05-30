namespace EyeCtrl.Core.Calibration;

public sealed class CalibrationData
{
    public TransformType Type { get; init; }
    public double[] TransformParams { get; init; } = Array.Empty<double>();
    public CalibrationPoint[] Points { get; init; } = Array.Empty<CalibrationPoint>();
    public DateTimeOffset CalibratedAt { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public string MonitorDeviceName { get; init; } = string.Empty;

    public double ComputeMeanErrorPixels()
    {
        if (Points.Length == 0) return double.PositiveInfinity;
        return Points.Average(p => p.EuclideanError());
    }

    public double ComputeQualityPercent()
    {
        var diagonal = Math.Sqrt(ScreenWidth * ScreenWidth + ScreenHeight * ScreenHeight);
        if (diagonal <= 0) return 100;
        return (ComputeMeanErrorPixels() / diagonal) * 100;
    }
}
