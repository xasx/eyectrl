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
}
