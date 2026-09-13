namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Implements the 1€ Filter (One Euro Filter), an adaptive low-pass filter designed for noisy signals
/// with variable frequency, particularly effective for real-time filtering of human motion data.
/// Returns positions in physical screen pixels.
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
/// <b>Deviations from the paper (Stage 3 retune, gaze-jitter debate).</b> Four, each closing a specific
/// failure the shipped form had on a ~33 Hz eye tracker:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The speed estimate is derived from the filtered signal</b>, not from <c>(x_raw − x_filtered)/dt</c>.
/// The old numerator WAS the per-sample noise residual, divided by a ~30 ms <c>dt</c> — so noise inflated
/// the speed, the speed raised the cutoff, and attenuation FELL as the tracker got noisier. It is the
/// pathology that made the shipped filter a pass-through, and it is what this class's own documentation
/// always claimed it did.
/// </description></item>
/// <item><description>
/// <b>A jump detector on the innovation</b> (<c>|x_raw − p_filtered|</c>) replaces <c>beta</c> as the
/// saccade mechanism. It fires on the FIRST sample of a saccade, whereas the derivative EMA needs several
/// samples to charge. Without it, (1) is a feedback trap: a heavily smoothed output moves slowly, so the
/// cutoff never opens, so the output keeps moving slowly.
/// </description></item>
/// <item><description>
/// <b>The cutoff is capped at ~<c>sampleRate/3</c></b>. Above Nyquist a cutoff is meaningless and
/// <c>alpha → 1</c>; the cap makes it structurally impossible for the adaptive branch to degrade into
/// <see cref="DirectFilter"/>. Because both the cap and <see cref="Alpha"/> use the same <c>dt</c>, the
/// cap is exactly a constant ceiling of <c>alpha ≤ 1/(1 + 3/2π) ≈ 0.677</c> — one sample can never cover
/// more than ~68 % of a step.
/// </description></item>
/// <item><description>
/// <b><c>dt</c> is clamped for <see cref="Alpha"/> only</b> (<c>dtSmooth</c>), never for the derivative
/// divisor. A long dropout otherwise opens <c>alpha</c> to ~1 and the first post-blink sample arrives
/// unfiltered — the recovery pop. Clamping the DIVISOR instead would report a 50 px move over a 100 ms
/// gap as 1500 px/s and recreate the same snap through the speed term.
/// </description></item>
/// </list>
/// <para>
/// <b>Known behaviour worth naming:</b> during SUSTAINED motion faster than about
/// <c>JumpFloorPixels</c> per sample (~455 px/s at 33 Hz) that starts abruptly, every sample's innovation
/// exceeds the threshold, the noise estimator never gets a quiet sample to learn from, and the filter
/// runs at the cap for the whole movement. That is the right answer for a saccade and an acceptable one
/// for fast pursuit — the output still lags by a bounded ~11 px and light filtering is what fast motion
/// wants — and it resolves by itself as soon as the gaze slows. Motion that ACCELERATES into that range
/// keeps the estimator fed and stays on the adaptive branch throughout.
/// </para>
/// <para>
/// <b>Performance:</b> O(1) time complexity per sample, O(1) space complexity.<br/>
/// The filter maintains minimal state (last filtered values) and performs constant-time computations.
/// </para>
/// <para>
/// <b>Reference:</b><br/>
/// Casiez, G., Roussel, N., &amp; Vogel, D. (2012). 1€ Filter: A Simple Speed-based Low-pass Filter
/// for Noisy Input in Interactive Systems. In Proceedings of the 30th ACM Conference on Human Factors
/// in Computing Systems (CHI '12), pp. 2527-2530.<br/>
/// DOI: <see href="https://doi.org/10.1145/2207676.2208639">10.1145/2207676.2208639</see><br/>
/// Full Paper: <see href="https://inria.hal.science/hal-00670496/document">https://inria.hal.science/hal-00670496/document</see><br/>
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
///     beta: 20.0,        // Hz of extra cutoff at one screen diagonal per second
///     dCutoff: 1.0       // Cutoff for derivative (typically left at 1.0)
/// );
///
/// // Filter gaze points in real-time
/// foreach (var rawGaze in gazeStream)
/// {
///     PhysicalPoint filtered = filter.Filter(rawGaze);
///     // Use filtered point for cursor positioning, fixation detection, etc.
/// }
///
/// // Reset when starting a new tracking session
/// filter.Reset();
/// </code>
/// </example>
public class OneEuroFilter : IGazeFilterStateAdopter
{
    /// <summary>
    /// Speed normalisation constant, in pixels: <c>beta</c> is expressed in Hz of extra cutoff per
    /// ONE OF THESE PER SECOND, rather than per pixel per second.
    /// </summary>
    /// <remarks>
    /// A nominal screen diagonal (1920×1080 is 2203 px; 2000 is the round number that stands in for
    /// "one screen diagonal"). The old pixel-unit <c>beta</c> was the sharpest "beyond settings tuning" finding
    /// of the jitter debate: at 1000 px/s a sane cutoff is ~15 Hz, which needs <c>beta ≈ 0.01</c> in
    /// pixel units, while the shipped default was 0.5 and the settings slider's smallest non-zero step
    /// was 0.1 — the correct value was not expressible in the UI at all. Dividing the speed by this
    /// constant moves the useful range onto a slider that can reach it, and makes the number mean
    /// something a person can reason about ("at one screen diagonal per second the filter opens to
    /// minCutoff + beta Hz") instead of depending on the display's pixel density.
    /// <para>
    /// Deliberately a CONSTANT rather than the live screen size: it is a unit definition that a
    /// persisted <c>beta</c> is denominated in, so a user moving their profile to a different monitor
    /// must keep the behaviour they tuned, not have it silently rescaled.
    /// </para>
    /// </remarks>
    public const double SpeedNormalizationPixels = 2000.0;

