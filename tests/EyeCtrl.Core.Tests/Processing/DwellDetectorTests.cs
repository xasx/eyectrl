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

        // "Resets progress" is a TargetProgress claim, not just an activated-vs-not one — pin it by
        // actually subscribing. Previously this test's only assertion was Check.That(activated).
        // IsFalse(), which passes even if the leave sample at line 253 of DwellDetector never fired
        // a 0 event at all; nothing here would have caught that regression.
        var progressEvents = new List<double>();
        detector.TargetProgress += (id, progress) => progressEvents.Add(progress);

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 30_000);
        detector.Update(new PhysicalPoint(100, 100), 60_000);
        detector.Update(new PhysicalPoint(500, 500), 90_000); // leave
        detector.Update(new PhysicalPoint(100, 100), 120_000);
        detector.Update(new PhysicalPoint(100, 100), 150_000);
        detector.Update(new PhysicalPoint(100, 100), 180_000);

        Check.That(activated).IsFalse();
        Check.WithCustomMessage("The leave sample at t=90_000 must reset progress to 0.")
            .That(progressEvents[2]).IsEqualTo(0.0);
    }

    [Fact]
    public void GetProgress_ReturnsDwellFraction()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300);
        detector.AddTarget(target);

        // Sampled continuously: a single 150 ms step is exactly the gap clamp's bound and would
        // restart the dwell instead of accumulating.
        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 100_000);
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

    [Fact]
    public void Update_WithFilter_ExcludedTargetNeverAccumulates()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("blocked", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        long time = 0;
        for (int i = 0; i < 20; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), time, targetFilter: _ => false);
            time += 30_000;
        }

        Check.That(activated).IsFalse();
        Check.That(detector.GetProgress("blocked")).IsEqualTo(0);
    }

    [Fact]
    public void Update_WithFilter_NearestEligibleWins_OverCloserIneligible()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("near", new PhysicalPoint(100, 100), radius: 60, dwellTimeMs: 200));
        detector.AddTarget(new DwellTarget("far", new PhysicalPoint(140, 100), radius: 60, dwellTimeMs: 200));

        string? activatedId = null;
        detector.TargetActivated += id => activatedId = id;

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(105, 100), time, targetFilter: t => t.Id == "far");
            time += 30_000;
        }

        Check.That(activatedId).IsEqualTo("far");
    }

    [Fact]
    public void Update_FilterFlipsMidDwell_ExcludedTargetStateResets()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 150_000);
        detector.Update(new PhysicalPoint(100, 100), 180_000, targetFilter: _ => false);
        detector.Update(new PhysicalPoint(100, 100), 210_000);
        detector.Update(new PhysicalPoint(100, 100), 240_000);

        Check.That(activated).IsFalse();
    }

    [Fact]
    public void Update_TwoArgOverload_StillActivates()
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));
        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        // Continuous cadence: the two-sample version relied on an unbounded 250 ms step, which the
        // gap clamp now restarts.
        long t = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new PhysicalPoint(100, 100), t);
            t += 30_000;
        }

        Check.That(activated).IsTrue();
    }

    /// <summary>
    /// Pins the clamp's boundary: a silence shorter than <c>MaxCreditedSampleGapUs</c> resumes (the
    /// anti-over-clamp guard), and the bound itself restarts. The comparison is <c>&gt;=</c>,
    /// matching <c>GapInterpolator</c>'s own sequence-break comparison at
    /// <c>CursorSettings.MaxGapInterpolationMs</c>'s 150 ms default — a silence the engine refuses
    /// to hold must not complete a dwell. 150 is that constant in milliseconds, kept literal so
    /// this pin fails loudly if the bound moves.
    /// </summary>
    [Theory]
    [InlineData(149, true)]
    [InlineData(150, false)]
    public void GapBoundary_UnderResumes_AtBoundRestarts(int gapMs, bool fires)
    {
        var detector = new DwellDetector();
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 500));
        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        // 390 ms of held gaze at 30 ms cadence, then one step of gapMs.
        long lastDelivered = 0;
        for (int i = 0; i < 14; i++)
        {
            lastDelivered = i * 30_000L;
            detector.Update(new PhysicalPoint(100, 100), lastDelivered);
        }

        detector.Update(new PhysicalPoint(100, 100), lastDelivered + (gapMs * 1000L));

        // 390 + 149 credits to 539 ms and fires; 390 + 150 restarts and cannot.
        Check.That(activated).IsEqualTo(fires);
    }
}
