namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>Rolling fatigue score from gaze samples. Derives fixation/saccade structure from
/// raw positions (does NOT depend on GazeEngine's private FixationDetector). Gated off by default.</summary>
public sealed class FatigueMonitor
{
    private readonly EyeCareSettings _settings;
    private readonly double _fixationRadiusPixels;
    private readonly long _windowMicroseconds;
    private readonly Queue<(long ts, double x, double y)> _samples = new();
    private double _sumX;
    private double _sumY;

    public FatigueMonitor(EyeCareSettings settings, double fixationRadiusPixels, int windowSeconds)
    {
        _settings = settings;
        _fixationRadiusPixels = fixationRadiusPixels;
        _windowMicroseconds = windowSeconds * 1_000_000L;
        Score = 0.0;
    }

    /// <summary>Current 0.0–1.0 fatigue score.</summary>
    public double Score { get; private set; }

    /// <summary>Feeds one raw gaze sample.</summary>
    public void OnSample(GazePoint sample)
    {
        if (!_settings.FatigueMonitorEnabled)
        {
            Score = 0.0;
            return;
        }
        if (!sample.IsValid) return;

        _samples.Enqueue((sample.TimestampMicroseconds, sample.X, sample.Y));
        _sumX += sample.X;
        _sumY += sample.Y;

        var cutoff = sample.TimestampMicroseconds - _windowMicroseconds;
        while (_samples.Count > 0 && _samples.Peek().ts < cutoff)
        {
            var old = _samples.Dequeue();
            _sumX -= old.x;
            _sumY -= old.y;
        }

        if (_samples.Count < 2) { Score = 0.0; return; }

        var count = _samples.Count;
        var cx = _sumX / count;
        var cy = _sumY / count;
        var rSq = _fixationRadiusPixels * _fixationRadiusPixels;

        int inFixation = 0;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var s in _samples)
        {
            if (((s.x - cx) * (s.x - cx) + (s.y - cy) * (s.y - cy)) <= rSq)
                inFixation++;
            if (s.x < minX) minX = s.x;
            if (s.x > maxX) maxX = s.x;
            if (s.y < minY) minY = s.y;
            if (s.y > maxY) maxY = s.y;
        }

        var fixationRatio = (double)inFixation / count;
        var boxArea = (maxX - minX) * (maxY - minY);
        var shrinkScore = 1.0 - Math.Clamp(boxArea / 1_000_000.0, 0.0, 1.0);

        Score = Math.Clamp(0.7 * fixationRatio + 0.3 * shrinkScore, 0.0, 1.0);
    }

    /// <summary>Resets the monitor (call on break completion or settings toggle).</summary>
    public void Reset()
    {
        _samples.Clear();
        _sumX = 0;
        _sumY = 0;
        Score = 0.0;
    }
}
