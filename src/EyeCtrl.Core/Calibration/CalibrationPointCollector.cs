namespace EyeCtrl.Core.Calibration;

using System.Collections.Concurrent;
using EyeCtrl.Core.Models;

public class CalibrationPointCollector
{
    private readonly PhysicalPoint _target;
    private readonly long _settleWindowMs;
    private readonly int _maxSamples;
    private readonly long _createdAt;
    private readonly ConcurrentBag<PhysicalPoint> _samples = new();
    private int _count;
    private volatile bool _ended;

    public bool IsCollecting => !_ended;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public CalibrationPointCollector(PhysicalPoint target, int settleWindowMs, int maxSamples = int.MaxValue)
    {
        _target = target;
        _settleWindowMs = settleWindowMs;
        _maxSamples = maxSamples <= 0 ? int.MaxValue : maxSamples;
        _createdAt = NowMs();
    }

    public void AddRawSample(PhysicalPoint gaze)
    {
        if (_ended) return;
        var elapsed = NowMs() - _createdAt;
        if (elapsed < _settleWindowMs) return;
        // Cap at _maxSamples (honors CalibrationSettings.SamplesPerPoint). A tiny
        // overshoot under concurrency is harmless — mean/std are unaffected.
        if (Volatile.Read(ref _count) >= _maxSamples) return;
        _samples.Add(gaze);
        Interlocked.Increment(ref _count);
    }

    public CalibrationPoint End()
    {
        _ended = true;
        var samples = _samples.ToArray();
        if (samples.Length == 0)
            throw new InvalidOperationException("No samples collected after settle window.");

        var n = samples.Length;
        var meanX = samples.Average(s => s.X);
        var meanY = samples.Average(s => s.Y);
        var stdX = Math.Sqrt(samples.Sum(s => (s.X - meanX) * (s.X - meanX)) / n);
        var stdY = Math.Sqrt(samples.Sum(s => (s.Y - meanY) * (s.Y - meanY)) / n);

        return new CalibrationPoint(_target, new PhysicalPoint(meanX, meanY), stdX, stdY, n);
    }
}
