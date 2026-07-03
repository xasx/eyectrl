namespace EyeCtrl.EyeCare.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class FatigueMonitorTests
{
    private static readonly EyeCareSettings Cfg = new()
    {
        FatigueMonitorEnabled = true,
        FatigueThreshold = 0.75
    };

    [Fact]
    public void SustainedStaring_RaisesScoreAboveThreshold()
    {
        var monitor = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);

        // 5 seconds of samples all within a tiny radius = staring
        for (int i = 0; i < 100; i++)
            monitor.OnSample(new GazePoint(500, 500, i * 50_000L, IsValid: true));

        Check.That(monitor.Score).IsStrictlyGreaterThan(0.75);
    }

    [Fact]
    public void NormalEyeMovement_KeepsScoreLow()
    {
        var monitor = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);

        // 5 seconds of samples spread across a wide area = healthy saccades
        var rnd = new Random(1);
        for (int i = 0; i < 100; i++)
        {
            var x = 200 + rnd.Next(1200);
            var y = 200 + rnd.Next(600);
            monitor.OnSample(new GazePoint(x, y, i * 50_000L, IsValid: true));
        }

        Check.That(monitor.Score).IsStrictlyLessThan(0.5);
    }

    [Fact]
    public void SingleInvalidSample_DoesNotResetScore()
    {
        var monitor = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        for (int i = 0; i < 100; i++)
            monitor.OnSample(new GazePoint(500, 500, i * 50_000L, IsValid: true));
        var before = monitor.Score;
        Check.That(before).IsStrictlyGreaterThan(0.0);

        monitor.OnSample(new GazePoint(0, 0, 100 * 50_000L, IsValid: false));
        Check.That(monitor.Score).IsEqualTo(before);
    }

    [Fact]
    public void ScoreAfterEviction_MatchesFreshRecompute()
    {
        var a = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 300)
            .Select(i => (x: 200.0 + rnd.Next(1200), y: 200.0 + rnd.Next(600), ts: i * 50_000L))
            .ToList();
        foreach (var p in pts)
            a.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));

        var cutoff = pts[^1].ts - 5_000_000L;
        var b = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        foreach (var p in pts.Where(p => p.ts >= cutoff))
            b.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));

        Check.That(a.Score).IsCloseTo(b.Score, 1e-9);
    }

    [Fact]
    public void ScoreAfterReset_MatchesFreshMonitor()
    {
        // Reset() must clear the running centroid sums (_sumX/_sumY), not just the sample queue.
        // If stale mass survives, the centroid is skewed and Score stays corrupted after a break.
        var used = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        for (int i = 0; i < 100; i++)
            used.OnSample(new GazePoint(1600, 950, i * 50_000L, IsValid: true)); // region A
        used.Reset();

        var rnd = new Random(3);
        var regionB = Enumerable.Range(0, 100)
            .Select(i => (x: 300.0 + rnd.Next(400), y: 300.0 + rnd.Next(400), ts: (200 + i) * 50_000L))
            .ToList();

        var fresh = new FatigueMonitor(Cfg, fixationRadiusPixels: 50, windowSeconds: 5);
        foreach (var p in regionB)
        {
            used.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));
            fresh.OnSample(new GazePoint(p.x, p.y, p.ts, IsValid: true));
        }

        Check.That(used.Score).IsCloseTo(fresh.Score, 1e-9);
    }

    [Fact]
    public void Disabled_NeverRaisesScore()
    {
        var cfg = new EyeCareSettings { FatigueMonitorEnabled = false };
        var monitor = new FatigueMonitor(cfg, fixationRadiusPixels: 50, windowSeconds: 5);

        for (int i = 0; i < 100; i++)
            monitor.OnSample(new GazePoint(500, 500, i * 50_000L, IsValid: true));

        Check.That(monitor.Score).IsEqualTo(0.0);
    }
}
