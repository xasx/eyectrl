namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class FixationDetector
{
    private readonly double _radiusPixels;
    private readonly long _fixationTimeMicroseconds;
    private ScreenPoint? _fixationCenter;
    private long _fixationStartTime;
    private bool _isFixating;

    public event Action<ScreenPoint>? FixationStarted;
    public event Action? FixationEnded;
    public bool IsFixating => _isFixating;
    public ScreenPoint? FixationPoint => _isFixating ? _fixationCenter : null;

    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * 1000L;
    }

    public void Update(ScreenPoint currentPoint, long timestampMicroseconds)
    {
        if (_fixationCenter == null)
        {
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
            return;
        }

        double distance = Math.Sqrt(
            Math.Pow(currentPoint.X - _fixationCenter.X, 2) +
            Math.Pow(currentPoint.Y - _fixationCenter.Y, 2));

        if (distance <= _radiusPixels)
        {
            if (!_isFixating && (timestampMicroseconds - _fixationStartTime) >= _fixationTimeMicroseconds)
            {
                _isFixating = true;
                FixationStarted?.Invoke(_fixationCenter);
            }
        }
        else
        {
            if (_isFixating)
            {
                _isFixating = false;
                FixationEnded?.Invoke();
            }
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
        }
    }

    public void Reset()
    {
        _fixationCenter = null;
        _isFixating = false;
    }
}
