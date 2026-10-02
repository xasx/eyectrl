namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellDetectorNearestTests
{
    private static DwellDetector TwoTargets(out string leftId, out string rightId)
    {
        var detector = new DwellDetector(400, EyeCtrl.Core.Adaptation.DwellContext.Other, sink: null);
        leftId = "left";
        rightId = "right";
        detector.AddTarget(new DwellTarget(leftId, new EyeCtrl.Core.Models.PhysicalPoint(0, 0), 100, 600));
        detector.AddTarget(new DwellTarget(rightId, new EyeCtrl.Core.Models.PhysicalPoint(240, 0), 100, 600));
        return detector;
    }

    [Fact]
    public void NearestHittingTargetId_PicksTheNearestHittingTarget()
    {
        var detector = TwoTargets(out var leftId, out var rightId);

        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(30, 0))).IsEqualTo(leftId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(210, 0))).IsEqualTo(rightId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(120, 500))).IsNull();
    }

    [Fact]
    public void NearestHittingTargetId_HonoursTheFilter()
    {
        var detector = TwoTargets(out var leftId, out _);

        var nearest = detector.NearestHittingTargetId(
            new EyeCtrl.Core.Models.PhysicalPoint(30, 0), t => t.Id != leftId);

        Check.That(nearest).IsNull(); // left excluded and right not hit
    }

    [Fact]
    public void TargetHit_ReportsRegionMembership()
    {
        var detector = TwoTargets(out var leftId, out _);

        Check.That(detector.TargetHit(leftId, new EyeCtrl.Core.Models.PhysicalPoint(50, 0))).IsTrue();
        Check.That(detector.TargetHit(leftId, new EyeCtrl.Core.Models.PhysicalPoint(500, 0))).IsFalse();
        Check.That(detector.TargetHit("missing", new EyeCtrl.Core.Models.PhysicalPoint(0, 0))).IsFalse();
    }

    [Fact]
    public void NearestHittingTargetId_AgreesWithUpdate_Selection()
    {
        // Drive Update along a path and confirm the API answers what Update acts on:
        // accumulate on the left target via Update, then confirm the API picks it too.
        var detector = TwoTargets(out var leftId, out _);
        var fired = new List<string>();
        detector.TargetActivated += id => fired.Add(id);

        for (var t = 0; t < 10; t++)
            detector.Update(new EyeCtrl.Core.Models.PhysicalPoint(30, 0), t * 100_000);

        Check.That(fired).ContainsExactly(leftId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(30, 0))).IsEqualTo(leftId);
    }

    [Fact]
    public void NearestHittingTargetId_AgreesWithUpdate_OnAnExactEquidistantTie()
    {
        // Two targets, gaze at the exact midpoint (integer coords ⇒ a real tie). The winner is
        // whatever the detector's own order produces — the API must agree with Update, not pick
        // its own winner.
        var detector = new DwellDetector(400, EyeCtrl.Core.Adaptation.DwellContext.Other, sink: null);
        detector.AddTarget(new DwellTarget("a", new EyeCtrl.Core.Models.PhysicalPoint(0, 0), 100, 600));
        detector.AddTarget(new DwellTarget("b", new EyeCtrl.Core.Models.PhysicalPoint(200, 0), 100, 600));
        var fired = new List<string>();
        detector.TargetActivated += id => fired.Add(id);

        for (var t = 0; t < 10; t++)
            detector.Update(new EyeCtrl.Core.Models.PhysicalPoint(100, 0), t * 100_000);

        var updateWinner = fired.Single();
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(100, 0))).IsEqualTo(updateWinner);
    }
}
