namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Models;

/// <summary>
/// Today's <c>FixationDetector</c> frozen as a measurement baseline, exactly as
/// <see cref="LegacyOneEuroFilter"/> froze the pre-retune filter. Ticket 3 rewrites the production
/// detector in place (running centroid, valid-time age), so without this copy "today's rule" would
/// silently become the candidate — the pinned baseline asserts would go red for a reason that is not a
/// regression, and G4/G5, which are defined against today at the same radius, would become
/// uncomputable.
/// <para>
/// The detection logic below is byte-for-byte the shipped <c>FixationDetector</c>: first-sample anchor,
/// wall-clock <c>FixationTimeMs</c>, one sample outside the radius ends the fixation.
/// <see cref="AnchorTimestamp"/> after <see cref="Reset"/> is OBSERVATION ONLY — added for the rule
/// seam, holding no rule of its own. Do not fix anything in here; a change to this class
/// invalidates every recorded baseline number.
/// </para>
/// </summary>
public sealed class LegacyFixationDetector
{
    private double _radiusPixels;
    private long _fixationTimeMicroseconds;
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

    /// <summary>Maximum distance in physical pixels from the fixation center for gaze to still be
    /// considered fixating.</summary>
    public double RadiusPixels
    {
        get => _radiusPixels;
        set
        {
            ValidateRadius(value, nameof(value));
            _radiusPixels = value;
        }
    }

    /// <summary>Minimum time in milliseconds gaze must stay within <see cref="RadiusPixels"/> to
    /// trigger fixation.</summary>
    public int FixationTimeMs
    {
        get => (int)(_fixationTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        set
        {
            ValidateTime(value, nameof(value));
            _fixationTimeMicroseconds = value * MathConstants.MicrosecondsPerMillisecond;
        }
    }

    /// <summary>Initializes a frozen copy of the legacy detector.</summary>
    public LegacyFixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        ValidateRadius(fixationRadiusPixels, nameof(fixationRadiusPixels));
        ValidateTime(fixationTimeMs, nameof(fixationTimeMs));

        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    private static void ValidateRadius(double value, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException("Fixation radius must be a finite number.", paramName);
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Fixation radius must be positive.");
    }

    private static void ValidateTime(int value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Fixation time must be positive.");
    }

    /// <summary>Update with current gaze point in physical screen pixels (shipped logic).</summary>
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

    /// <summary>Returns fixation progress as 0.0-1.0 ratio of elapsed time to required fixation time.</summary>
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

    // ── Observation only (not part of the frozen rule) ────────────────────────

    /// <summary>The current run's anchor timestamp, or 0 before the first sample. Observation only.</summary>
    public long AnchorTimestamp => _fixationCenter is null ? 0 : _fixationStartTime;
}
