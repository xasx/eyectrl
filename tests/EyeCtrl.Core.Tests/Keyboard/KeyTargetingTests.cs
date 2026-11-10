namespace EyeCtrl.Core.Tests.Keyboard;

using System.Collections.Generic;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// The box-first rule in isolation. The geometry is the case the rule exists for: a WIDE key next
/// to a normal one, where a point inside the wide key near its edge is closer to the neighbour's
/// centre. Nearest-centre gives that point to the neighbour; box-first gives it to the wide key.
///
/// <para>Row (y 0–100): <c>wide</c> spans x 0–240 (centre 120), a 4 px gutter, <c>a</c> spans
/// x 244–344 (centre 294). The point (230, 50) is inside <c>wide</c> but 110 px from its centre
/// and 64 px from <c>a</c>'s.</para>
///
/// <para>Outside every box the nearest centre wins only within 8 physical px of that key's box;
/// beyond the band the pick is null (empty space is not a key). The gutter and the gutters at
/// 100 % and 200 % scale are inside the band; a ragged row's indent is outside it.</para>
/// </summary>
public class KeyTargetingTests
{
    private static readonly PhysicalRect WideBox = new(0, 0, 240, 100);
    private static readonly PhysicalRect ABox = new(244, 0, 100, 100);

    private static (Dictionary<string, PhysicalPoint> Centres, Dictionary<string, PhysicalRect> Boxes) Row() =>
        (new() { ["wide"] = WideBox.Center, ["a"] = ABox.Center },
         new() { ["wide"] = WideBox, ["a"] = ABox });

    [Fact]
    public void InsideABox_PicksThatKey_EvenWhenANeighbourCentreIsNearer()
    {
        var (centres, boxes) = Row();
        var gaze = new PhysicalPoint(230, 50);

        // Precondition: the case is real — nearest-centre would pick the neighbour.
        Check.That(gaze.DistanceSquaredTo(centres["a"])).IsStrictlyLessThan(gaze.DistanceSquaredTo(centres["wide"]));

        Check.That(KeyTargeting.PickKey(gaze, centres, boxes)).IsEqualTo("wide");
    }

