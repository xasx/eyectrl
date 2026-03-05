namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public sealed class FixationCentroidTracker
{
    private readonly double _velocityThresholdPxPerSec;
    private bool _isTracking;
    private ScreenPoint? _lastPoint;
    private long _lastTimestamp;

    // Welford online mean
    private int _count;
    private double _meanX;
    private double _meanY;

    public FixationCentroidTracker(double velocityThresholdPxPerSec)
    {
        _velocityThresholdPxPerSec = velocityThresholdPxPerSec;
    }

    public ScreenPoint? StableCentroid =>
        _count > 0 ? new ScreenPoint(_meanX, _meanY) : null;

    public void StartTracking()
    {
        _isTracking = true;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
    }

    public void StopTracking()
    {
        _isTracking = false;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
    }

    public void Update(ScreenPoint point, long timestampMicroseconds)
    {
        if (!_isTracking) return;

        if (_lastPoint == null)
        {
            // First sample — no velocity, always include
            Include(point);
            _lastPoint = point;
            _lastTimestamp = timestampMicroseconds;
            return;
        }

        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? Math.Sqrt(
                Math.Pow(point.X - _lastPoint.X, 2) +
                Math.Pow(point.Y - _lastPoint.Y, 2)) / dtSeconds
            : 0;

        if (speed <= _velocityThresholdPxPerSec)
            Include(point);

        _lastPoint = point;
        _lastTimestamp = timestampMicroseconds;
    }

    private void Include(ScreenPoint point)
    {
        _count++;
        _meanX += (point.X - _meanX) / _count;
        _meanY += (point.Y - _meanY) / _count;
    }
}
