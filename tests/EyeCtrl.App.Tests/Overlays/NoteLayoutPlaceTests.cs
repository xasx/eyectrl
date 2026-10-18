namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;
using static EyeCtrl.App.Overlays.NoteLayout;

/// <summary>
/// The pure <see cref="NoteLayout.Place"/> table (P3 D2), run over anchors at every edge and
/// corner of a 1920×1200 DIP work area at scale 2.0 (physical anchors in, DIP everything out).
/// Every case checks the side, the window inside the inset area, the tail tip 6 DIP short of
/// the anchor, the control cells inside the window, avoid-rect handling, and the exclude/
/// clamp fallback contract.
/// </summary>
public class NoteLayoutPlaceTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1200);
    private static readonly Size Card = new(
        EyeCtrl.Core.LayoutConstants.NoteWindowWidth, EyeCtrl.Core.LayoutConstants.NoteWindowHeight);
    private const double Scale = 2.0;
    private const double Margin = NoteLayout.Constants.EdgeMarginDip;
    private static readonly Rect Inset = new(Margin, Margin, 1920 - 2 * Margin, 1200 - 2 * Margin);

    private static PhysicalPoint Phys(double dipX, double dipY) => new(dipX * Scale, dipY * Scale);

    private static NotePlacement Place(double dipX, double dipY, NoteSide preferred = NoteSide.Right,
        IReadOnlyList<Rect>? avoid = null, NoteSide? exclude = null, Rect? workArea = null) =>
        NoteLayout.Place(Phys(dipX, dipY), preferred, Card, workArea ?? WorkArea, avoid ?? Array.Empty<Rect>(),
            Scale, exclude)!;

    // ── the side table: centre, both horizontal edges, both vertical edges, all four corners ──

    [Theory]
    [InlineData(960, 600, NoteSide.Right)]   // centre: the preferred Right fits
    [InlineData(1800, 600, NoteSide.Left)]   // right edge: Right cannot fit, cycle falls to Left
    [InlineData(960, 1100, NoteSide.Above)]  // bottom edge: card+tail only fits above
    [InlineData(960, 100, NoteSide.Below)]   // top edge: only below
    [InlineData(200, 100, NoteSide.Below)]   // near the top-left corner, still a clean fit
    public void TheSideCycle_FindsTheFirstFittingSide_AtEveryEdge(double x, double y, NoteSide expected)
    {
        var placement = Place(x, y);

        Check.That(placement.Side).IsEqualTo(expected);
        WindowIsInsideTheInsetArea(placement);
        TailIsSixDipShortOfTheAnchor(placement, x, y);
        CellsAreInsideTheWindow(placement);
    }

    [Fact]
    public void NoFitAtTheTopRightCorner_ClampsThePreferredInsideTheInset()
    {
        // 1800,100: Right/Left/Below/Above all miss (the card is 280 wide against a 1866-wide
        // inset at that corner) — the clamp fallback keeps the card on screen.
        var placement = Place(1800, 100, preferred: NoteSide.Below);

        Check.That(placement.Side).IsEqualTo(NoteSide.Below);
        WindowIsInsideTheInsetArea(placement);
        Check.That(placement.Window.Left).IsEqualTo(Inset.Right - placement.Window.Width);
        CellsAreInsideTheWindow(placement);
    }

    [Fact]
    public void NoFitAtTheBottomRightCorner_ClampsAndKeepsTheTailTowardTheAnchor()
    {
        var placement = Place(1800, 1100, preferred: NoteSide.Above);

        Check.That(placement.Side).IsEqualTo(NoteSide.Above);
        WindowIsInsideTheInsetArea(placement);
        Check.That(placement.Window.Left).IsEqualTo(Inset.Right - placement.Window.Width);
        // The anchor is not under the clamped card, so the tail bends to it — its tip stays
        // OUTSIDE the card (the nearest window point toward the anchor), never on the card.
        Check.That(placement.TailVisible).IsTrue();
        var tip = placement.TailTip!.Value;
        var card = placement.Card;
        Check.WithCustomMessage("the clamped tail's tip stays outside the card")
            .That(!card.Contains(tip)).IsTrue();
        CellsAreInsideTheWindow(placement);
    }

    [Fact]
    public void NoFitAtTheBottomLeftCorner_ClampsToTheInsetOrigin()
    {
        var placement = Place(100, 1100, preferred: NoteSide.Above);

        Check.That(placement.Side).IsEqualTo(NoteSide.Above);
        WindowIsInsideTheInsetArea(placement);
        Check.That(placement.Window.Left).IsEqualTo(Inset.Left);
    }

    [Fact]
    public void AnAnchorUnderTheClampedCard_HidesTheTail()
    {
        // A tiny work area: nothing fits, the clamp pulls the window over the anchor — the tail
        // would point at a spot under the card, so it hides (P3 D2.3).
        var placement = Place(150, 150, preferred: NoteSide.Right, workArea: new Rect(0, 0, 300, 300));

        Check.That(placement.TailVisible).IsFalse();
        Check.That(placement.TailTip).IsNull();
        WindowIsInsideTheInsetArea(placement);
    }

    // ── avoid rects ──

    [Fact]
    public void AnAvoidRectOverThePreferredSide_PushesTheCardToTheNextFittingSide()
    {
        // The keyboard band covers the Right slot; the placement moves to Left.
        var avoid = new[] { new Rect(960 + 6, 600 - 132, 310, 264) };
        var placement = Place(960, 600, avoid: avoid);

        Check.That(placement.Side).IsEqualTo(NoteSide.Left);
        WindowIsInsideTheInsetArea(placement);
    }

    [Fact]
    public void AvoidRects_AreHonoured_OverEveryCandidate()
    {
        // One rect covers the Right slot exactly, a second covers Below's lower band: the cycle
        // must skip Right and land on Left, whose window intersects neither.
        var avoid = new[] { new Rect(966, 468, 310, 264), new Rect(820, 740, 280, 190) };
        var placement = Place(960, 600, avoid: avoid);

        Check.That(placement.Side).IsEqualTo(NoteSide.Left);
        Check.That(placement.Window.IntersectsWith(avoid[0])).IsFalse();
        Check.That(placement.Window.IntersectsWith(avoid[1])).IsFalse();
    }

    [Fact]
    public void AnAvoidRectOverEverything_ClampsThePreferredInstead()
    {
        var avoid = new[] { new Rect(0, 0, 1920, 1200) };
        var placement = Place(960, 600, preferred: NoteSide.Right, avoid: avoid);

        // No side fits; without exclude the clamp fallback still returns a placement.
        Check.That(placement.Side).IsEqualTo(NoteSide.Right);
        WindowIsInsideTheInsetArea(placement);
    }

    // ── exclude ──

    [Fact]
    public void Exclude_IsSkipped_ButThePreferredCanStillWin()
    {
        var placement = Place(960, 600, preferred: NoteSide.Right, exclude: NoteSide.Left);

        Check.That(placement.Side).IsEqualTo(NoteSide.Right);
    }

    [Fact]
    public void Exclude_PushesToTheNextSide_InTheFixedCycle()
    {
        var placement = Place(960, 600, preferred: NoteSide.Right, exclude: NoteSide.Right);

        Check.That(placement.Side).IsEqualTo(NoteSide.Left);
    }

    [Fact]
    public void Exclude_WhenNoOtherSideFits_ReturnsNull()
    {
        // The top-right corner: only the clamp fallback exists, and exclude forbids returning it.
        var placement = NoteLayout.Place(
            Phys(1800, 100), NoteSide.Below, Card, WorkArea, Array.Empty<Rect>(), Scale,
            exclude: NoteSide.Below);

        Check.That(placement).IsNull();
    }

    // ── geometry invariants shared by every case ──

    private static void WindowIsInsideTheInsetArea(NotePlacement placement)
    {
        Check.WithCustomMessage($"window {placement.Window} must lie inside the inset {Inset}")
            .That(placement.Window.Left >= Inset.Left && placement.Window.Top >= Inset.Top
                && placement.Window.Right <= Inset.Right && placement.Window.Bottom <= Inset.Bottom)
            .IsTrue();
    }

    private static void TailIsSixDipShortOfTheAnchor(NotePlacement placement, double ax, double ay)
    {
        Check.That(placement.TailVisible).IsTrue();
        var tip = placement.TailTip!.Value;
        switch (placement.Side)
        {
            case NoteSide.Right:
                Check.WithCustomMessage("Right's tip stops 6 DIP short of the anchor").That(tip.X).IsEqualTo(ax + 6.0);
                break;
            case NoteSide.Left:
                Check.WithCustomMessage("Left's tip stops 6 DIP short of the anchor").That(tip.X).IsEqualTo(ax - 6.0);
                break;
            case NoteSide.Below:
                Check.WithCustomMessage("Below's tip stops 6 DIP short of the anchor").That(tip.Y).IsEqualTo(ay + 6.0);
                break;
            default:
                Check.WithCustomMessage("Above's tip stops 6 DIP short of the anchor").That(tip.Y).IsEqualTo(ay - 6.0);
                break;
        }
    }

    private static void CellsAreInsideTheWindow(NotePlacement placement)
    {
        // ButtonCenter returns window-RELATIVE points; the window's own rect in its own frame
        // is (0, 0, width, height).
        var windowLocal = new Rect(0, 0, placement.Window.Width, placement.Window.Height);
        for (int slot = 0; slot < 3; slot++)
        {
            var (x, y) = ButtonCenter(placement.Side, slot);
            Check.WithCustomMessage($"cell {slot} at ({x}, {y}) must be inside the window {windowLocal}")
                .That(windowLocal.Contains(new Point(x, y))).IsTrue();
        }
    }

    // ── the tail triangle and the column side ──

    [Fact]
    public void TheTailTriangle_SitsBetweenTheCardEdgeAndTheTip()
    {
        var placement = Place(960, 600); // Right
        var triangle = TailTriangle(placement);

        Check.That(triangle).IsNotNull();
        var drawn = triangle!; // NFluent's HasSize cannot narrow — this is the defensive accessor (fixup-01 n1)
        Check.That(drawn).HasSize(3);
        // Tip first, then the two base points on the card's left edge.
        Check.That(drawn[0].X).IsCloseTo(placement.TailTip!.Value.X - placement.Window.X, 1e-9);
        Check.That(drawn[1].X).IsCloseTo(placement.Card.Left - placement.Window.X, 1e-9);
        Check.That(drawn[2].X).IsCloseTo(placement.Card.Left - placement.Window.X, 1e-9);
        // The base straddles the tip's Y and stays inside the card.
        Check.That(drawn[1].Y).IsStrictlyLessThan(drawn[0].Y);
        Check.That(drawn[2].Y).IsStrictlyGreaterThan(drawn[0].Y);
    }

    [Fact]
    public void TheHiddenTail_TrianglesToNull()
    {
        var placement = Place(150, 150, workArea: new Rect(0, 0, 300, 300));

        Check.That(TailTriangle(placement)).IsNull();
    }

    [Fact]
    public void TheColumn_SitsOnTheCardEdgeAwayFromTheTail_PerSide()
    {
        // Right: tail left → column on the far right edge (today's default).
        var (rx, _) = ButtonCenter(NoteSide.Right, 0);
        Check.That(rx).IsCloseTo(
            EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius
            + Constants.TailLengthDip, 1e-9);

        // Left: tail right → column on the far left edge.
        var (lx, _) = ButtonCenter(NoteSide.Left, 0);
        Check.That(lx).IsCloseTo(
            EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius, 1e-9);

        // Below: tail at the top strip → column on the right edge, shifted DOWN by the tail zone
        // (the tail strip takes the window's top; the card's X origin does not move).
        var (bx, by) = ButtonCenter(NoteSide.Below, 0);
        Check.That(bx).IsCloseTo(
            EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius, 1e-9);
        Check.That(by).IsCloseTo(EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius
            + Constants.TailLengthDip, 1e-9);

        // Above: tail at the bottom → column on the right edge, no shift.
        var (ax2, ay2) = ButtonCenter(NoteSide.Above, 0);
        Check.That(ax2).IsCloseTo(
            EyeCtrl.Core.LayoutConstants.NoteWindowWidth
            - EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            - EyeCtrl.Core.LayoutConstants.NoteButtonRadius, 1e-9);
        Check.That(ay2).IsCloseTo(EyeCtrl.Core.LayoutConstants.NoteTitleBarHeight
            + EyeCtrl.Core.LayoutConstants.NoteButtonPadding
            + EyeCtrl.Core.LayoutConstants.NoteButtonRadius, 1e-9);
    }
}