    // A2 (debate item 4): the largest dt Alpha() is allowed to see — three sample periods at the
    // PCEye 5's ~33 Hz. Three, not one: a dt-aware prior really does go stale over a blink, so the
    // filter SHOULD open up somewhat after a dropout; what it must not do is open all the way to
    // alpha ≈ 1 and hand back the first post-blink sample raw.
    private const double MaxAlphaDtSeconds = 0.100;

    // Debate item 3: cutoff ceiling as a fraction of the sample rate. See the class remarks — with
    // the same dt on both sides this is exactly alpha <= 1/(1 + 3/2pi) ≈ 0.6769.
    private const double CutoffCapFractionOfSampleRate = 1.0 / 3.0;

    // Debate item 2. The threshold is 3x the running RMS of the innovation magnitude, which for
    // isotropic noise of per-axis sigma is 3*sqrt(2)*sigma ≈ 4.24 sigma — a ~1e-4 per-sample false-fire
    // rate, i.e. about one per four minutes at 33 Hz. (The estimator censors its own upper tail by
    // skipping jump samples; solving that fixed point gives back the same 4.24 sigma, so the censoring
    // does not bias the threshold down.) At the sigma ≈ 10 px this tracker is expected to sit at, that
    // lands on ≈ 42 px, within 1.5x of the debate's "or a fixed ~60 px".
    //
    // The floor governs only a pristine signal and the first sample or two after CONSTRUCTION, where the
    // estimate is still empty. Not after a Reset and not after a rebuild: the estimate deliberately
    // survives both (see the field declaration and Reset's comment), which is what shrank this window to
    // a genuine startup case. 15 px is ~8 sigma on the quiet synthetic stream (it never fires there)
    // while still letting a genuine first saccade through.
    //
    // The value is a free choice inside a wide band, not a pinned constant: a sweep against
    // OneEuroFilterTests.Filter_StationaryGaze_ConvergesToTarget passes at every floor from 15 to 59 and
    // fails only at 60 — so the single value that test forbids is the debate's own "fixed ~60 px"
    // alternative. On real input the floor never governs at all: at sigma ~ 10 px the self-tuned
    // threshold is ~44 px, so Math.Max picks the estimator every time.
    private const double JumpFloorPixels = 15.0;
    private const double JumpResidualMultiple = 3.0;

    // Long-run memory of the innovation estimator, ~10 samples (300 ms at 33 Hz). During warm-up the
    // weight is 1/n instead, so ONE quiet sample already seeds a usable threshold: an estimator that
    // crawled up from zero would spend its first ~20 samples with the threshold pinned at the floor,
    // passing a third of a sigma=10 stream straight through — and those first samples are exactly the
    // post-dropout recovery window this retune is trying to calm.
    private const double ResidualEmaWeight = 0.1;

    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private LowPassFilter? _xFilter;
    private LowPassFilter? _yFilter;
    private LowPassFilter? _dxFilter;
    private LowPassFilter? _dyFilter;
    private long _prevTimestamp;
    private bool _initialized;
    private PhysicalPoint _lastFiltered = new(0, 0);
    // The output BEFORE _lastFiltered, and the dt that separated them: together they are the filtered
    // derivative (item 1). Differencing two FILTERED points over the interval that actually produced
    // them costs one sample of delay in the velocity estimate and removes all of the noise from it;
    // the jump detector covers the delay.
    private PhysicalPoint _prevFiltered = new(0, 0);
    private double _prevDt;

