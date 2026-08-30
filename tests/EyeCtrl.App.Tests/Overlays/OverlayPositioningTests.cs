namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;

public class OverlayPositioningTests
{
    private static readonly Rect Area = new(0, 0, 1920, 1040);

    [Fact]
    public void TopEdge_CentersHorizontallyAtWorkAreaTop()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "top", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(0d);
    }

    [Fact]
    public void BottomEdge_CentersHorizontallyAtWorkAreaBottom()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "bottom", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(1040d - 60d);
    }

    [Fact]
    public void SideEdges_CenterVertically()
    {
        var (leftL, topL) = OverlayPositioning.PlaceAtEdge(Area, "left", 60, 400);
        Check.That(leftL).IsEqualTo(0d);
        Check.That(topL).IsEqualTo((1040 - 400) / 2d);

        var (leftR, topR) = OverlayPositioning.PlaceAtEdge(Area, "right", 60, 400);
        Check.That(leftR).IsEqualTo(1920d - 60d);
        Check.That(topR).IsEqualTo((1040 - 400) / 2d);
    }

    [Fact]
    public void EdgeCase_IsInsensitive_AndPascalCaseMatches()
    {
        Check.That(OverlayPositioning.PlaceAtEdge(Area, "TOP", 100, 50).Top).IsEqualTo(0d);
        // QuickActionsGridWindow passes PascalCase values ("Left"/"Right"/"Top"/"Bottom").
        Check.That(OverlayPositioning.PlaceAtEdge(Area, "Bottom", 100, 50).Top).IsEqualTo(1040d - 50d);
    }

    [Fact]
    public void UnknownEdge_FallsBackToTop()
    {
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, "diagonal", 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(0d);
    }

    [Fact]
    public void NullEdge_FallsBackToTop_WithoutThrowing()
    {
        // The signature is string? because the body has always matched on `edge?.ToLowerInvariant()`.
        // ToolbarWindow.SetMode leans on this for its own null-tolerant orientation match.
        var (left, top) = OverlayPositioning.PlaceAtEdge(Area, null, 400, 60);
        Check.That(left).IsEqualTo((1920 - 400) / 2d);
        Check.That(top).IsEqualTo(0d);
    }

    [Fact]
    public void NonZeroWorkAreaOrigin_IsHonored()
    {
        var area = new Rect(1920, 0, 1920, 1040); // secondary monitor to the right
        var (left, top) = OverlayPositioning.PlaceAtEdge(area, "top", 400, 60);
        Check.That(left).IsEqualTo(1920d + (1920d - 400d) / 2d);
        Check.That(top).IsEqualTo(0d);
    }
}
