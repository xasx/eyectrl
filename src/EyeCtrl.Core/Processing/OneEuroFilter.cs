namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// 1€ Filter (Casiez et al., 2012) — adaptive low-pass filter.
/// Smooth when fixating, responsive during fast movement.
/// </summary>
public class OneEuroFilter : IGazeFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private LowPassFilter? _xFilter;
    private LowPassFilter? _yFilter;
    private LowPassFilter? _dxFilter;
    private LowPassFilter? _dyFilter;
    private long _prevTimestamp;
    private bool _initialized;
    private ScreenPoint _lastFiltered = new(0, 0);

    public OneEuroFilter(double minCutoff = 5.0, double beta = 0.5, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _initialized)
            return _lastFiltered;

        double x = rawGaze.X;
        double y = rawGaze.Y;

        if (!_initialized)
        {
            _xFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), x);
            _yFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), y);
            _dxFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _dyFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _prevTimestamp = rawGaze.TimestampMicroseconds;
            _initialized = true;
            _lastFiltered = new ScreenPoint(x, y);
            return _lastFiltered;
        }

        long dt_us = rawGaze.TimestampMicroseconds - _prevTimestamp;
        double dt = dt_us > 0 ? dt_us / 1_000_000.0 : 1.0 / 30;
        _prevTimestamp = rawGaze.TimestampMicroseconds;

        // Estimate derivatives
        double dx = (x - _xFilter!.Last) / dt;
        double dy = (y - _yFilter!.Last) / dt;

        // Filter derivatives
        double dAlpha = Alpha(_dCutoff, dt);
        double edx = _dxFilter!.Filter(dx, dAlpha);
        double edy = _dyFilter!.Filter(dy, dAlpha);

        // Compute adaptive cutoff based on speed
        double speed = Math.Sqrt(edx * edx + edy * edy);
        double cutoff = _minCutoff + _beta * speed;

        // Filter signal
        double alpha = Alpha(cutoff, dt);
        double fx = _xFilter.Filter(x, alpha);
        double fy = _yFilter.Filter(y, alpha);

        _lastFiltered = new ScreenPoint(fx, fy);
        return _lastFiltered;
    }

    public void Reset()
    {
        _initialized = false;
        _xFilter = null;
        _yFilter = null;
        _dxFilter = null;
        _dyFilter = null;
        _lastFiltered = new ScreenPoint(0, 0);
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }

    private class LowPassFilter
    {
        public double Last { get; private set; }

        public LowPassFilter(double alpha, double initVal)
        {
            Last = initVal;
        }

        public double Filter(double value, double alpha)
        {
            double result = alpha * value + (1 - alpha) * Last;
            Last = result;
            return result;
        }
    }
}
