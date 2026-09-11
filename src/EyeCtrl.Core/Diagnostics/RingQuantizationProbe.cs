namespace EyeCtrl.Core.Diagnostics;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Instrument 2: does sub-pixel filter output survive the last stage that moves the cursor ring, or is
/// it thrown away?
/// <para>
/// The ring is positioned by writing <c>Window.Left</c>/<c>Top</c> in DIPs. If the window manager
/// lands the window on whole device pixels, then every fractional pixel a filter works to produce is
/// discarded at the very end — and NO filter, however well tuned, can make the ring sub-pixel smooth.
/// That would promote the transform-based overlay endgame; the opposite result drops it from the
/// roadmap. Either way the decision stops being a guess.
/// </para>
/// <para>
/// The second question is cadence: position updates arriving at ~33 Hz against a 60 Hz compositor can
/// beat, producing judder that no amount of spatial filtering removes. The interval histogram settles
/// whether that beat is present.
/// </para>
/// <para>
/// Measurement only — the caller reads back where the window actually landed and reports it here;
/// nothing about how the ring is positioned changes.
/// </para>
/// <para>Not thread-safe: record from the UI thread, which is the only thread that moves a window.</para>
/// </summary>
public sealed class RingQuantizationProbe
{
    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Residual below which a write is treated as having landed exactly. A device pixel is the unit in
    /// question, so this is well inside "same pixel" while absorbing double-rounding in the DIP↔device
    /// conversion itself.
    /// </summary>
    public const double ExactResidualDevicePixels = 0.01;

    private readonly DurationHistogram _intervals;
    private long _lastTimestamp;

    private long _count;
    private long _landedOnWholeDevicePixel;
    private long _matchedRequestExactly;
    private double _sumAbsResidual;

    /// <summary>Initializes the probe.</summary>
    /// <param name="maxIntervalMilliseconds">Largest inter-call interval given its own bucket.</param>
    public RingQuantizationProbe(int maxIntervalMilliseconds = 255)
    {
        _intervals = new DurationHistogram(maxIntervalMilliseconds);
    }

    /// <summary>Distribution of intervals between successive position writes — the cadence evidence.</summary>
    public DurationHistogram Intervals => _intervals;

    /// <summary>Number of position writes observed.</summary>
    public long Count => _count;

    /// <summary>Fraction of writes whose observed position sat on a whole device pixel.</summary>
    public double WholePixelFraction => _count == 0 ? 0 : (double)_landedOnWholeDevicePixel / _count;

    /// <summary>Fraction of writes where the observed position matched the request to within
    /// <see cref="ExactResidualDevicePixels"/> — i.e. the sub-pixel request survived.</summary>
    public double ExactMatchFraction => _count == 0 ? 0 : (double)_matchedRequestExactly / _count;

    /// <summary>Mean absolute difference, in device pixels, between where the ring was asked to go and
    /// where it landed. This is the size of the error quantization introduces.</summary>
    public double MeanAbsResidualDevicePixels => _count == 0 ? 0 : _sumAbsResidual / _count;

    /// <summary>
    /// Records one position write and where the window actually ended up.
    /// </summary>
    /// <param name="requestedDipX">The X value written to <c>Window.Left</c>, in DIPs.</param>
    /// <param name="requestedDipY">The Y value written to <c>Window.Top</c>, in DIPs.</param>
    /// <param name="observedDeviceX">Observed left edge in device pixels (e.g. from <c>GetWindowRect</c>).</param>
    /// <param name="observedDeviceY">Observed top edge in device pixels.</param>
    /// <param name="scaleX">Horizontal DIP→device scale in force.</param>
    /// <param name="scaleY">Vertical DIP→device scale in force.</param>
    public void Record(
        double requestedDipX, double requestedDipY,
        double observedDeviceX, double observedDeviceY,
        double scaleX, double scaleY)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastTimestamp != 0)
            _intervals.Record((now - _lastTimestamp) * TicksToMilliseconds);
        _lastTimestamp = now;

        _count++;

        // Compare in DEVICE pixels: that is the unit quantization happens in, and comparing in DIPs
        // would hide the effect entirely on a 100 % display, where one DIP is one device pixel.
        double requestedDeviceX = requestedDipX * scaleX;
        double requestedDeviceY = requestedDipY * scaleY;

        double residualX = Math.Abs(requestedDeviceX - observedDeviceX);
        double residualY = Math.Abs(requestedDeviceY - observedDeviceY);
        double residual = Math.Max(residualX, residualY);
        _sumAbsResidual += residual;

        if (residual <= ExactResidualDevicePixels) _matchedRequestExactly++;

        if (IsWholeNumber(observedDeviceX) && IsWholeNumber(observedDeviceY))
            _landedOnWholeDevicePixel++;
    }

    private static bool IsWholeNumber(double value) =>
        Math.Abs(value - Math.Round(value)) <= ExactResidualDevicePixels;

    /// <summary>
    /// The verdict: <c>true</c> when sub-pixel positioning survives to the window, <c>false</c> when it
    /// is quantized away.
    /// <para>
    /// Decided on whether the requests that ASKED for a fractional device pixel were honoured. Reading
    /// "everything landed on a whole pixel" alone would be a trap: if every request happened to be a
    /// whole pixel, perfect fidelity looks identical to total quantization.
    /// </para>
    /// </summary>
    /// <returns><c>null</c> when no sample asked for a fractional position, so the question is unanswered.</returns>
    public bool? SubPixelSurvives()
    {
        if (_count == 0) return null;

        // No fractional requests at all → nothing was ever asked of the window that could be quantized.
        if (_sumAbsResidual == 0 && _matchedRequestExactly == _count && _landedOnWholeDevicePixel == _count)
            return null;

        return ExactMatchFraction > 0.99;
    }

    /// <summary>Renders the session summary, including the verdict, as one log line.</summary>
    public string Describe()
    {
        if (_count == 0) return "ring quantization: no samples";

        string verdict = SubPixelSurvives() switch
        {
            true => "sub-pixel output SURVIVES to the window — the transform-based overlay endgame is not needed for this",
            false => "sub-pixel output is DISCARDED to whole device pixels — no filter can make the ring sub-pixel smooth; "
                   + "the transform-based overlay endgame is promoted",
            null => "inconclusive — no write asked for a fractional device pixel",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"ring quantization: n={_count} exactMatch={ExactMatchFraction:P1} wholePixel={WholePixelFraction:P1} " +
            $"meanResidual={MeanAbsResidualDevicePixels:0.###}px; {verdict}");
    }

    /// <summary>Renders the cadence summary as one log line.</summary>
    public string DescribeCadence() =>
        _intervals.Count == 0
            ? "ring cadence: no intervals"
            : $"ring cadence: {_intervals.Describe()} buckets=[{_intervals.DescribeBuckets()}]";
}
