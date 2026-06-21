namespace EyeCtrl.Core.Tests.Processing;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellDetectorTests
{
    [Fact]
    public void Update_DwellOnTarget_RaisesActivated()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        string? activatedId = null;
        detector.TargetActivated += (id) => { activated = true; activatedId = id; };

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(105, 105), time);
            time += 30_000;
        }

        Check.That(activated).IsTrue();
        Check.That(activatedId).IsEqualTo("test");
    }

    [Fact]
    public void Update_GazeLeavesTarget_ResetsProgress()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        detector.TargetActivated += (id) => activated = true;

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 30_000);
        detector.Update(new PhysicalPoint(100, 100), 60_000);
        detector.Update(new PhysicalPoint(500, 500), 90_000); // leave
        detector.Update(new PhysicalPoint(100, 100), 120_000);
        detector.Update(new PhysicalPoint(100, 100), 150_000);
        detector.Update(new PhysicalPoint(100, 100), 180_000);

        Check.That(activated).IsFalse();
    }

    [Fact]
    public void GetProgress_ReturnsDwellFraction()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300);
        detector.AddTarget(target);

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 150_000);

        double progress = detector.GetProgress("test");
        Check.That(progress >= 0.4 && progress <= 0.6).IsTrue();
    }

    [Fact]
    public void Update_OverlappingTargets_OnlyNearestAccumulatesDwell()
    {
        var detector = new DwellDetector();
        // Two targets at (100,100) and (130,100) with radius 40 — they overlap
        var targetA = new DwellTarget("A", new PhysicalPoint(100, 100), radius: 40, dwellTimeMs: 200);
        var targetB = new DwellTarget("B", new PhysicalPoint(130, 100), radius: 40, dwellTimeMs: 200);
        detector.AddTarget(targetA);
        detector.AddTarget(targetB);

        var progressIds = new List<string>();
        detector.TargetProgress += (id, progress) =>
        {
            if (progress > 0) progressIds.Add(id);
        };

        // Gaze at (110, 100) — inside both circles, but closer to A (dist=10) than B (dist=20)
        long time = 0;
        for (int i = 0; i < 5; i++)
        {
            detector.Update(new PhysicalPoint(110, 100), time);
            time += 30_000;
        }

        // Only A should have received positive progress
        Check.That(progressIds).Contains("A");
        Check.That(progressIds.Contains("B")).IsFalse();
    }

    [Fact]
    public void Update_WithinCooldown_DoesNotRetrigger()
    {
        var detector = new DwellDetector(cooldownMs: 400);
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        long time = 0;

        // First dwell: gaze enters, accumulate 200ms → activates
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000; // 30ms per frame
        }
        Check.That(activations).HasSize(1);

        // Continue gazing within cooldown (400ms = 400_000us from activation)
        // Activation happened around time ~240_000. Cooldown ends at ~640_000.
        // We're now at time ~240_000. Send updates for another 300ms — should NOT re-trigger.
        for (int i = 0; i < 10; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }

        Check.That(activations).HasSize(1); // still only one
    }

    [Fact]
    public void Update_AfterCooldown_CanRetrigger()
    {
        var detector = new DwellDetector(cooldownMs: 400);
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        long time = 0;

        // First dwell: accumulate 200ms → activates
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }
        Check.That(activations).HasSize(1);

        // Jump past cooldown period (400ms beyond activation)
        time += 500_000; // 500ms jump — well past cooldown

        // Second dwell: accumulate another 200ms → should activate again
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }

        Check.That(activations.Count).IsEqualTo(2);
    }

    [Fact]
    public void Update_ZeroCooldown_AllowsImmediateRetrigger()
    {
        var detector = new DwellDetector(cooldownMs: 0);
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        var activations = new List<string>();
        detector.TargetActivated += id => activations.Add(id);

        long time = 0;

        // First dwell
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }
        Check.That(activations).HasSize(1);

        // Immediately continue — with 0 cooldown, should re-trigger after another 200ms
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time);
            time += 30_000;
        }

        Check.That(activations.Count).IsEqualTo(2);
    }
}
