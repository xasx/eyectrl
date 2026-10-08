namespace EyeCtrl.Core.Calibration;

using System.Collections.Concurrent;
using EyeCtrl.Core.Models;

/// <summary>
/// Collects raw gaze samples for a single calibration target and computes the mean and standard
/// deviation on completion.
/// <para>
/// Collection is gated on <b>arrival</b>: samples only count once the user has actually reached
/// the target — either gaze enters the band around the target, or gaze becomes stable (a tight
/// recent cluster) <em>while that cluster sits inside the band</em>. The settle window starts at
/// ARRIVAL, not at target creation, and the sample that first establishes arrival is discarded with
/// it (it is the landing of the saccade). This is what makes calibration move at the user's pace:
/// before this, samples were counted from a timer with no notion of whether the user had found the
/// point yet (2026-08-17 field report: "calibration is not usable, the points should move at the
/// user's pace").
/// </para>
/// <para>
/// Collection is <b>also gated in space</b>, which it was not until 2026-09-13. Arrival used to open
/// the gate and never close it, so every sample for the next ~1.1 s entered the mean and σ wherever
/// the user's gaze had gone. That is how a session on a device with a measured 1–2 px stationary
/// floor produced σ ≈ 400 px: the number described the user searching the screen, and was then
/// reported as tracker noise and accepted by a gate that only looked at residuals. Now a post-arrival
/// sample counts only while it is inside the band, and <see cref="Coverage"/> reports what fraction
/// of the attempt that was — so a target built from two good samples and a hundred wandering ones can
/// no longer present itself as precise.
/// </para>
/// <para>
/// The two counts are the point: <see cref="InBandSamples"/> alone can always be made to look good by
/// waiting longer, and σ over a gated set is meaningless without knowing how much was thrown away.
/// They are only trustworthy read together.
/// </para>
/// </summary>
public class CalibrationPointCollector
{
    private readonly PhysicalPoint _target;
    private readonly long _settleWindowMs;
    private readonly int _maxSamples;
    private readonly double _bandRadiusPx;
    private readonly double _stabilityRadiusPx;
    private readonly int _stabilitySampleCount;
    private readonly Func<long> _nowMs;
    private readonly Queue<PhysicalPoint> _recent = new();
    private readonly ConcurrentBag<PhysicalPoint> _samples = new();
    private long _arrivedAtMs = -1;
    private int _count;
    private int _attempted;
    private volatile bool _ended;

    /// <summary>Whether the collector is still accepting new samples.</summary>
    public bool IsCollecting => !_ended;

    /// <summary>Whether gaze has reached the target. Settle runs from here.</summary>
    public bool HasArrived => Volatile.Read(ref _arrivedAtMs) >= 0;

    /// <summary>Post-settle samples that fell inside the band — the set the mean and σ are built from.</summary>
    public int InBandSamples => Volatile.Read(ref _count);

    /// <summary>
    /// Every post-settle sample this target saw, in band or not. The denominator of
    /// <see cref="Coverage"/>, and the number that makes a gated σ interpretable.
    /// </summary>
    public int AttemptedSamples => Volatile.Read(ref _attempted);

    /// <summary>
    /// Fraction of the attempt spent inside the band, or <c>null</c> before any post-settle sample
    /// has been seen. Null rather than zero on purpose: "nothing has happened yet" and "the user was
    /// never on target" are different states, and a UI that showed 0 % for the first would be lying
    /// about a target that has not been tried.
    /// </summary>
    public double? Coverage
    {
        get
        {
            int attempted = AttemptedSamples;
            return attempted == 0 ? null : (double)InBandSamples / attempted;
        }
    }

    /// <summary>
    /// Whether enough <em>in-band</em> post-settle samples have been collected. The session paces
    /// itself on this: the caller advances to the next target when it flips true (with its own
    /// minimum display time), not on a timer.
    /// <para>
    /// In-band, not elapsed: a user whose sustained holds are ~0.25 s cannot fill a fixed window, but
    /// can accumulate the same samples across several shorter holds. Wandering simply does not
    /// advance the count, so the target waits instead of collecting rubbish.
    /// </para>
    /// </summary>
    public bool IsComplete => HasArrived && Volatile.Read(ref _count) >= _maxSamples;

    private static long DefaultNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationPointCollector"/> class.
    /// </summary>
    /// <param name="target">The screen position of the calibration target in physical pixels.</param>
    /// <param name="settleWindowMs">Time in milliseconds to discard samples after gaze first arrives.</param>
    /// <param name="maxSamples">Number of post-settle samples to collect before complete.</param>
    /// <param name="bandRadiusPx">
    /// Distance from the target (physical px) within which gaze counts as being ON the target — for
    /// arrival AND for whether a post-arrival sample enters the statistics. Generous by design:
    /// pre-calibration accuracy is exactly what is being measured. Callers should take it from
    /// <see cref="CalibrationGeometry.BandRadiusPx"/> so the UI draws the same number.
    /// </param>
    /// <param name="stabilityRadiusPx">
    /// Bounding-box size (physical px) under which a full <paramref name="stabilitySampleCount"/>
    /// window counts as fixated. Since 2026-09-13 this path <em>also</em> requires the cluster to sit
    /// inside <paramref name="bandRadiusPx"/> of the target. It used to accept a tight cluster
    /// anywhere on screen, on the reasoning that a systematically offset user is still fixating — but
    /// that is indistinguishable from a user steadily looking at the wrong thing, and it is how the
    /// 2026-09-12 top-left target certified arrival and finished with a mean 911 px away.
    /// </param>
    /// <param name="stabilitySampleCount">Recent samples the stability window considers.</param>
    /// <param name="nowMs">Clock in Unix milliseconds; injectable for tests.</param>
    public CalibrationPointCollector(
        PhysicalPoint target,
        int settleWindowMs,
        int maxSamples = int.MaxValue,
        double bandRadiusPx = CalibrationGeometry.MinimumBandRadiusPx,
        double stabilityRadiusPx = 60,
        int stabilitySampleCount = 5,
        Func<long>? nowMs = null)
    {
        _target = target;
        _settleWindowMs = settleWindowMs;
        _maxSamples = maxSamples <= 0 ? int.MaxValue : maxSamples;
        _bandRadiusPx = bandRadiusPx;
        _stabilityRadiusPx = stabilityRadiusPx;
        _stabilitySampleCount = Math.Max(2, stabilitySampleCount);
        _nowMs = nowMs ?? DefaultNowMs;
    }

