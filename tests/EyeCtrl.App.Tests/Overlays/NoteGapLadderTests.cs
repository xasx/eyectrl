namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;

/// <summary>
/// B (sitting 12): the note gives way to the taller F17 keyboard in TWO steps, in this order.
/// <para>
/// 1. The screen-edge margin yields first: from the A2 design's 80 DIP (<see
/// cref="NoteLayout.Constants.EdgeMarginDip"/>) down to the hit cell's own half-width, 56 DIP
/// (<see cref="NoteLayout.Constants.EdgeMarginFloorDip"/>), and only as far as the placement
/// needs. A docked card's controls sit next to the keyboard while its far edge faces the screen
/// edge, so the keyboard gap protects more than the design's 24 DIP edge extra.
/// </para>
/// <para>
/// 2. Only then does the keyboard gap shrink, 24 down to 0. When nothing clears even at gap 0,
/// the least-overlap fallback takes the candidate with the smallest possible overlap.
/// </para>
/// <para>
/// These are PURE layout tests: one full-width keyboard rect against a work-area edge (a
/// "band"), and a two-rect corridor for the cases where the pre-B fallback could not reach the
/// minimum. Nothing is shown; no DPI is faked (the layout is scale-free in DIP).
/// </para>
/// </summary>
public class NoteGapLadderTests
{
    /// <summary>The owner's work area in DIP at 2.0.</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    private const double InsetBottom = 1152 - NoteLayout.Constants.EdgeMarginDip; // 1072

    /// <summary>The full 80 DIP design inset (the pre-B rule).</summary>
    private static readonly Rect Inset = new(
        NoteLayout.Constants.EdgeMarginDip, NoteLayout.Constants.EdgeMarginDip,
        WorkArea.Width - (2 * NoteLayout.Constants.EdgeMarginDip),
        WorkArea.Height - (2 * NoteLayout.Constants.EdgeMarginDip));

    /// <summary>A DIP card: the layout is scale-free, so any width the band holds is fine.</summary>
    private static readonly Size Card = new(400, 200);

    /// <summary>The keyboard on the TOP edge with a height that leaves <paramref
    /// name="band80Dip"/> DIP between its bottom and the 80 DIP inset's bottom edge.</summary>
    private static Rect TopKeyboardLeaving(double band80Dip) =>
        new(0, 0, 1920, InsetBottom - band80Dip);

    /// <summary>Places the synthetic card under a top-docked keyboard, dead centre of the band.</summary>
    private static NotePlacement Place(double anchorY, IReadOnlyList<Rect> avoid) =>
        NoteLayout.Place(new PhysicalPoint(960 * 2, anchorY * 2), NoteSide.Right, Card, WorkArea,
            avoid, 2.0, exclude: null, allowDisplaced: true)!;

    /// <summary>The signed clearance between a rect and the keyboard: positive is a real gap,
    /// negative is how deep the overlap goes (the production measure's test mirror).</summary>
    private static double Clearance(Rect window, Rect rect) => Math.Max(
        Math.Max(window.Left - rect.Right, rect.Left - window.Right),
        Math.Max(window.Top - rect.Bottom, rect.Top - window.Bottom));

    /// <summary>The screen-edge margin the placement spent beyond the 80 DIP design inset.</summary>
    private static double MarginYield(Rect window) => Math.Max(0, Math.Max(
        Math.Max(Inset.Left - window.Left, window.Right - Inset.Right),
        Math.Max(Inset.Top - window.Top, window.Bottom - Inset.Bottom)));

