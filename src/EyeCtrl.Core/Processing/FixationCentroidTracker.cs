namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// Tracks the centroid of stable gaze points during fixation.
/// All coordinates are in physical screen pixels.
/// </summary>
public sealed class FixationCentroidTracker
{
    private readonly double _velocityThresholdPxPerSec;
    private bool _isTracking;
    private PhysicalPoint? _lastPoint;
    private long _lastTimestamp;

    // Welford online mean (centroid)
    private int _count;
    private double _meanX;
    private double _meanY;

    // Welford online variance of per-sample gaze speed (jitter / shakiness proxy)
    private int _speedCount;
    private double _speedMean;
    private double _speedM2;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixationCentroidTracker"/> class.
    /// </summary>
    /// <param name="velocityThresholdPxPerSec">Maximum velocity in pixels per second below which samples are included in the centroid calculation.</param>
    public FixationCentroidTracker(double velocityThresholdPxPerSec)
    {
        _velocityThresholdPxPerSec = velocityThresholdPxPerSec;
    }

    /// <summary>
    /// Current stable centroid in physical screen pixels, if available.
    /// </summary>
    public PhysicalPoint? StableCentroid =>
        _count > 0 ? new PhysicalPoint(_meanX, _meanY) : null;

    /// <summary>
    /// Standard deviation (px/s) of per-sample gaze speed accumulated during the current fixation —
    /// a shakiness/jitter proxy. Zero until at least two speeds have been observed; reset on
    /// <see cref="StartTracking"/>/<see cref="StopTracking"/>.
    /// </summary>
    public double SpeedStdDev =>
        _speedCount > 1 ? Math.Sqrt(_speedM2 / (_speedCount - 1)) : 0.0;

    /// <summary>
    /// Gaze-speed standard deviation of the most recently *completed* fixation, latched at
    /// <see cref="StopTracking"/> (fixation end) before the live accumulator resets. Persists
    /// between fixations, so a windowed reader sees the last completed fixation's jitter rather
    /// than a mid-fixation fragment or a between-fixations zero (design §6.2 — "emitted at fixation end").
    /// </summary>
    public double LastCompletedSpeedStdDev { get; private set; }

    /// <summary>Starts tracking the centroid from the current position, resetting accumulated data.</summary>
    public void StartTracking()
    {
        _isTracking = true;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
        _speedCount = 0;
        _speedMean = 0;
        _speedM2 = 0;
    }

    /// <summary>Stops tracking and resets accumulated centroid data.</summary>
    public void StopTracking()
    {
        _isTracking = false;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
        LastCompletedSpeedStdDev = SpeedStdDev;
        _speedCount = 0;
        _speedMean = 0;
        _speedM2 = 0;
    }

    /// <summary>
    /// Update with current gaze point in physical screen pixels.
    /// </summary>
    public void Update(PhysicalPoint point, long timestampMicroseconds)
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
            ? point.DistanceTo(_lastPoint.Value) / dtSeconds
            : 0;

        // Welford accumulation of per-sample speed for the jitter statistic.
        // Skip degenerate dt (duplicate / out-of-order timestamps → phantom zero speed) so it
        // doesn't deflate the variance.
        if (dtSeconds > 0)
        {
            _speedCount++;
            double speedDelta = speed - _speedMean;
            _speedMean += speedDelta / _speedCount;
            _speedM2 += speedDelta * (speed - _speedMean);
        }

        if (speed <= _velocityThresholdPxPerSec)
            Include(point);

        _lastPoint = point;
        _lastTimestamp = timestampMicroseconds;
    }

    private void Include(PhysicalPoint point)
    {
        _count++;
        _meanX += (point.X - _meanX) / _count;
        _meanY += (point.Y - _meanY) / _count;
    }
}
