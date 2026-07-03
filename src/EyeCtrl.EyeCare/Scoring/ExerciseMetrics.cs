namespace EyeCtrl.EyeCare.Scoring;

using EyeCtrl.Core.Models;

/// <summary>Accumulates per-frame gaze-vs-target deltas, then computes an <see cref="ExerciseResult"/>.</summary>
public sealed class ExerciseMetrics
{
    private readonly List<(double dx, double dy)> _deltas = new();
    private readonly List<long> _reactionTimesUs = new();

    /// <summary>Records one gaze-vs-target sample pair.</summary>
    public void RecordDelta(PhysicalPoint gaze, PhysicalPoint target)
    {
        _deltas.Add((gaze.X - target.X, gaze.Y - target.Y));
    }

    /// <summary>Records one reaction-time sample (microseconds from target appearance to gaze acquisition).</summary>
    public void RecordReactionTime(long latencyMicroseconds) => _reactionTimesUs.Add(latencyMicroseconds);

    /// <summary>Mean accuracy 0–100: the percentage of samples within the on-target radius.</summary>
    public double ComputeAccuracyPercent(double onTargetRadiusPixels)
    {
        if (_deltas.Count == 0) return 0;
        var rSq = onTargetRadiusPixels * onTargetRadiusPixels;
        var onTarget = _deltas.Count(d => (d.dx * d.dx + d.dy * d.dy) <= rSq);
        return 100.0 * onTarget / _deltas.Count;
    }

    /// <summary>Steadiness 0–5: lower gaze-to-target distance variance ⇒ steadier.</summary>
    public double ComputeSteadiness0To5()
    {
        if (_deltas.Count < 2) return 0;
        var mean = _deltas.Average(d => Math.Sqrt(d.dx * d.dx + d.dy * d.dy));
        // Map: 0px mean error → 5.0; 100px mean error → 0.0 (clamped). Tunable.
        var score = 5.0 * (1.0 - Math.Clamp(mean / 100.0, 0.0, 1.0));
        return score;
    }

    /// <summary>Mean reaction time in ms, or null if none recorded.</summary>
    public double? ComputeReactionTimeMs()
    {
        if (_reactionTimesUs.Count == 0) return null;
        return _reactionTimesUs.Average(us => us / 1000.0);
    }

    /// <summary>Number of recorded gaze samples.</summary>
    public int SampleCount => _deltas.Count;

    /// <summary>Clears all recorded samples so the metrics can be reused for a fresh run.</summary>
    public void Reset()
    {
        _deltas.Clear();
        _reactionTimesUs.Clear();
    }
}
