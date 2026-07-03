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
    /// <summary>Fired when the current fixation ends (gaze moves outside the radius).</summary>
    public event Action? FixationEnded;
    /// <summary>Whether the detector currently considers gaze to be fixating.</summary>
    public bool IsFixating => _isFixating;
    /// <summary>Current fixation point in physical screen pixels, if fixating.</summary>
    public PhysicalPoint? FixationPoint => _isFixating ? _fixationCenter : null;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixationDetector"/> class.
    /// </summary>
    /// <param name="fixationRadiusPixels">Maximum distance in physical pixels from the fixation center for gaze to still be considered fixating.</param>
    /// <param name="fixationTimeMs">Minimum time in milliseconds gaze must stay within the radius to trigger fixation.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fixationRadiusPixels"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when parameters are non-positive.</exception>
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

        double distance = currentPoint.DistanceTo(_fixationCenter.Value);

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

    /// <summary>Resets the detector to its initial state (no fixation active).</summary>
    public void Reset()
    {
        _fixationCenter = null;
        _isFixating = false;
    }
}
