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
    public void Disabled_NeverRaisesScore()
    {
        var cfg = new EyeCareSettings { FatigueMonitorEnabled = false };
        var monitor = new FatigueMonitor(cfg, fixationRadiusPixels: 50, windowSeconds: 5);

        for (int i = 0; i < 100; i++)
            monitor.OnSample(new GazePoint(500, 500, i * 50_000L, IsValid: true));

        Check.That(monitor.Score).IsEqualTo(0.0);
    }
}
