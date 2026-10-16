namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class PhysicalRectGeometryTests
{
    [Fact]
    public void Contains_Point_Inline_With_DoubleOverload()
    {
        var r = new PhysicalRect(10, 10, 20, 20);
        Check.That(r.Contains(new PhysicalPoint(20, 20))).IsTrue();
        Check.That(r.Contains(new PhysicalPoint(9, 20))).IsFalse();
    }

    [Fact]
    public void IntersectsWith_TouchingEdges_DoNotIntersect()
    {
        // STRICT on purpose: D17's collision check must not fire on tangency (P1) —
        // a panel whose edge exactly kisses the keyboard's edge is not "over" it.
        var a = new PhysicalRect(0, 0, 10, 10);
        Check.That(a.IntersectsWith(new PhysicalRect(10, 0, 10, 10))).IsFalse();
        Check.That(a.IntersectsWith(new PhysicalRect(9.9, 0, 10, 10))).IsTrue();
    }

    [Fact]
    public void FromCenter_CentersTheRect()
    {
        var r = PhysicalRect.FromCenter(new PhysicalPoint(100, 50), 20, 10);
        Check.That(r.X).IsCloseTo(90, 1e-9);
        Check.That(r.Y).IsCloseTo(45, 1e-9);
    }

    [Fact]
    public void ExistingMembers_StillPresent()
    {
        var r = new PhysicalRect(0, 0, 10, 10);
        Check.That(r.CenterX).IsCloseTo(5, 1e-9);
        Check.That(r.Contains(5, 5)).IsTrue();
    }
}