    [Fact]
    public void TheFullGap_HoldsUnchanged_WhenTheBandAlreadyFitsIt()
    {
        // A band of 24 + h: today's placement, byte for byte - full gap, no margin spent.
        var keyboard = TopKeyboardLeaving(NoteLayout.Constants.AvoidGapDip + Card.Height);
        var placement = Place(500, new[] { keyboard });

        Check.WithCustomMessage($"the card {placement.Window} must keep the full gap")
            .That(Clearance(placement.Window, keyboard)).IsCloseTo(NoteLayout.Constants.AvoidGapDip, 1e-6);
        Check.WithCustomMessage("no screen-edge margin may be spent when the full gap fits")
            .That(MarginYield(placement.Window)).IsEqualTo(0.0);
        Check.WithCustomMessage("and it stays inside the 80 DIP design inset")
            .That(Inset.Contains(placement.Window)).IsTrue();
    }

    [Fact]
    public void TheMarginYieldsFirst_ToKeepTheFullGap()
    {
        // A band of h + 10: the full gap needs 14 DIP more than the 80 DIP inset allows. The
        // margin yields exactly those 14 - the gap is the part worth keeping, so it stays 24.
        var keyboard = TopKeyboardLeaving(Card.Height + 10);
        var placement = Place(500, new[] { keyboard });

        Check.WithCustomMessage($"the card {placement.Window} must keep the FULL gap, not 10")
            .That(Clearance(placement.Window, keyboard)).IsCloseTo(NoteLayout.Constants.AvoidGapDip, 1e-6);
        Check.WithCustomMessage("the screen-edge margin yields exactly what the gap needs (14 DIP)")
            .That(MarginYield(placement.Window)).IsCloseTo(14, 1e-6);
        Check.WithCustomMessage("and the card never leaves the work area")
            .That(WorkArea.Contains(placement.Window)).IsTrue();
    }

    [Fact]
    public void TheGapShrinks_OnlyAfterTheMarginHasReachedItsFloor()
    {
        // A band of h - 10 at the 80 DIP inset. Even the 56 DIP floor leaves only h + 14, so the
        // margin is spent to its floor and the GAP (not the margin) gives the remaining 10.
        var keyboard = TopKeyboardLeaving(Card.Height - 10);
        var placement = Place(500, new[] { keyboard });

        Check.WithCustomMessage($"the card {placement.Window} must clear with the gap the floor leaves (14)")
            .That(Clearance(placement.Window, keyboard)).IsCloseTo(14, 1e-6);
        Check.WithCustomMessage("the screen-edge margin is spent to its 24 DIP floor")
            .That(MarginYield(placement.Window)).IsCloseTo(NoteLayout.Constants.EdgeMarginYieldDip, 1e-6);
        Check.WithCustomMessage("the card's far edge stops at the cell's own half-width (56 DIP)")
            .That(WorkArea.Bottom - placement.Window.Bottom)
            .IsCloseTo(NoteLayout.Constants.EdgeMarginFloorDip, 1e-6);
    }

    [Fact]
    public void TheOverlapIsTheMinimum_WhenEvenGapZeroCannotFit()
    {
        // A band of h - 30 at the 80 DIP inset: h - 6 at the 56 DIP floor. Nothing clears, and
        // the card overlaps the keyboard by exactly the 6 DIP the floor cannot absorb - the
        // smallest overlap the geometry allows.
        var keyboard = TopKeyboardLeaving(Card.Height - 30);
        var placement = Place(500, new[] { keyboard });

        Check.WithCustomMessage($"the card {placement.Window} must overlap the keyboard by exactly 6")
            .That(Clearance(placement.Window, keyboard)).IsCloseTo(-6, 1e-6);
        Check.WithCustomMessage("with the screen-edge margin at its floor")
            .That(MarginYield(placement.Window)).IsCloseTo(NoteLayout.Constants.EdgeMarginYieldDip, 1e-6);
        Check.WithCustomMessage("and the card's controls still inside the work area")
            .That(WorkArea.Contains(placement.Card)).IsTrue();
    }

