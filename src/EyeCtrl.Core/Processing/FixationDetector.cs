namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Detects when gaze remains stable within a radius for a duration.
/// All coordinates are in physical screen pixels.
/// </summary>
public class FixationDetector
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

    /// <summary>
    /// Maximum distance in physical pixels from the fixation center for gaze to still be considered
    /// fixating. Settable at runtime so a tolerance change — a manual <c>DwellSettings</c> edit or an
    /// adaptation delta — applies to the LIVE detector, in place. Rebuilding the detector instead would
    /// drop the in-progress fixation, and the adaptation controller steps tolerance on every tick while
    /// relaxing or recovering, so a rebuild-per-change starves fixation entirely whenever the tick
    /// interval is shorter than <see cref="FixationTimeMs"/> (design §5, §9.1 "never per tick").
    /// <para>
    /// State-preserving, mirroring <see cref="DwellDetector.UpdateTargetGeometry"/>: enlarging keeps an
    /// in-progress fixation (gaze already inside the old radius is inside the new one), and shrinking
    /// such that gaze now falls outside ends it on the next <see cref="Update"/> as an ordinary
    /// gaze-left — a real transition rather than a synthetic teardown. Assigning the current value is a
    /// no-op, so a controller may write it every tick.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public double RadiusPixels
    {
        get => _radiusPixels;
        set
        {
            ValidateRadius(value, nameof(value));
            _radiusPixels = value;
        }
    }

    /// <summary>
    /// Minimum time in milliseconds gaze must stay within <see cref="RadiusPixels"/> to trigger fixation.
    /// Settable at runtime for the same reason as <see cref="RadiusPixels"/>, and likewise
    /// state-preserving: shortening it below the already-elapsed hold lets the next <see cref="Update"/>
    /// fire <see cref="FixationStarted"/>, and lengthening it leaves an established fixation intact
    /// (only a gaze-left ends one). <see cref="GetProgress"/> re-scales against the new value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public int FixationTimeMs
    {
        get => (int)(_fixationTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        set
        {
            ValidateTime(value, nameof(value));
            _fixationTimeMicroseconds = value * MathConstants.MicrosecondsPerMillisecond;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FixationDetector"/> class.
    /// </summary>
    /// <param name="fixationRadiusPixels">Maximum distance in physical pixels from the fixation center for gaze to still be considered fixating.</param>
    /// <param name="fixationTimeMs">Minimum time in milliseconds gaze must stay within the radius to trigger fixation.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fixationRadiusPixels"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when parameters are non-positive.</exception>
    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        // Validated through the shared helpers rather than the property setters so the thrown
        // ArgumentException carries the CONSTRUCTOR's parameter name (callers — and
        // FixationDetectorValidationTests — assert on ParamName), not the setter's "value".
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
