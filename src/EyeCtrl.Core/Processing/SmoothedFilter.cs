namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Simple exponential smoothing filter.
/// Returns positions in physical screen pixels.
/// </summary>
public class SmoothedFilter : IGazeFilter
{
    private readonly double _factor;
    private PhysicalPoint? _previous;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmoothedFilter"/> class.
    /// </summary>
    /// <param name="smoothingFactor">Smoothing factor between 0 and 1 (clamped). Higher values give more weight to new samples.</param>
    public SmoothedFilter(double smoothingFactor)
    {
        _factor = Math.Clamp(smoothingFactor, 0.0, 1.0);
    }

    /// <inheritdoc />
    public PhysicalPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _previous != null)
            return _previous.Value;

        var raw = new PhysicalPoint(rawGaze.X, rawGaze.Y);
        if (_previous == null)
        {
            _previous = raw;
            return raw;
        }

        var smoothed = new PhysicalPoint(
            _factor * raw.X + (1 - _factor) * _previous.Value.X,
            _factor * raw.Y + (1 - _factor) * _previous.Value.Y);
        _previous = smoothed;
        return smoothed;
    }

    /// <inheritdoc />
    public void Reset() => _previous = null;
}
