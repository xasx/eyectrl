namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class SmoothedFilter : IGazeFilter
{
    private readonly double _factor;
    private ScreenPoint? _previous;

    public SmoothedFilter(double smoothingFactor)
    {
        _factor = Math.Clamp(smoothingFactor, 0.0, 1.0);
    }

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _previous != null)
            return _previous;

        var raw = new ScreenPoint(rawGaze.X, rawGaze.Y);
        if (_previous == null)
        {
            _previous = raw;
            return raw;
        }

        var smoothed = new ScreenPoint(
            _factor * raw.X + (1 - _factor) * _previous.X,
            _factor * raw.Y + (1 - _factor) * _previous.Y);
        _previous = smoothed;
        return smoothed;
    }

    public void Reset() => _previous = null;
}