    // The learned noise level and its sample count, as ONE value.
    //
    // LIFETIME: set at construction and never cleared again — not by initialisation, not by Reset. It
    // is an estimate of the TRACKER's noise, which does not change because the eye moved, because the
    // user blinked, or because a settings slider was dragged. Position state is per-stream; this is not.
    //
    // This is load-bearing, not tidiness. Clearing it dropped the threshold to its 15 px floor on the
    // first sample after every filter rebuild and every over-budget blink — and at sigma = 15 px the
    // very next noise sample clears 15 px about 60 % of the time, so the filter opened to alpha = 0.677
    // on pure noise and emitted a ~13 px pop where it should have emitted ~4 px. Rebuilds fire on every
    // settings change (once per gaze frame while a slider is dragged) and up to once a second while the
    // adaptation controller is ramping, so the "rare startup case" was neither rare nor startup.
    //
    // ONE FIELD, not two, so that a partial hand-over cannot be written. The RMS and the count are only
    // meaningful together: the warm-up weight is max(0.1, 1/n), so a carried RMS with n left at 0 is
    // overwritten by the very next innovation at weight 1. Measured, 3-sample mean displacement after a
    // rebuild at sigma=15: 5.31 px correct, 5.99 px carrying the RMS alone, 12.90 px carrying nothing.
    // The middle case is a real degradation that sat 0.007 px inside the test's bound — detectable only
    // by a threshold tuned tighter than the noise justifies, so the shape of the data closes it instead.
    //
    // A readonly record STRUCT: assignment is atomic and allocation-free on the per-sample path; the one
    // boxing conversion happens in CaptureState, once per rebuild.
    private NoiseEstimate _noise;

