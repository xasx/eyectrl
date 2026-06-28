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
        if (!_settings.FatigueMonitorEnabled || !sample.IsValid)
        {
            Score = 0.0;
            return;
        }

        _samples.Enqueue((sample.TimestampMicroseconds, sample.X, sample.Y));
        // Evict samples outside the window
        var cutoff = sample.TimestampMicroseconds - _windowMicroseconds;
        while (_samples.Count > 0 && _samples.Peek().ts < cutoff)
            _samples.Dequeue();

        if (_samples.Count < 2) { Score = 0.0; return; }

        // ── Signal 1: sustained-fixation ratio ──
        // % of samples within _fixationRadiusPixels of the window centroid.
        var arr = _samples.ToArray();
        var cx = arr.Average(s => s.x);
        var cy = arr.Average(s => s.y);
        var rSq = _fixationRadiusPixels * _fixationRadiusPixels;
        var inFixation = arr.Count(s => ((s.x - cx) * (s.x - cx) + (s.y - cy) * (s.y - cy)) <= rSq);
        var fixationRatio = (double)inFixation / arr.Length;

        // ── Signal 2: saccade-range shrink ──
        // Bounding-box area normalized; small box = eyes locked in.
        var minX = arr.Min(s => s.x); var maxX = arr.Max(s => s.x);
        var minY = arr.Min(s => s.y); var maxY = arr.Max(s => s.y);
        var boxArea = (maxX - minX) * (maxY - minY);
        // Normalize: assume a "healthy" box of ~1,000,000 sq px (1000x1000); shrink → higher score.
        var shrinkScore = 1.0 - Math.Clamp(boxArea / 1_000_000.0, 0.0, 1.0);

        // Weighted blend (fixation ratio weighted higher — more reliable signal).
        Score = Math.Clamp(0.7 * fixationRatio + 0.3 * shrinkScore, 0.0, 1.0);
    }

    /// <summary>Resets the monitor (call on break completion or settings toggle).</summary>
    public void Reset()
    {
        _samples.Clear();
        Score = 0.0;
    }
}
