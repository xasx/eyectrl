namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DwellTargetUpdateTests
{
    [Fact]
    public void UpdateTargetDwellTime_ReEvaluatesWithoutResettingProgress()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter (EnteredTimestamp = 0)
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 150 ms of 300 ms — not yet activated
        Check.That(activated).IsFalse();

        d.UpdateTargetDwellTime("t", 100);               // shorten to 100 ms; must NOT reset the timer
        d.Update(new PhysicalPoint(100, 100), 160_000);  // elapsed 160 ms >= new 100 ms → activates

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_EnlargingMidDwell_PreservesProgress()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.Update(new PhysicalPoint(100, 100), 0);        // enter
        d.Update(new PhysicalPoint(100, 100), 150_000);  // 50% progress
        d.UpdateTargetGeometry("t", 60);                 // enlarge radius; must preserve EnteredTimestamp
        d.Update(new PhysicalPoint(100, 100), 300_000);  // reaches 300 ms → activates (progress not reset)

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_GrowsTheCircularHitArea()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        // (140,100) is 40 px out — outside the base radius of 30.
        d.Update(new PhysicalPoint(140, 100), 0);
        d.Update(new PhysicalPoint(140, 100), 200_000);
        Check.That(activated).IsFalse();

        d.UpdateTargetGeometry("t", 20); // radius 30 → 50, now covers the point
        d.Update(new PhysicalPoint(140, 100), 300_000);
        d.Update(new PhysicalPoint(140, 100), 500_000);

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_GrowsRectangularBounds_NotJustTheRadius()
    {
        // Regression: HitTest ignores Radius whenever Bounds is set, so a radius-only update was a
        // silent no-op for every rect target (ExternalDwellService registers rects). The Phase-1b
        // target-enlargement knob would have appeared to work while doing nothing on those surfaces.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("r", new PhysicalRect(100, 100, 40, 40), dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        // (150,120) is 10 px right of the rect's right edge (x = 140).
        d.Update(new PhysicalPoint(150, 120), 0);
        d.Update(new PhysicalPoint(150, 120), 200_000);
        Check.That(activated).IsFalse();

        d.UpdateTargetGeometry("r", 20); // outset 20 px per side → right edge 160
        d.Update(new PhysicalPoint(150, 120), 300_000);
        d.Update(new PhysicalPoint(150, 120), 500_000);

        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_IsIdempotent_NotCumulative()
    {
        // Phase 1b re-applies a computed margin every tick; compounding would inflate targets without
        // bound. The margin is always relative to the as-registered extents.
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        for (int i = 0; i < 10; i++)
            d.UpdateTargetGeometry("t", 10); // radius must settle at 40, not 30 + 10*10

        // 45 px out: outside radius 40, so ten applications did not compound to 130.
        d.Update(new PhysicalPoint(145, 100), 0);
        d.Update(new PhysicalPoint(145, 100), 200_000);
        Check.That(activated).IsFalse();

        // 35 px out: inside radius 40, so the single margin did apply.
        d.Update(new PhysicalPoint(135, 100), 300_000);
        d.Update(new PhysicalPoint(135, 100), 500_000);
        Check.That(activated).IsTrue();
    }

    [Fact]
    public void UpdateTargetGeometry_ZeroMargin_RestoresAsRegisteredGeometry()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 100));
        bool activated = false;
        d.TargetActivated += _ => activated = true;

        d.UpdateTargetGeometry("t", 40); // grow to 70
        d.UpdateTargetGeometry("t", 0);  // back to the registered 30

        d.Update(new PhysicalPoint(150, 100), 0); // 50 px out — outside the restored radius
        d.Update(new PhysicalPoint(150, 100), 200_000);

        Check.That(activated).IsFalse();
    }

    [Fact]
    public void UpdateTargetGeometry_NegativeMarginBeyondSize_ClampsInsteadOfInverting()
    {
        var d = new DwellDetector();
        d.AddTarget(new DwellTarget("r", new PhysicalRect(100, 100, 40, 40), dwellTimeMs: 100));

        d.UpdateTargetGeometry("r", -500); // must not produce a negative-extent rect

        bool activated = false;
        d.TargetActivated += _ => activated = true;
        d.Update(new PhysicalPoint(120, 120), 0); // the former center
        d.Update(new PhysicalPoint(120, 120), 200_000);

        // Degenerate (zero-area) rect at the center still hit-tests its own corner point; the contract
        // being pinned is "no inversion / no crash", not a particular hit outcome.
        Check.That(activated).IsTrue();
    }
}