    /// <summary>
    /// Initializes a new instance of the <see cref="OneEuroFilter"/> with specified parameters.
    /// </summary>
    /// <param name="minCutoff">
    /// Minimum cutoff frequency (Hz) for the filter when there is no movement. Lower values (e.g., 0.5-1.0)
    /// provide more aggressive smoothing but introduce more lag. Higher values (e.g., 5.0-10.0) reduce smoothing
    /// and lag. Default is 1.5 Hz. Typical range: 0.5-10.0 Hz.
    /// <para><b>Tuning guidance:</b> If filtered signal is too jittery during fixations, decrease this value.
    /// If lag is too noticeable during movements, increase this value.</para>
    /// <para>At 33 Hz a one-pole filter at 1.5 Hz attenuates white noise by ~65 % for ~106 ms of lag at
    /// rest; that pair is the ceiling of what this knob alone can buy, which is why the retune adds the
    /// jump detector rather than lowering it further.</para>
    /// </param>
    /// <param name="beta">
    /// Speed coefficient: how many Hz the cutoff rises per <see cref="SpeedNormalizationPixels"/> pixels
    /// per second of filtered speed. Default is 20.0 — i.e. a one-screen-diagonal-per-second movement adds
    /// 20 Hz. Typical range: 0-60.
    /// <para><b>Tuning guidance:</b> If fast movements feel laggy or have trailing, increase this value.
    /// If there's too much jitter during rapid movements, decrease this value.</para>
    /// <para><b>Units changed in the Stage 3 retune.</b> This used to be Hz per PIXEL per second, where the
    /// correct value was ~0.01 and the shipped default of 0.5 was ~50x too large. A persisted pixel-unit
    /// value is <c>SettingsService</c>'s job to convert; nothing here can tell the two apart.</para>
    /// <para>Note that <c>beta</c> only shapes the sub-saccade regime: above roughly
    /// <c>(cap − minCutoff)/beta</c> screen diagonals per second the cutoff cap binds, and above the jump
    /// threshold the detector takes over entirely.</para>
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
    public OneEuroFilter(double minCutoff = 1.5, double beta = 20.0, double dCutoff = 1.0)
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
    /// A smoothed <see cref="PhysicalPoint"/> with reduced jitter in physical screen pixels.
    /// If the input gaze point is invalid and the filter has been initialized, returns the
    /// last successfully filtered point.
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
    public PhysicalPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _initialized)
            return _lastFiltered;

        double x = rawGaze.X;
        double y = rawGaze.Y;

        if (!_initialized)
        {
            _xFilter = new LowPassFilter(x);
            _yFilter = new LowPassFilter(y);
            _dxFilter = new LowPassFilter(0);
            _dyFilter = new LowPassFilter(0);
            _prevTimestamp = rawGaze.TimestampMicroseconds;
            _initialized = true;
            _lastFiltered = new PhysicalPoint(x, y);
            _prevFiltered = _lastFiltered;
            _prevDt = 0;
            // NOTE the noise estimate is deliberately NOT cleared here. See the field declaration:
            // it outlives initialisation, Reset, and rebuilds, because it describes the TRACKER, not
            // the position. Clearing it here dropped the threshold to its floor on the first sample
            // after every rebuild and every over-budget blink, which read as a saccade most of the time.
            return _lastFiltered;
        }

        // Calculate time delta and convert from microseconds to seconds
        long dt_us = rawGaze.TimestampMicroseconds - _prevTimestamp;
        double dt = dt_us > 0 ? dt_us / 1_000_000.0 : 1.0 / 30;  // Fallback to 30 Hz if timestamps are invalid
        _prevTimestamp = rawGaze.TimestampMicroseconds;

        // A2: the clamped dt, used for SMOOTHING decisions only. Never substitute it into a divisor
        // that turns a displacement into a velocity — over a 100 ms dropout that would report a
        // legitimate 50 px catch-up as 1500 px/s and blow the cutoff wide open, which is the very snap
        // the clamp exists to prevent.
        double dtSmooth = Math.Min(dt, MaxAlphaDtSeconds);

        // (1) Velocity from the last two FILTERED outputs, over the interval that separated them —
        // the TRUE dt, unclamped. The old form differenced the raw sample against the filtered one, so
        // its numerator was the per-sample noise residual and the "speed" it produced was mostly noise.
        double derivativeDt = _prevDt > 0 ? _prevDt : dt;
        double dx = (_lastFiltered.X - _prevFiltered.X) / derivativeDt;
        double dy = (_lastFiltered.Y - _prevFiltered.Y) / derivativeDt;

        // Filter the derivatives to get smooth velocity estimates
        double dAlpha = Alpha(_dCutoff, dtSmooth);
        double edx = _dxFilter!.Filter(dx, dAlpha);
        double edy = _dyFilter!.Filter(dy, dAlpha);

        // Compute Euclidean speed (magnitude of velocity vector) in pixels per second
        double speed = Math.Sqrt((edx * edx) + (edy * edy));

        // (3) The ceiling. Derived from dtSmooth so that a dropout cannot make the ceiling itself
        // collapse — the clamp and the cap must not fight each other.
        double cutoffCap = CutoffCapFractionOfSampleRate / dtSmooth;

        // (2) The innovation: how far this raw sample is from where the filter currently believes the
        // gaze is. During a fixation it IS the per-sample noise; on the first sample of a saccade it is
        // the whole saccade, one sample before the derivative EMA notices anything.
        double innovationX = x - _lastFiltered.X;
        double innovationY = y - _lastFiltered.Y;
        double innovation = Math.Sqrt((innovationX * innovationX) + (innovationY * innovationY));
        double jumpThreshold = Math.Max(JumpFloorPixels, JumpResidualMultiple * _noise.Rms);
        bool isJump = innovation > jumpThreshold;

        // Adaptive cutoff: rises linearly with the NORMALISED speed, bounded by the cap. A detected
        // jump goes straight to the cap: the fastest the filter is ever allowed to move, reached on the
        // saccade's first sample rather than after the derivative EMA has charged.
        double cutoff = isJump
            ? cutoffCap
            : Math.Min(_minCutoff + (_beta * (speed / SpeedNormalizationPixels)), cutoffCap);

        // The estimator learns from quiet samples only, or a saccade would inflate the threshold and
        // deafen the detector just after the moment it matters most. 1/n while young, EMA thereafter.
        if (!isJump)
        {
            int samples = _noise.Samples + 1;
            double weight = Math.Max(ResidualEmaWeight, 1.0 / samples);
            double rms = Math.Sqrt(((1 - weight) * _noise.Rms * _noise.Rms) + (weight * innovation * innovation));
            _noise = new NoiseEstimate(rms, samples);
        }

        // Apply adaptive low-pass filter to the signal with the computed cutoff frequency
        double alpha = Alpha(cutoff, dtSmooth);
        double fx = _xFilter!.Filter(x, alpha);
        double fy = _yFilter!.Filter(y, alpha);

        _prevFiltered = _lastFiltered;
        _prevDt = dt;
        _lastFiltered = new PhysicalPoint(fx, fy);
        LastCutoffHz = cutoff;
        LastCutoffCapHz = cutoffCap;
        LastSampleWasJump = isJump;
        LastJumpThresholdPixels = jumpThreshold;
        return _lastFiltered;
    }

    /// <summary>Test seam (<c>InternalsVisibleTo</c> → EyeCtrl.Core.Tests): the cutoff actually used for
    /// the most recent sample, in Hz. 0 until the first non-initialising sample.</summary>
    internal double LastCutoffHz { get; private set; }

    /// <summary>Test seam: the <c>sampleRate/3</c> ceiling in force for the most recent sample, in Hz.</summary>
    internal double LastCutoffCapHz { get; private set; }

    /// <summary>Test seam: whether the jump detector fired on the most recent sample.</summary>
    internal bool LastSampleWasJump { get; private set; }

    /// <summary>Test seam: the innovation threshold the most recent sample was judged against, in pixels.</summary>
    internal double LastJumpThresholdPixels { get; private set; }

    /// <summary>
    /// The learned noise level, carried across a rebuild. The two components are only meaningful
    /// together — see the <c>_noise</c> field — so they travel as one value and are stored as one.
    /// </summary>
    /// <param name="Rms">Running RMS of the innovation magnitude, in pixels.</param>
    /// <param name="Samples">How many samples have gone into it; drives the warm-up weight.</param>
    internal readonly record struct NoiseEstimate(double Rms, int Samples) : IGazeFilterState;

    // Both members are EXPLICIT interface implementations, not because the behaviour wants hiding but
    // because the language requires it: an implicit implementation must be public, and a public member
    // of this public class may not expose the internal IGazeFilterState. Test call sites therefore cast
    // to the interface, which is also the only way the production caller reaches them.

    /// <inheritdoc />
    /// <remarks>
    /// <c>null</c> until the estimator has seen a quiet sample, which is also what a freshly constructed
    /// filter hands over — so a rebuild during the first moments of a session correctly carries nothing
    /// rather than a fabricated zero estimate.
    /// </remarks>
    IGazeFilterState? IGazeFilterStateAdopter.CaptureState() => _noise.Samples == 0 ? null : _noise;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing about a rebuild changes the tracker, so the outgoing estimate is simply still true. Where
    /// it is mildly stale — a large <c>minCutoff</c> change alters how much the output lags, and so the
    /// innovation distribution — the EMA re-converges within ~10 samples, which is far better than
    /// starting from nothing.
    /// </remarks>
    void IGazeFilterStateAdopter.AdoptState(IGazeFilterState? state)
    {
        if (state is NoiseEstimate estimate) _noise = estimate;   // anything else: mode switch, start clean
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
        _lastFiltered = new PhysicalPoint(0, 0);
        _prevFiltered = new PhysicalPoint(0, 0);
        _prevDt = 0;
        // The noise estimate is NOT cleared. Reset means "the position history is void" — GapInterpolator
        // calls it when a dropout outruns the gap budget — and a blink does not change how noisy the
        // tracker is. Clearing it here would hand the next sample a floor-level threshold at exactly the
        // moment the eye is most likely to have moved, which is the post-blink recovery pop this retune
        // exists to calm. See the field declaration.
        LastCutoffHz = 0;
        LastCutoffCapHz = 0;
        LastSampleWasJump = false;
        LastJumpThresholdPixels = 0;
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
        return 1.0 / (1.0 + (tau / dt));
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
        /// Initializes a new low-pass filter with the given initial value.
        /// The smoothing factor is passed per-sample to <see cref="Filter"/>.
        /// </summary>
        /// <param name="initVal">Initial value for the filter state.</param>
        public LowPassFilter(double initVal)
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
            double result = (alpha * value) + ((1 - alpha) * Last);
            Last = result;
            return result;
        }
    }
}
