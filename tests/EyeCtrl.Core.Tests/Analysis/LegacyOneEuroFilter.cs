namespace EyeCtrl.Core.Tests.Analysis;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// The One-Euro filter EXACTLY as it shipped before the Stage 3 retune, frozen here as the measurement
/// baseline: pixel-unit <c>beta = 0.5</c>, <c>minCutoff = 5.0</c>, speed differenced from the RAW sample
/// against the previous FILTERED one, no jump detector, no cutoff cap, no <c>dt</c> clamp.
/// <para>
/// It lives in the test project, not in production, because that is precisely what it is: a reference
/// point, not a filter anyone should be able to select. Every "before" number the acceptance gate is
/// scored against comes from here, and <see cref="ReplayPipeline.Baseline"/> points at it — so the
/// baseline keeps meaning "what the user's machine actually did" no matter how far the production filter
/// moves afterwards. Without the freeze, the retune would silently redefine its own control: the
/// characterisation tests that recorded the shipped filter's 0 ms step response would start measuring
/// the candidate and quietly agree with it.
/// </para>
/// <para>
/// Do not fix anything in here. A change to this class invalidates every recorded baseline number.
/// </para>
/// </summary>
internal sealed class LegacyOneEuroFilter : IGazeFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private LowPass? _xFilter;
    private LowPass? _yFilter;
    private LowPass? _dxFilter;
    private LowPass? _dyFilter;
    private long _prevTimestamp;
    private bool _initialized;
    private PhysicalPoint _lastFiltered = new(0, 0);

    /// <summary>Constructs the legacy filter. The defaults are the ones that shipped.</summary>
    public LegacyOneEuroFilter(double minCutoff = 5.0, double beta = 0.5, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public PhysicalPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _initialized)
            return _lastFiltered;

        double x = rawGaze.X;
        double y = rawGaze.Y;

        if (!_initialized)
        {
            _xFilter = new LowPass(x);
            _yFilter = new LowPass(y);
            _dxFilter = new LowPass(0);
            _dyFilter = new LowPass(0);
            _prevTimestamp = rawGaze.TimestampMicroseconds;
            _initialized = true;
            _lastFiltered = new PhysicalPoint(x, y);
            return _lastFiltered;
        }

        long dtUs = rawGaze.TimestampMicroseconds - _prevTimestamp;
        double dt = dtUs > 0 ? dtUs / 1_000_000.0 : 1.0 / 30;
        _prevTimestamp = rawGaze.TimestampMicroseconds;

        double dx = (x - _xFilter!.Last) / dt;
        double dy = (y - _yFilter!.Last) / dt;

        double dAlpha = Alpha(_dCutoff, dt);
        double edx = _dxFilter!.Filter(dx, dAlpha);
        double edy = _dyFilter!.Filter(dy, dAlpha);

        double speed = Math.Sqrt((edx * edx) + (edy * edy));
        double cutoff = _minCutoff + (_beta * speed);

        double alpha = Alpha(cutoff, dt);
        double fx = _xFilter.Filter(x, alpha);
        double fy = _yFilter.Filter(y, alpha);

        _lastFiltered = new PhysicalPoint(fx, fy);
        return _lastFiltered;
    }

    public void Reset()
    {
        _initialized = false;
        _xFilter = null;
        _yFilter = null;
        _dxFilter = null;
        _dyFilter = null;
        _lastFiltered = new PhysicalPoint(0, 0);
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + (tau / dt));
    }

    private sealed class LowPass
    {
        public double Last { get; private set; }

        public LowPass(double initVal) => Last = initVal;

        public double Filter(double value, double alpha)
        {
            double result = (alpha * value) + ((1 - alpha) * Last);
            Last = result;
            return result;
        }
    }
}
