namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Implements the 1€ Filter (One Euro Filter), an adaptive low-pass filter designed for noisy signals
/// with variable frequency, particularly effective for real-time filtering of human motion data.
/// </summary>
/// <remarks>
/// <para>
/// The 1€ Filter automatically adjusts its smoothing characteristics based on signal velocity:
/// it provides strong smoothing during slow movements (like fixations) and reduces lag during
/// fast movements (like saccades), making it ideal for gaze tracking applications.
/// </para>
/// <para>
/// <b>Algorithm Overview:</b><br/>
/// The filter uses two low-pass filters: one for the signal itself and one for its derivative (velocity).
/// The cutoff frequency of the signal filter adapts based on the filtered velocity, allowing the filter
/// to be smooth when movement is slow and responsive when movement is fast.
/// </para>
/// <para>
/// <b>Performance:</b> O(1) time complexity per sample, O(1) space complexity.<br/>
/// The filter maintains minimal state (last filtered values) and performs constant-time computations.
/// </para>
/// <para>
/// <b>Reference:</b><br/>
/// Casiez, G., Roussel, N., &amp; Vogel, D. (2012). 1€ Filter: A Simple Speed-based Low-pass Filter
/// for Noisy Input in Interactive Systems. In Proceedings of the SIGCHI Conference on Human Factors
/// in Computing Systems (CHI '12), pp. 2527-2530.<br/>
/// Paper: <see href="https://hal.inria.fr/hal-00670496/document">https://hal.inria.fr/hal-00670496/document</see><br/>
/// Interactive Demo: <see href="http://cristal.univ-lille.fr/~casiez/1euro/">http://cristal.univ-lille.fr/~casiez/1euro/</see>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Create filter with default parameters (good starting point for most applications)
/// var filter = new OneEuroFilter();
///
/// // Or customize parameters for specific needs
/// var customFilter = new OneEuroFilter(
///     minCutoff: 1.0,   // Lower = more smoothing, higher = more responsive
///     beta: 0.7,         // Higher = more responsive to speed changes
///     dCutoff: 1.0       // Cutoff for derivative (typically left at 1.0)
/// );
///
/// // Filter gaze points in real-time
/// foreach (var rawGaze in gazeStream)
/// {
///     ScreenPoint filtered = filter.Filter(rawGaze);
///     // Use filtered point for cursor positioning, fixation detection, etc.
/// }
///
/// // Reset when starting a new tracking session
/// filter.Reset();
/// </code>
/// </example>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="OneEuroFilter"/> with specified parameters.
    /// </summary>
    /// <param name="minCutoff">
    /// Minimum cutoff frequency (Hz) for the filter when there is no movement. Lower values (e.g., 0.5-1.0)
    /// provide more aggressive smoothing but introduce more lag. Higher values (e.g., 5.0-10.0) reduce smoothing
    /// and lag. Default is 5.0 Hz. Typical range: 0.5-10.0 Hz.
    /// <para><b>Tuning guidance:</b> If filtered signal is too jittery during fixations, decrease this value.
    /// If lag is too noticeable during movements, increase this value.</para>
    /// </param>
    /// <param name="beta">
    /// Speed coefficient that controls how much the cutoff frequency increases with signal velocity.
    /// Higher values (e.g., 0.7-1.0) make the filter more responsive to fast movements but may allow
    /// more jitter. Lower values (e.g., 0.001-0.3) prioritize smoothness over responsiveness.
    /// Default is 0.5. Typical range: 0.001-1.0.
    /// <para><b>Tuning guidance:</b> If fast movements feel laggy or have trailing, increase this value.
    /// If there's too much jitter during rapid movements, decrease this value.</para>
    /// </param>
    /// <param name="dCutoff">
    /// Cutoff frequency (Hz) for filtering the derivative (velocity) of the signal. This is typically
    /// kept at a low value (1.0 Hz) to smooth velocity estimation. Default is 1.0 Hz.
    /// <para><b>Tuning guidance:</b> This parameter rarely needs adjustment. Only modify if velocity
    /// calculations appear noisy.</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="minCutoff"/>, <paramref name="beta"/>, or <paramref name="dCutoff"/>
    /// is not a finite number (NaN or Infinity).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minCutoff"/> or <paramref name="dCutoff"/> is less than or equal to zero,
    /// or when <paramref name="beta"/> is negative.
    /// </exception>
    public OneEuroFilter(double minCutoff = 5.0, double beta = 0.5, double dCutoff = 1.0)
    {
        if (!double.IsFinite(minCutoff))
            throw new ArgumentException("minCutoff must be a finite number.", nameof(minCutoff));
        if (!double.IsFinite(beta))
            throw new ArgumentException("beta must be a finite number.", nameof(beta));
        if (!double.IsFinite(dCutoff))
            throw new ArgumentException("dCutoff must be a finite number.", nameof(dCutoff));

        if (minCutoff <= 0)
            throw new ArgumentOutOfRangeException(nameof(minCutoff), minCutoff, "minCutoff must be positive.");
        if (beta < 0)
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "beta must be non-negative.");
        if (dCutoff <= 0)
            throw new ArgumentOutOfRangeException(nameof(dCutoff), dCutoff, "dCutoff must be positive.");

        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    /// <summary>
    /// Filters a raw gaze point using the adaptive 1€ Filter algorithm.
    /// </summary>
    /// <param name="rawGaze">
    /// The raw, unfiltered gaze point from the eye tracker. Must include timestamp for proper
    /// filtering behavior.
    /// </param>
    /// <returns>
    /// A smoothed <see cref="ScreenPoint"/> with reduced jitter. If the input gaze point is invalid
    /// and the filter has been initialized, returns the last successfully filtered point.
    /// </returns>
    /// <remarks>
    /// <para>
    /// On first call (initialization), the filter returns the raw point and initializes internal state.
    /// Subsequent calls apply adaptive filtering based on the estimated velocity of the signal.
    /// </para>
    /// <para>
    /// The filter handles invalid gaze points gracefully by returning the last valid filtered position,
    /// which prevents cursor jumps during brief tracking losses.
    /// </para>
    /// </remarks>
    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _initialized)
            return _lastFiltered;

        double x = rawGaze.X;
        double y = rawGaze.Y;

        if (!_initialized)
        {
            // Initialize all filters with a default sample rate of 30 Hz
            _xFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), x);
            _yFilter = new LowPassFilter(Alpha(_minCutoff, 1.0 / 30), y);
            _dxFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _dyFilter = new LowPassFilter(Alpha(_dCutoff, 1.0 / 30), 0);
            _prevTimestamp = rawGaze.TimestampMicroseconds;
            _initialized = true;
            _lastFiltered = new ScreenPoint(x, y);
            return _lastFiltered;
        }

        // Calculate time delta and convert from microseconds to seconds
        long dt_us = rawGaze.TimestampMicroseconds - _prevTimestamp;
        double dt = dt_us > 0 ? dt_us / 1_000_000.0 : 1.0 / 30;  // Fallback to 30 Hz if timestamps are invalid
        _prevTimestamp = rawGaze.TimestampMicroseconds;

        // Estimate derivatives (velocity) using finite difference method: dx/dt
        double dx = (x - _xFilter!.Last) / dt;
        double dy = (y - _yFilter!.Last) / dt;

        // Filter the derivatives to get smooth velocity estimates
        double dAlpha = Alpha(_dCutoff, dt);
        double edx = _dxFilter!.Filter(dx, dAlpha);
        double edy = _dyFilter!.Filter(dy, dAlpha);

        // Compute Euclidean speed (magnitude of velocity vector) in pixels per second
        double speed = Math.Sqrt(edx * edx + edy * edy);

        // Adaptive cutoff: increases linearly with speed, allowing faster response during rapid movements
        // Formula: cutoff_freq = minCutoff + beta * speed
        double cutoff = _minCutoff + _beta * speed;

        // Apply adaptive low-pass filter to the signal with the computed cutoff frequency
        double alpha = Alpha(cutoff, dt);
        double fx = _xFilter.Filter(x, alpha);
        double fy = _yFilter.Filter(y, alpha);

        _lastFiltered = new ScreenPoint(fx, fy);
        return _lastFiltered;
    }

    /// <summary>
    /// Resets the filter to its initial state, clearing all history.
    /// </summary>
    /// <remarks>
    /// Call this method when starting a new tracking session or when the signal has been interrupted
    /// for a significant period. After reset, the next call to <see cref="Filter"/> will reinitialize
    /// the filter state.
    /// </remarks>
    public void Reset()
    {
        _initialized = false;
        _xFilter = null;
        _yFilter = null;
        _dxFilter = null;
        _dyFilter = null;
        _lastFiltered = new ScreenPoint(0, 0);
    }

    /// <summary>
    /// Computes the smoothing factor (alpha) for the low-pass filter based on cutoff frequency and sample period.
    /// </summary>
    /// <param name="cutoff">The desired cutoff frequency in Hz.</param>
    /// <param name="dt">The time elapsed since the last sample, in seconds.</param>
    /// <returns>
    /// The smoothing factor alpha in range [0, 1], where values closer to 1 result in less smoothing
    /// and values closer to 0 result in more smoothing.
    /// </returns>
    /// <remarks>
    /// This method implements the standard formula for converting cutoff frequency to the smoothing
    /// factor for a first-order low-pass filter:
    /// <code>
    /// tau = 1 / (2 * π * cutoff)
    /// alpha = 1 / (1 + tau / dt)
    /// </code>
    /// where tau is the time constant of the filter.
    /// </remarks>
    private static double Alpha(double cutoff, double dt)
    {
        // Time constant (tau) represents the time it takes for the filter to reach ~63% of a step input
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }

    /// <summary>
    /// Internal low-pass filter implementation using exponential smoothing.
    /// </summary>
    /// <remarks>
    /// Implements a simple first-order low-pass filter (exponential moving average):
    /// <code>
    /// output = alpha * input + (1 - alpha) * previous_output
    /// </code>
    /// This is a fundamental signal processing building block used by the 1€ Filter.
    /// </remarks>
    private class LowPassFilter
    {
        /// <summary>
        /// Gets the last filtered value output by this filter.
        /// </summary>
        public double Last { get; private set; }

        /// <summary>
        /// Initializes a new low-pass filter with the given smoothing factor and initial value.
        /// </summary>
        /// <param name="alpha">Initial smoothing factor (not used, alpha is passed per-sample).</param>
        /// <param name="initVal">Initial value for the filter state.</param>
        public LowPassFilter(double alpha, double initVal)
        {
            Last = initVal;
        }

        /// <summary>
        /// Filters a single value using exponential smoothing.
        /// </summary>
        /// <param name="value">The new input value to filter.</param>
        /// <param name="alpha">
        /// Smoothing factor in range [0, 1]. Higher values (close to 1) make the filter more responsive
        /// to new values; lower values (close to 0) increase smoothing.
        /// </param>
        /// <returns>The filtered output value.</returns>
        public double Filter(double value, double alpha)
        {
            // Exponential smoothing: weighted average of new value and previous filtered value
            double result = alpha * value + (1 - alpha) * Last;
            Last = result;
            return result;
        }
    }
}
