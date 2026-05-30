namespace EyeCtrl.Core.Calibration;

using System.Text.Json;
using EyeCtrl.Core.Models;

public class CalibrationService
{
    private readonly object _transformLock = new();
    private volatile ICalibrationTransform? _currentTransform;

    private List<PhysicalPoint>? _targetPositions;
    private CalibrationPointCollector? _currentCollector;
    private readonly List<CalibrationPoint> _collectedPoints = new();
    private int _currentTargetIndex;

    public ICalibrationTransform? CurrentTransform => _currentTransform;
    public bool IsSessionActive => _targetPositions != null;

    public int StartSession(int pointCount, PhysicalPoint screenSize)
    {
        if (_targetPositions != null)
            throw new InvalidOperationException("Session already in progress.");

        _targetPositions = CalibrationTargetPositionHelper.GetPositions(pointCount, screenSize).ToList();
        _collectedPoints.Clear();
        _currentTargetIndex = 0;
        return _targetPositions.Count;
    }

    public PhysicalPoint GetTargetPosition(int index)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");
        return _targetPositions[index];
    }

    public void BeginTarget(int index, PhysicalPoint screenSize)
    {
        _currentTargetIndex = index;
        var target = GetTargetPosition(index);
        _currentCollector = new CalibrationPointCollector(target, settleWindowMs: 0);
    }

    public void AddRawSample(PhysicalPoint gaze)
    {
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

        lock (_transformLock) { _currentTransform = transform; }

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

            var transform = new AffineTransform();
            transform.SetParameters(data.TransformParams);
            lock (_transformLock) { _currentTransform = transform; }
        }
        catch
        {
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
