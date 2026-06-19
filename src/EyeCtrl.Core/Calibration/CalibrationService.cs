namespace EyeCtrl.Core.Calibration;

using System.Text.Json;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

public class CalibrationService
{
    private readonly object _transformLock = new();
    private volatile ICalibrationTransform? _currentTransform;
    private readonly ILogger<CalibrationService>? _logger;

    private List<PhysicalPoint>? _targetPositions;
    private CalibrationPointCollector? _currentCollector;
    private readonly List<CalibrationPoint> _collectedPoints = new();
    private int _currentTargetIndex;
    private PhysicalPoint _screenSize;

    public CalibrationService(ILogger<CalibrationService>? logger = null)
    {
        _logger = logger;
    }

    public ICalibrationTransform? CurrentTransform => _currentTransform;
    public bool IsSessionActive => _targetPositions != null;

    /// <summary>
    /// Metadata for the currently active calibration (from the last completed session
    /// or a successful load). Null when no calibration is applied.
    /// </summary>
    public CalibrationData? CurrentData { get; private set; }

    public int StartSession(int pointCount, PhysicalPoint screenSize)
    {
        if (_targetPositions != null)
            throw new InvalidOperationException("Session already in progress.");

        _targetPositions = CalibrationTargetPositionHelper.GetPositions(pointCount, screenSize).ToList();
        _collectedPoints.Clear();
        _currentTargetIndex = 0;
        _screenSize = screenSize;
        return _targetPositions.Count;
    }

    public PhysicalPoint GetTargetPosition(int index)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");
        return _targetPositions[index];
    }

    public void BeginTarget(int index, int settleWindowMs = 0, int maxSamples = int.MaxValue)
    {
        if (_currentCollector != null)
            throw new InvalidOperationException("A target is already being collected. Call EndTarget() first.");

        _currentTargetIndex = index;
        var target = GetTargetPosition(index);
        _currentCollector = new CalibrationPointCollector(target, settleWindowMs, maxSamples);
    }

    public void AddRawSample(PhysicalPoint gaze)
    {
        // Drop implausible samples (non-finite / far off-screen) so a misbehaving sensor cannot
        // pollute a target's mean during capture.
        if (!CalibrationValidator.IsPlausibleSample(gaze, _screenSize.X, _screenSize.Y))
            return;
        _currentCollector?.AddRawSample(gaze);
    }

    public bool IsCollecting => _currentCollector?.IsCollecting ?? false;

    public CalibrationPoint EndTarget()
    {
        if (_currentCollector == null)
            throw new InvalidOperationException("No target being collected.");
        var point = _currentCollector.End();
        _collectedPoints.Add(point);
        _currentCollector = null;
        return point;
    }

    public CalibrationData CompleteSession(PhysicalPoint screenSize, string monitorDeviceName)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");

        var points = _collectedPoints.ToArray();

        // Reject capture data where the sensor was not tracking the user's gaze — the gaze barely
        // moved across targets that span the screen. Caught before fitting for a clear message.
        if (!CalibrationValidator.HasSufficientGazeVariation(points))
            throw new InvalidOperationException(
                "Gaze barely changed across calibration targets — the eye tracker may not be tracking. Please recalibrate.");

        var transform = AffineTransform.Fit(points);

        var data = new CalibrationData
        {
            Type = TransformType.Affine,
            TransformParams = transform.GetParameters(),
            Points = points,
            CalibratedAt = DateTimeOffset.UtcNow,
            ScreenWidth = (int)screenSize.X,
            ScreenHeight = (int)screenSize.Y,
            MonitorDeviceName = monitorDeviceName,
        };

        // Reject degenerate fits before they are applied or persisted. A calibration fit to
        // near-constant or contradictory gaze samples can yield a finite (so AffineTransform.Fit's
        // non-finite/singular guards don't catch it) but unusable transform that maps every gaze
        // point far off-screen — silently breaking all gaze interaction. The caller surfaces this
        // as a failed calibration and never persists it.
        if (!CalibrationValidator.IsTransformSane(transform, screenSize.X, screenSize.Y))
            throw new InvalidOperationException(
                "Calibration produced an unusable transform — gaze would map off-screen. Please recalibrate.");

        double diagonal = Math.Sqrt(screenSize.X * screenSize.X + screenSize.Y * screenSize.Y);
        double maxResidual = CalibrationValidator.MaxResidualPixels(transform, points);
        if (maxResidual > diagonal * CalibrationValidator.MaxResidualScreenFraction)
            throw new InvalidOperationException(
                $"Calibration accuracy is too poor ({maxResidual:F0}px off target). Please recalibrate.");

        lock (_transformLock) { _currentTransform = transform; }
        CurrentData = data;

        _targetPositions = null;
        _collectedPoints.Clear();
        return data;
    }

    public void CancelSession()
    {
        _targetPositions = null;
        _collectedPoints.Clear();
        _currentCollector = null;
    }

    public void Clear()
    {
        lock (_transformLock) { _currentTransform = null; }
        CurrentData = null;
    }

    public void Load(string filePath, PhysicalPoint currentScreenSize, string currentMonitorDeviceName)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<CalibrationData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            if (data == null) return;

            if (data.ScreenWidth != (int)currentScreenSize.X ||
                data.ScreenHeight != (int)currentScreenSize.Y ||
                !string.Equals(data.MonitorDeviceName, currentMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (data.TransformParams is not { Length: 6 })
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} has invalid transform parameters (expected 6); ignoring",
                    filePath);
                return;
            }

            var transform = new AffineTransform();
            transform.SetParameters(data.TransformParams);

            // Defense in depth: never trust a persisted transform that maps gaze far off-screen
            // (e.g. a degenerate calibration written by an older build). Ignoring it leaves the
            // pipeline uncalibrated (raw gaze) rather than poisoning every gaze point.
            if (!CalibrationValidator.IsTransformSane(transform, currentScreenSize.X, currentScreenSize.Y))
            {
                _logger?.LogWarning(
                    "Calibration data from {FilePath} maps gaze off-screen (degenerate transform); ignoring",
                    filePath);
                return;
            }

            lock (_transformLock) { _currentTransform = transform; }
            CurrentData = data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Failed to load calibration data from {FilePath}; ignoring", filePath);
        }
    }

    public void Save(string filePath, CalibrationData data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, json);
    }
}
