namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Detects when gaze remains stable within a radius for a duration.
/// All coordinates are in physical screen pixels.
/// </summary>
public class FixationDetector
{
    private readonly double _radiusPixels;
    private readonly long _fixationTimeMicroseconds;
    private PhysicalPoint? _fixationCenter;
    private long _fixationStartTime;
    private long _lastTimestamp;
    private bool _isFixating;

    /// <summary>Fired when fixation starts. Point is in physical screen pixels.</summary>
    public event Action<PhysicalPoint>? FixationStarted;
    public event Action? FixationEnded;
    public bool IsFixating => _isFixating;
    /// <summary>Current fixation point in physical screen pixels, if fixating.</summary>
    public PhysicalPoint? FixationPoint => _isFixating ? _fixationCenter : null;

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

    /// <summary>
    /// Update with current gaze point in physical screen pixels.
    /// </summary>
    public void Update(PhysicalPoint currentPoint, long timestampMicroseconds)
    {
        _lastTimestamp = timestampMicroseconds;

        if (_fixationCenter == null)
        {
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
            return;
        }

        double distance = Math.Sqrt(
            Math.Pow(currentPoint.X - _fixationCenter.Value.X, 2) +
            Math.Pow(currentPoint.Y - _fixationCenter.Value.Y, 2));

        if (distance <= _radiusPixels)
        {
            if (!_isFixating && (timestampMicroseconds - _fixationStartTime) >= _fixationTimeMicroseconds)
            {
                _isFixating = true;
                FixationStarted?.Invoke(_fixationCenter.Value);
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
