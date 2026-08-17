namespace EyeCtrl.Core.Calibration;

using System.Collections.Concurrent;
using EyeCtrl.Core.Models;

/// <summary>
/// Collects raw gaze samples for a single calibration target and computes the mean and standard
/// deviation on completion.
/// <para>
/// Collection is gated on <b>arrival</b>: samples only count once the user has actually reached
/// the target — either gaze enters the acceptance radius around the target, or gaze becomes
/// stable (a tight recent cluster, wherever it sits, which covers a systematically offset but
/// fixating user). The settle window starts at ARRIVAL, not at target creation, and the sample
/// that first establishes arrival is discarded with it (it is the landing of the saccade). This
/// is what makes calibration move at the user's pace: before this, samples were counted from a
/// timer with no notion of whether the user had found the point yet (2026-08-17 field report:
/// "calibration is not usable, the points should move at the user's pace").
/// </para>
/// </summary>
public class CalibrationPointCollector
{
    private readonly PhysicalPoint _target;
    private readonly long _settleWindowMs;
    private readonly int _maxSamples;
    private readonly double _acceptanceRadiusPx;
    private readonly double _stabilityRadiusPx;
    private readonly int _stabilitySampleCount;
    private readonly Func<long> _nowMs;
    private readonly Queue<PhysicalPoint> _recent = new();
    private readonly ConcurrentBag<PhysicalPoint> _samples = new();
    private long _arrivedAtMs = -1;
    private int _count;
    private volatile bool _ended;

    /// <summary>Whether the collector is still accepting new samples.</summary>
    public bool IsCollecting => !_ended;

    /// <summary>Whether gaze has reached the target (proximity or stability). Settle runs from here.</summary>
    public bool HasArrived => Volatile.Read(ref _arrivedAtMs) >= 0;

    /// <summary>
    /// Whether enough post-settle samples have been collected. The session paces itself on this:
    /// the caller advances to the next target when it flips true (with its own minimum display
    /// time), not on a timer.
    /// </summary>
    public bool IsComplete => HasArrived && Volatile.Read(ref _count) >= _maxSamples;

    private static long DefaultNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Initializes a new instance of the <see cref="CalibrationPointCollector"/> class.
    /// </summary>
    /// <param name="target">The screen position of the calibration target in physical pixels.</param>
    /// <param name="settleWindowMs">Time in milliseconds to discard samples after gaze first arrives.</param>
    /// <param name="maxSamples">Number of post-settle samples to collect before complete.</param>
    /// <param name="acceptanceRadiusPx">
    /// Distance from the target (physical px) within which gaze counts as arrived. Generous by
    /// design: pre-calibration accuracy is exactly what is being measured.
    /// </param>
    /// <param name="stabilityRadiusPx">
    /// Bounding-box size (physical px) under which a full <paramref name="stabilitySampleCount"/>
    /// window of samples anywhere on screen counts as fixated — the user steadily looking where
    /// they believe the point is, however far raw gaze is offset.
    /// </param>
    /// <param name="stabilitySampleCount">Recent samples the stability window considers.</param>
    /// <param name="nowMs">Clock in Unix milliseconds; injectable for tests.</param>
    public CalibrationPointCollector(
        PhysicalPoint target,
        int settleWindowMs,
        int maxSamples = int.MaxValue,
        double acceptanceRadiusPx = 120,
        double stabilityRadiusPx = 60,
        int stabilitySampleCount = 5,
        Func<long>? nowMs = null)
    {
        _target = target;
        _settleWindowMs = settleWindowMs;
        _maxSamples = maxSamples <= 0 ? int.MaxValue : maxSamples;
        _acceptanceRadiusPx = acceptanceRadiusPx;
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
            if (DistanceToTarget(gaze) <= _acceptanceRadiusPx)
            {
                Arrive(now);
                return;
            }

            _recent.Enqueue(gaze);
            while (_recent.Count > _stabilitySampleCount)
                _recent.Dequeue();
            if (_recent.Count == _stabilitySampleCount && IsTightCluster())
                Arrive(now);
            return;
        }

        if (now - _arrivedAtMs < _settleWindowMs) return;
        // Cap at _maxSamples (honors CalibrationSettings.SamplesPerPoint). A tiny
        // overshoot under concurrency is harmless — mean/std are unaffected.
        if (Volatile.Read(ref _count) >= _maxSamples) return;
        _samples.Add(gaze);
        Interlocked.Increment(ref _count);
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

    /// <summary>Ends collection and computes a <see cref="CalibrationPoint"/> with mean, standard deviation, and sample count.</summary>
    /// <returns>The <see cref="CalibrationPoint"/> representing the collected data for this target.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no samples were collected after arrival.</exception>
    public CalibrationPoint End()
    {
        _ended = true;
        var samples = _samples.ToArray();
        if (samples.Length == 0)
            throw new InvalidOperationException("No samples collected after arrival.");

        var n = samples.Length;
        var meanX = samples.Average(s => s.X);
        var meanY = samples.Average(s => s.Y);
        var stdX = Math.Sqrt(samples.Sum(s => (s.X - meanX) * (s.X - meanX)) / n);
        var stdY = Math.Sqrt(samples.Sum(s => (s.Y - meanY) * (s.Y - meanY)) / n);

        return new CalibrationPoint(_target, new PhysicalPoint(meanX, meanY), stdX, stdY, n);
    }
}
