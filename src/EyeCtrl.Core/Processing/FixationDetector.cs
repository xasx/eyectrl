namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class FixationDetector
{
    private readonly double _radiusPixels;
    private readonly long _fixationTimeMicroseconds;
    private ScreenPoint? _fixationCenter;
    private long _fixationStartTime;
    private long _lastTimestamp;
    private bool _isFixating;

    public event Action<ScreenPoint>? FixationStarted;
    public event Action? FixationEnded;
    public bool IsFixating => _isFixating;
    public ScreenPoint? FixationPoint => _isFixating ? _fixationCenter : null;

    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        if (!double.IsFinite(fixationRadiusPixels))
            throw new ArgumentException("Fixation radius must be a finite number.", nameof(fixationRadiusPixels));
        if (fixationRadiusPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixationRadiusPixels), fixationRadiusPixels, "Fixation radius must be positive.");
        if (fixationTimeMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixationTimeMs), fixationTimeMs, "Fixation time must be positive.");

        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    public void Update(ScreenPoint currentPoint, long timestampMicroseconds)
    {
        _lastTimestamp = timestampMicroseconds;

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

    /// <summary>
    /// Returns fixation progress as 0.0–1.0 ratio of elapsed time to required fixation time.
    /// Returns 0.0 if no fixation is being tracked.
    /// </summary>
    public double GetProgress()
    {
        if (_fixationCenter == null || _fixationTimeMicroseconds <= 0)
            return 0.0;

        var elapsed = _lastTimestamp - _fixationStartTime;
        if (elapsed <= 0) return 0.0;

        return Math.Min(1.0, (double)elapsed / _fixationTimeMicroseconds);
    }

    public void Reset()
    {
        _fixationCenter = null;
        _isFixating = false;
    }
}
