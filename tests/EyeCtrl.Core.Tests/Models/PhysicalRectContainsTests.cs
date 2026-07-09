namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using NFluent;

public class PhysicalRectContainsTests
{
    // Rect at (100,200) size 50x40 → spans x[100,150], y[200,240]
    private static readonly PhysicalRect Rect = new(100, 200, 50, 40);

    [Fact]
    public void Contains_InteriorPoint_True()
    {
        Check.That(Rect.Contains(125, 220)).IsTrue();
    }

    [Fact]
    public void Contains_TopLeftCorner_True_Inclusive()
    {
        Check.That(Rect.Contains(100, 200)).IsTrue();
    }

    [Fact]
    public void Contains_BottomRightCorner_True_Inclusive()
    {
        Check.That(Rect.Contains(150, 240)).IsTrue();
    }

    [Fact]
    public void Contains_PointLeftOfRect_False()
    {
        Check.That(Rect.Contains(99.9, 220)).IsFalse();
    }

    [Fact]
    public void Contains_PointAboveRect_False()
    {
        Check.That(Rect.Contains(125, 199.9)).IsFalse();
    }

    [Fact]
    public void Contains_PointRightOfRect_False()
    {
        Check.That(Rect.Contains(150.1, 220)).IsFalse();
    }

    [Fact]
    public void Contains_PointBelowRect_False()
    {
        Check.That(Rect.Contains(125, 240.1)).IsFalse();
    }

    [Fact]
    public void Contains_ZeroSizedRect_ContainsOnlyItsOrigin()
    {
        var zero = new PhysicalRect(10, 10, 0, 0);
        Check.That(zero.Contains(10, 10)).IsTrue();
        Check.That(zero.Contains(10.1, 10)).IsFalse();
    }
}