    /// <summary>Adds a raw gaze sample. Before arrival, samples only feed arrival detection; the arriving sample itself is discarded (saccade landing).</summary>
    /// <param name="gaze">Raw gaze position in physical screen pixels.</param>
    public void AddRawSample(PhysicalPoint gaze)
    {
        if (_ended) return;
        var now = _nowMs();

        if (!HasArrived)
        {
            if (IsInBand(gaze))
            {
                Arrive(now);
                return;
            }

            _recent.Enqueue(gaze);
            while (_recent.Count > _stabilitySampleCount)
                _recent.Dequeue();
            // Tight AND near. Dropping the second condition is what let a steady gaze hundreds of px
            // from the target be certified as having arrived on it.
            if (_recent.Count == _stabilitySampleCount && IsTightCluster() && IsInBand(ClusterCentroid()))
                Arrive(now);
            return;
        }

        if (now - _arrivedAtMs < _settleWindowMs) return;

        // STOP COUNTING once the target is satisfied. Everything below this line is bookkeeping about
        // an attempt that is already over: the target stays on screen until the session advances it
        // (TargetHoldMs, then the dispatcher), and a user doing exactly the right thing — still
        // looking at the finished dot — goes on producing samples the whole time.
        //
        // Counting those as "attempted" was a bug with teeth, because _count is frozen at the cap
        // while the denominator keeps climbing: coverage decays from 100 % towards zero purely as a
        // function of how long the target lingers. Driven against the real collector with a user who
        // NEVER leaves the band, it measured 60 % at shipped defaults, 34 % at 60 Hz, 23 % at 90 Hz,
        // and 16 % with TargetHoldMs at its maximum — so the coverage gate would have rejected a
        // flawless calibration on a faster tracker, and the blame would have landed on the user.
        //
        // A sample arriving after completion was never a candidate for inclusion, so it belongs in
        // neither half of the ratio.
        if (Volatile.Read(ref _count) >= _maxSamples) return;

        // The denominator: every sample that COULD have been included, in band or not. Counted before
        // the band test, because its job is to record what the user did rather than what survived it.
        Interlocked.Increment(ref _attempted);

        // The gate the 2026-09-12 session did not have. Outside the band the sample is observed and
        // discarded — it still lowers Coverage, which is how a wandering target stays visible instead
        // of quietly producing a tight σ over whatever few samples happened to land.
        if (!IsInBand(gaze)) return;

        _samples.Add(gaze);
        Interlocked.Increment(ref _count);
    }

    private bool IsInBand(PhysicalPoint gaze) => DistanceToTarget(gaze) <= _bandRadiusPx;

    private PhysicalPoint ClusterCentroid()
    {
        double sumX = 0, sumY = 0;
        foreach (var p in _recent) { sumX += p.X; sumY += p.Y; }
        return new PhysicalPoint(sumX / _recent.Count, sumY / _recent.Count);
    }

    private void Arrive(long now)
    {
        _recent.Clear();
        _arrivedAtMs = now;
    }

    private double DistanceToTarget(PhysicalPoint gaze)
    {
        var dx = gaze.X - _target.X;
        var dy = gaze.Y - _target.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private bool IsTightCluster()
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in _recent)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return (maxX - minX) <= _stabilityRadiusPx && (maxY - minY) <= _stabilityRadiusPx;
    }

    /// <summary>Ends collection and computes a <see cref="CalibrationPoint"/> with mean, standard deviation, and sample counts.</summary>
    /// <returns>The <see cref="CalibrationPoint"/> representing the collected data for this target.</returns>
    /// <exception cref="CalibrationFailedException">Thrown when no in-band samples were collected, carrying the typed failure.</exception>
    public CalibrationPoint End()
    {
        _ended = true;
        var samples = _samples.ToArray();
        if (samples.Length == 0)
        {
            // Typed, not textual: the two causes need opposite responses — a target never reached is a
            // pacing/aim problem, one reached but never held is a stillness problem — and the
            // calibration window renders each from its code. "No samples collected" said neither.
            throw new CalibrationFailedException(
                HasArrived
                    ? new CalibrationFailure(CalibrationFailureCode.TargetNeverHeld, _bandRadiusPx, AttemptedSamples)
                    : new CalibrationFailure(CalibrationFailureCode.TargetNeverReached));
        }

        var n = samples.Length;
        var meanX = samples.Average(s => s.X);
        var meanY = samples.Average(s => s.Y);
        var stdX = Math.Sqrt(samples.Sum(s => (s.X - meanX) * (s.X - meanX)) / n);
        var stdY = Math.Sqrt(samples.Sum(s => (s.Y - meanY) * (s.Y - meanY)) / n);

        return new CalibrationPoint(
            _target, new PhysicalPoint(meanX, meanY), stdX, stdY, n, AttemptedSamples);
    }
}
