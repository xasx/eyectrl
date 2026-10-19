namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// T1, the placement gate (magnifier-capture-place-investigation fix, C1): the capture square is
/// always centred on P and is never translated back onto the screen. The old translate-clamp kept
/// the square's size and slid its origin, which moved P off the view centre near every edge — the
/// owner's "I see a part of the screen I wasn't looking at".
///
/// <para>
/// The five rows are the ticket's worked rows at the owner's geometry (3840×2400, r = 340). They
/// pin the origin (negative at edges), the on-screen source, and the destination offset; the
/// invariants on every row pin the rules independent of the table. Replacing the centred placement
/// with the old clamp turns every edge row RED.
/// </para>
/// </summary>
public class CapturePlacementTests
{
    private const int ScreenWidth = 3840;
    private const int ScreenHeight = 2400;
    private const int Radius = 340;

    [Theory]
    [InlineData(1920, 1200, 1580, 860, 1580, 860, 680, 680, 0, 0)]
    [InlineData(3803, 85, 3463, -255, 3463, 0, 377, 425, 0, 255)]
    [InlineData(3839, 2399, 3499, 2059, 3499, 2059, 341, 341, 0, 0)]
    [InlineData(0, 0, -340, -340, 0, 0, 340, 340, 340, 340)]
    [InlineData(20, 1200, -320, 860, 0, 860, 360, 680, 320, 0)]
    public void Place_KeepsTheSquareCentredOnP_AndClipsOnlyTheSource(
        int centerX,
        int centerY,
        int originX,
        int originY,
        int sourceX,
        int sourceY,
        int sourceW,
        int sourceH,
        int destX,
        int destY)
    {
        var placement = CaptureRegionMath.Place(centerX, centerY, Radius, ScreenWidth, ScreenHeight);

        Check.That(placement).IsNotNull();
        var p = placement!.Value;

        Check.WithCustomMessage($"P=({centerX},{centerY}): the square's origin must be P − r, not a clamped one.")
            .That(p.OriginX).IsEqualTo(originX);
        Check.That(p.OriginY).IsEqualTo(originY);
        Check.That(p.Size).IsEqualTo(2 * Radius);

        Check.That(p.SourceX).IsEqualTo(sourceX);
        Check.That(p.SourceY).IsEqualTo(sourceY);
        Check.That(p.SourceW).IsEqualTo(sourceW);
        Check.That(p.SourceH).IsEqualTo(sourceH);
        Check.That(p.DestX).IsEqualTo(destX);
        Check.That(p.DestY).IsEqualTo(destY);

        // The invariants the table must satisfy, checked independently of it.
        Check.WithCustomMessage("the source must lie inside the screen.").That(p.SourceX).IsGreaterOrEqualThan(0);
        Check.That(p.SourceY).IsGreaterOrEqualThan(0);
        Check.That(p.SourceX + p.SourceW).IsLessOrEqualThan(ScreenWidth);
        Check.That(p.SourceY + p.SourceH).IsLessOrEqualThan(ScreenHeight);
        Check.WithCustomMessage("the destination must be the source's offset inside the square.")
            .That(p.DestX).IsEqualTo(p.SourceX - p.OriginX);
        Check.That(p.DestY).IsEqualTo(p.SourceY - p.OriginY);

        // The whole point: the square's centre is P, so the displayed image is centred on P.
        Check.WithCustomMessage("the square's centre must be exactly P.")
            .That(CaptureRegionMath.CenterOf((p.OriginX, p.OriginY, p.Size)))
            .IsEqualTo(new PhysicalPoint(centerX, centerY));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Place_NonPositiveRadius_ReturnsNull(int radius)
    {
        Check.That(CaptureRegionMath.Place(500, 400, radius, ScreenWidth, ScreenHeight)).IsNull();
    }

    [Fact]
    public void Place_SquareEntirelyOffScreen_ReturnsNull()
    {
        // P is left of the screen by more than a radius: the square has no on-screen part. In
        // production ActivationOfferEngine refuses such points; the placement treats it as failed.
        Check.That(CaptureRegionMath.Place(-400, 1200, Radius, ScreenWidth, ScreenHeight)).IsNull();
    }
}