    [Fact]
    public void WithoutBoxes_IsPlainNearestCentre()
    {
        var (centres, _) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, null)).IsEqualTo("a");
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, new Dictionary<string, PhysicalRect>()))
             .IsEqualTo("a");
    }

    [Theory]
    // In the 4 px gutter between the boxes: nearer a's centre, 2 px from a's box.
    [InlineData(242, 50, "a")]
    // Inside the gutter, hard against a's left edge.
    [InlineData(243, 50, "a")]
    // Exactly 8 px above the wide key's box: the band is inclusive.
    [InlineData(60, -8, "wide")]
    // Within the band above the wide key.
    [InlineData(60, -4, "wide")]
    public void OutsideEveryBox_Within8PixelsOfThatKeysBox_PicksTheNearestCentre(
        double x, double y, string expected)
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(x, y), centres, boxes)).IsEqualTo(expected);
    }

    [Theory]
    // 10 px above the row (a row indent / gap band): outside the 8 px band.
    [InlineData(60, -10)]
    // 10 px above the row, over a.
    [InlineData(300, -10)]
    // Below the row near the wide key's right edge: 9 px left of a's box and 5 px below it, so
    // 10.3 px from a's edge — outside the band even though a is nearest.
    [InlineData(235, 105)]
    // Just past the inclusive boundary.
    [InlineData(60, -8.001)]
    public void OutsideEveryBox_FurtherThan8PixelsFromThatKeysBox_PicksNothing(double x, double y)
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(x, y), centres, boxes)).IsNull();
    }

    [Fact]
    public void FarFromTheGrid_PicksNothing()
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(-5000, 9000), centres, boxes)).IsNull();
    }

    [Fact]
    public void Edges_AreInclusive()
    {
        var (centres, boxes) = Row();
        // wide's right edge (x = 240) is nearer a's centre (54 px) than wide's (120 px).
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(240, 50), centres, boxes)).IsEqualTo("wide");
        // a's left edge belongs to a.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(244, 0), centres, boxes)).IsEqualTo("a");
    }

    /// <summary>
    /// A 4-DIP gap at 100 % (4 physical px) and 200 % (8 physical px, this owner's scale) still
    /// resolves to the nearer key. The 8 px band is exactly the 200 % gutter, so the whole gutter
    /// keeps the nearest-centre rule; a narrower band would open a dead zone between keys.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AdjacentKeyGap_At4Dip_StillPicksTheNearerKey(double scale)
    {
        double gap = 4.0 * scale;
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(100 + gap, 0, 100, 100);
        var centres = new Dictionary<string, PhysicalPoint> { ["left"] = left.Center, ["right"] = right.Center };
        var boxes = new Dictionary<string, PhysicalRect> { ["left"] = left, ["right"] = right };

        // One pixel off the left box: still nearer the left centre.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(101, 50), centres, boxes)).IsEqualTo("left");
        // One pixel off the right box: still nearer the right centre.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(100 + gap - 1, 50), centres, boxes)).IsEqualTo("right");
        // The exact middle is a tie, inside the band: layout order decides, as it always did.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(100 + gap / 2, 50), centres, boxes)).IsEqualTo("left");
    }

    /// <summary>
    /// A gap wider than two 8 px bands has no owner in its middle: 10 px from either box is empty
    /// space, not a reach. Nothing fires until the gaze comes within 8 px of a key.
    /// </summary>
    [Fact]
    public void GapWiderThanTheBand_PicksNothing()
    {
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(120, 0, 100, 100);   // 20 px gutter
        var gap = new PhysicalPoint(110, 50);             // 10 px from both boxes

        Check.That(KeyTargeting.PickKey(gap,
            new Dictionary<string, PhysicalPoint> { ["left"] = left.Center, ["right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["left"] = left, ["right"] = right })).IsNull();
    }

    /// <summary>
    /// Two equal boxes sharing an edge: a point on the edge is inside both and equidistant from
    /// both centres, so the tie-break decides. It is the enumeration (layout) order, the same
    /// strict <c>&lt;</c> the nearest-centre loop always used — deterministic, and pinned both ways
    /// so a "last wins" or ordinal-id tie-break cannot slip in.
    /// </summary>
    [Fact]
    public void SharedEdge_ExactTie_GoesToTheFirstKeyInLayoutOrder()
    {
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(100, 0, 100, 100);
        var edge = new PhysicalPoint(100, 50);

        var lr = KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right });
        var rl = KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["a_right"] = right.Center, ["z_left"] = left.Center },
            new Dictionary<string, PhysicalRect> { ["a_right"] = right, ["z_left"] = left });

        Check.That(lr).IsEqualTo("z_left");
        Check.That(rl).IsEqualTo("a_right");
        // Repeatable for the same input.
        Check.That(KeyTargeting.PickKey(edge,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right })).IsEqualTo(lr);
    }

    /// <summary>
    /// Pass 2 keeps the old loop's tie order too: a gap point exactly between two centres goes to
    /// the key enumerated first, in both orders. The gutter here is 10 px wide, so its midpoint is
    /// 5 px from each box — inside the 8 px band, which is where a key pick exists at all; a gap
    /// wider than the band has no pick in its middle (see <see cref="GapWiderThanTheBand_PicksNothing"/>).
    /// </summary>
    [Fact]
    public void GapPoint_ExactTie_GoesToTheFirstKeyInLayoutOrder()
    {
        var left = new PhysicalRect(0, 0, 100, 100);
        var right = new PhysicalRect(110, 0, 100, 100);
        var gap = new PhysicalPoint(105, 50);   // in the gutter, 55 px from both centres

        Check.That(KeyTargeting.PickKey(gap,
            new Dictionary<string, PhysicalPoint> { ["z_left"] = left.Center, ["a_right"] = right.Center },
            new Dictionary<string, PhysicalRect> { ["z_left"] = left, ["a_right"] = right })).IsEqualTo("z_left");
        Check.That(KeyTargeting.PickKey(gap,
            new Dictionary<string, PhysicalPoint> { ["a_right"] = right.Center, ["z_left"] = left.Center },
            new Dictionary<string, PhysicalRect> { ["a_right"] = right, ["z_left"] = left })).IsEqualTo("a_right");
    }

    /// <summary>
    /// Overlapping boxes: the point is inside both, and the NEAREST centre among them wins — in
    /// both enumeration orders, so it is the distance and not the order deciding. A third key
    /// whose centre is nearer still but whose box does not contain the point must not win.
    /// </summary>
    [Fact]
    public void OverlappingBoxes_ResolveByNearestCentre_AndOutsideKeysDoNotCompete()
    {
        var big = new PhysicalRect(0, 0, 300, 100);      // centre (150, 50)
        var small = new PhysicalRect(200, 0, 100, 100);  // centre (250, 50), inside big
        var outside = new PhysicalRect(305, 0, 20, 100); // centre (315, 50), box excludes the point
        var gaze = new PhysicalPoint(290, 50);           // in big and small; 25 px from outside's centre

        foreach (var order in new[] { new[] { "big", "small", "outside" }, new[] { "outside", "small", "big" } })
        {
            var rects = new Dictionary<string, PhysicalRect> { ["big"] = big, ["small"] = small, ["outside"] = outside };
            var centres = new Dictionary<string, PhysicalPoint>();
            var boxes = new Dictionary<string, PhysicalRect>();
            foreach (var id in order)
            {
                centres[id] = rects[id].Center;
                boxes[id] = rects[id];
            }

            Check.WithCustomMessage($"order {string.Join(",", order)}")
                 .That(KeyTargeting.PickKey(gaze, centres, boxes)).IsEqualTo("small");
        }
    }

    /// <summary>
    /// A key with a centre but no box cannot be validated when boxes are present, so it is
    /// refused — the nearest centre alone is not evidence that the gaze reached a key. Points
    /// inside another key's box still resolve, and a box whose key has no centre is ignored.
    /// </summary>
    [Fact]
    public void AKeyWithoutABox_IsRefused_AndDoesNotChangeTheOtherKeys()
    {
        var (centres, boxes) = Row();
        boxes.Remove("wide");
        // Inside wide's (absent) box the nearest centre is a, but a's own box is 14 px away.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, boxes)).IsNull();
        // A box whose key has no centre is ignored entirely.
        var withGhost = new Dictionary<string, PhysicalRect>(boxes) { ["ghost"] = new(0, 0, 1000, 1000) };
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(245, 50), centres, withGhost)).IsEqualTo("a");
    }

    /// <summary>
    /// The owner's E1 log: two Wort-löschen fires had the mean gaze 139 px below and 148 px above
    /// the key's centre against a 105 px half-height — in the band beside a shorter row's end,
    /// where the old nearest-centre rule handed the space to the outermost key. Row 0 has two keys;
    /// row 1 ends after one. The gaze beside row 1's outer edge is nearer its centre than the keys
    /// above, but 110 px from every box: no pick, so no dwell and no fire.
    /// </summary>
    [Fact]
    public void RaggedRowEnd_BesideTheOutermostKeyOfAShorterRow_PicksNothing()
    {
        var upperLeft = new PhysicalRect(0, 0, 210, 210);
        var upperRight = new PhysicalRect(220, 0, 210, 210);
        var shortRow = new PhysicalRect(0, 220, 210, 210);
        var centres = new Dictionary<string, PhysicalPoint>
        {
            ["upper_left"] = upperLeft.Center,
            ["upper_right"] = upperRight.Center,
            ["short_row"] = shortRow.Center,
        };
        var boxes = new Dictionary<string, PhysicalRect>
        {
            ["upper_left"] = upperLeft,
            ["upper_right"] = upperRight,
            ["short_row"] = shortRow,
        };

        var gaze = new PhysicalPoint(320, 325);   // beside short_row's right edge, in its row band
        // Precondition: nearest-centre would pick a key here — the short row's own.
        Check.That(gaze.DistanceSquaredTo(centres["short_row"]))
             .IsStrictlyLessThan(gaze.DistanceSquaredTo(centres["upper_left"]));
        Check.That(gaze.DistanceSquaredTo(centres["short_row"]))
             .IsStrictlyLessThan(gaze.DistanceSquaredTo(centres["upper_right"]));

        Check.That(KeyTargeting.PickKey(gaze, centres, boxes)).IsNull();
    }

    [Fact]
    public void NoKeys_PicksNothing()
    {
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(1, 1),
            new Dictionary<string, PhysicalPoint>(), new Dictionary<string, PhysicalRect>())).IsNull();
    }
}