    [Fact]
    public void TheLargestGapWins_NotTheNearestClearCandidate()
    {
        // The band at the 56 DIP margin floor is h + 23.9, so the full 24 is impossible even
        // with the margin fully spent; the OWN window (the anchor sits h/2 under the keyboard's
        // edge) only touches the keyboard (gap 0, margin 0.1) while the band below keeps 23.9.
        // The ladder's rule is "largest gap first, tie-breaks after": the note must step to
        // 23.9 with the margin spent, not stay on the keyboard's edge because it is nearer.
        var keyboard = new Rect(0, 0, 1920,
            (1152 - NoteLayout.Constants.EdgeMarginFloorDip) - (Card.Height + 23.9));
        var placement = Place(keyboard.Bottom + (Card.Height / 2), new[] { keyboard });

        Check.WithCustomMessage($"the card {placement.Window} must take the band's 23.9 DIP gap")
            .That(Clearance(placement.Window, keyboard)).IsCloseTo(23.9, 1e-6);
        Check.WithCustomMessage("and it must lie at the band's far end, not on the keyboard's edge")
            .That(placement.Window.Top).IsCloseTo(keyboard.Bottom + 23.9, 1e-6);
        Check.WithCustomMessage("the margin is the part spent for it")
            .That(MarginYield(placement.Window)).IsCloseTo(NoteLayout.Constants.EdgeMarginYieldDip, 1e-6);
    }

    [Fact]
    public void InACorridor_ExactFitClearsAtGapZero()
    {
        // Two avoid rects bound a corridor exactly as tall as the card. The pre-B fallback could
        // not reach the touching spot (its 24 DIP windows clamp onto one of the rects), so it
        // overlapped by 20; the ladder's gap-0 family clears with NO overlap.
        var banner = new Rect(0, 0, 1920, 100);
        var keyboard = new Rect(0, 900, 1920, 252);
        var card = new Size(400, 800);
        var placement = NoteLayout.Place(new PhysicalPoint(960 * 2, 1000 * 2), NoteSide.Right,
            card, WorkArea, new[] { banner, keyboard }, 2.0, exclude: null, allowDisplaced: true)!;

        Check.WithCustomMessage($"the card {placement.Window} must clear both rects")
            .That(Clearance(placement.Window, banner)).IsGreaterOrEqualThan(-1e-6);
        Check.WithCustomMessage($"the card {placement.Window} must clear the keyboard")
            .That(Clearance(placement.Window, keyboard)).IsGreaterOrEqualThan(-1e-6);
        Check.WithCustomMessage("the corridor fit is a ZERO gap, not an overlap")
            .That(Math.Min(Clearance(placement.Window, banner), Clearance(placement.Window, keyboard)))
            .IsCloseTo(0, 1e-6);
        Check.WithCustomMessage("and no screen-edge margin was needed")
            .That(MarginYield(placement.Window)).IsEqualTo(0.0);
    }

    [Fact]
    public void InACorridor_TheOverlapIsTheMinimum()
    {
        // The same corridor, 5 DIP too short for the card: everything must overlap something.
        // The minimum is the shortfall SPLIT between the two rects - 2.5 DIP each - and the
        // pre-B fallback (24 DIP windows only) took 20 because both clamped onto one rect. The
        // fallback's fine gap sweep is what finds the split.
        var banner = new Rect(0, 0, 1920, 100);
        var keyboard = new Rect(0, 900, 1920, 252);
        var card = new Size(400, 805);
        var placement = NoteLayout.Place(new PhysicalPoint(960 * 2, 1000 * 2), NoteSide.Right,
            card, WorkArea, new[] { banner, keyboard }, 2.0, exclude: null, allowDisplaced: true)!;

        double worst = Math.Min(Clearance(placement.Window, banner), Clearance(placement.Window, keyboard));
        Check.WithCustomMessage($"the card {placement.Window} must split the 5 DIP shortfall (2.5 each)")
            .That(worst).IsCloseTo(-2.5, 1e-6);
        Check.WithCustomMessage("and stay inside the work area")
            .That(WorkArea.Contains(placement.Window)).IsTrue();
    }

    [Fact]
    public void TheYieldedMargin_NeverSlidesTheCardUnderAnAvoidRect()
    {
        // The coordinator's guard: the margin may not let the card slide under a toolbar. A
        // banner rect stands for the toolbar; the corridor between it and a bottom-docked
        // keyboard is 24 DIP short of the card plus two gaps, so the margins would love to
        // spend themselves - but a margin yield into the banner is an AVOID overlap and the
        // gap shrinks instead, symmetrically (10 DIP each side).
        var banner = new Rect(0, 0, 1920, 140);
        var keyboard = new Rect(0, 900, 1920, 252);
        var card = new Size(400, 740);
        var placement = NoteLayout.Place(new PhysicalPoint(960 * 2, 1000 * 2), NoteSide.Right,
            card, WorkArea, new[] { banner, keyboard }, 2.0, exclude: null, allowDisplaced: true)!;

        Check.WithCustomMessage($"the card {placement.Window} must not reach under the banner {banner}")
            .That(placement.Window.Top).IsGreaterOrEqualThan(banner.Bottom - 1e-6);
        Check.WithCustomMessage($"the card {placement.Window} must clear with the 10 DIP the corridor leaves")
            .That(Math.Min(Clearance(placement.Window, banner), Clearance(placement.Window, keyboard)))
            .IsCloseTo(10, 1e-6);
        Check.WithCustomMessage("and it spends no screen-edge margin")
            .That(MarginYield(placement.Window)).IsEqualTo(0.0);
    }

    [Fact]
    public void AtTheExactFullGap_TheLeastMarginBreaksTheTie()
    {
        // Fixup-01 F4: the owner's Fn page at 28 DIP. The band at the 56 DIP floor fits the
        // drawn 200 DIP card AND the full 24 gap (233.5 >= 224), so the band's Below family
        // keeps exactly 24 with a 14.5 DIP margin, while the anchor's own clamped window keeps
        // 24 too but spends 20. The ladder must spend the LEAST margin (14.5). Before the
        // exact-24 return, the family's bisected 24*(1-2^-32) lost to the own window's exact 24,
        // so the margin rule never ran and M7 (least margin disabled) survived every test.
        var keyboard = new Rect(139, 0, 1642, 862.5);
        var card = NoteLayout.EditCardSize(28, 0, WorkArea.Height);
        var placement = NoteLayout.Place(new PhysicalPoint(100 * 2, 1000 * 2), NoteSide.Right,
            card, WorkArea, new[] { keyboard }, 2.0, exclude: null, allowDisplaced: true,
            topGrowthDip: NoteLayout.TagProtrusionDip(28))!;
        var drawn = NoteLayout.WithTagClearance(placement, 28);
        var (gap, margin) = NoteLayout.GapAndMarginYield(drawn.Window, new[] { keyboard }, WorkArea);

        Check.WithCustomMessage($"the gap must be exactly the full 24 (got {gap})")
            .That(gap).IsCloseTo(NoteLayout.Constants.AvoidGapDip, 1e-6);
        Check.WithCustomMessage($"the least margin (14.5) must win at the full gap (got {margin})")
            .That(margin).IsCloseTo(14.5, 1e-6);
    }

    [Fact]
    public void TheExposedMeasure_MatchesThePlacement()
    {
        // The manager's NOTE-PLACE line reads GapAndMarginYield; it must answer the placement's
        // own geometry (the signed gap and the margin spent), or the log would lie.
        var keyboard = TopKeyboardLeaving(Card.Height + 10);
        var placement = Place(500, new[] { keyboard });
        var (gap, margin) = NoteLayout.GapAndMarginYield(placement.Window, new[] { keyboard }, WorkArea);

        Check.That(gap).IsCloseTo(Clearance(placement.Window, keyboard), 1e-6);
        Check.That(margin).IsCloseTo(MarginYield(placement.Window), 1e-6);
    }
}
