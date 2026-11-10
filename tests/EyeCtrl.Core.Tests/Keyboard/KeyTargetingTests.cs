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
/// <para>Outside every box the nearest centre wins among the keys whose box is within 8 physical px
/// of the gaze; if no box is that close the pick is null (empty space is not a key). The band is
/// measured per box, so the 4-DIP gutters stay alive at both 100 % and 200 % scale; a ragged row's
/// indent is outside it.</para>
/// </summary>
public class KeyTargetingTests
{
    private const double MaxPickGapPixels = 8.0;

    private static readonly PhysicalRect WideBox = new(0, 0, 240, 100);
    private static readonly PhysicalRect ABox = new(244, 0, 100, 100);

    private static (Dictionary<string, PhysicalPoint> Centres, Dictionary<string, PhysicalRect> Boxes) Row() =>
        (new() { ["wide"] = WideBox.Center, ["a"] = ABox.Center },
         new() { ["wide"] = WideBox, ["a"] = ABox });

    /// <summary>Euclidean distance from a point to a box's nearest edge; 0 inside.</summary>
    private static double DistanceToBox(PhysicalPoint p, PhysicalRect box)
    {
        double dx = Math.Max(0, Math.Max(box.X - p.X, p.X - (box.X + box.Width)));
        double dy = Math.Max(0, Math.Max(box.Y - p.Y, p.Y - (box.Y + box.Height)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

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
    // 5 px below wide's box near its right corner, where the nearest CENTRE is a's (80 px vs
    // 128 px) but a's box is 10.3 px away. The band is per box, so wide is in reach and wins; the
    // old "nearest centre, then check ITS box" rule returned null here.
    [InlineData(235, 105, "wide")]
    public void OutsideEveryBox_ABoxWithin8Pixels_PicksTheNearestCentre(double x, double y, string expected)
    {
        var (centres, boxes) = Row();
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(x, y), centres, boxes)).IsEqualTo(expected);
    }

    [Theory]
    // 10 px above the row (a row indent / gap band): outside the 8 px band of both boxes.
    [InlineData(60, -10)]
    // 10 px above the row, over a.
    [InlineData(300, -10)]
    // Below the row near wide's right edge but past every box's band: 9 px below wide's box and
    // 12.7 px from a's corner, even though a is nearest by centre.
    [InlineData(235, 109)]
    // Just past the inclusive boundary.
    [InlineData(60, -8.001)]
    public void OutsideEveryBox_FurtherThan8PixelsFromEveryBox_PicksNothing(double x, double y)
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

    /// <summary>
    /// The reviewer's dead strip: between two rows of different widths, just below the upper row's
    /// end and just outside a wider key on the lower row. (305, 102) is 5.4 px from the lower key's
    /// box and 95 px from the upper row's, yet the nearest CENTRE belongs to the upper row's right
    /// key (154 px vs 163 px). Measuring the band against the nearest centre's box — the first T2
    /// implementation — returned null here while a key box was within the band; the band is per
    /// box, so the lower key wins.
    /// </summary>
    [Fact]
    public void BetweenRowGutter_BesideAWiderKey_PicksTheKeyWhoseBoxIsInReach()
    {
        var upperLeft = new PhysicalRect(0, 0, 100, 100);
        var upperRight = new PhysicalRect(110, 0, 100, 100);
        var wideBelow = new PhysicalRect(0, 104, 300, 100);
        var centres = new Dictionary<string, PhysicalPoint>
        {
            ["upper_left"] = upperLeft.Center,
            ["upper_right"] = upperRight.Center,
            ["wide_below"] = wideBelow.Center,
        };
        var boxes = new Dictionary<string, PhysicalRect>
        {
            ["upper_left"] = upperLeft,
            ["upper_right"] = upperRight,
            ["wide_below"] = wideBelow,
        };
        var gaze = new PhysicalPoint(305, 102);   // 2 px above wide_below, 5 px right of it

        // Preconditions: genuinely empty space; wide_below's box is in reach; the nearest centre
        // belongs to upper_right, whose box is 95 px away — the dead-strip trigger.
        Check.That(boxes.Values.Any(b => b.Contains(gaze.X, gaze.Y))).IsFalse();
        Check.That(DistanceToBox(gaze, wideBelow)).IsLessOrEqualThan(MaxPickGapPixels);
        Check.That(gaze.DistanceSquaredTo(centres["upper_right"]))
             .IsStrictlyLessThan(gaze.DistanceSquaredTo(centres["wide_below"]));
        Check.That(DistanceToBox(gaze, upperRight)).IsStrictlyGreaterThan(MaxPickGapPixels);

        Check.That(KeyTargeting.PickKey(gaze, centres, boxes)).IsEqualTo("wide_below");
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
    /// A key with a centre but no box cannot be validated when boxes are present, so it is skipped
    /// — the nearest centre alone is not evidence that the gaze reached a key. Points inside
    /// another key's box still resolve, and a box whose key has no centre is ignored.
    /// </summary>
    [Fact]
    public void AKeyWithoutABox_IsSkipped_AndDoesNotChangeTheOtherKeys()
    {
        var (centres, boxes) = Row();
        boxes.Remove("wide");
        // Inside wide's (absent) box the nearest centre is a, but a's own box is 14 px away.
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(230, 50), centres, boxes)).IsNull();

        // The unboxed key is nearest by centre (10 px vs 184 px) while every real box is > 130 px
        // away: trusting the unboxed key would pick "wide", so this point isolates the skip.
        var trustProbe = new PhysicalPoint(110, 50);
        Check.That(trustProbe.DistanceSquaredTo(centres["wide"]))
             .IsStrictlyLessThan(trustProbe.DistanceSquaredTo(centres["a"]));
        Check.That(DistanceToBox(trustProbe, boxes["a"])).IsStrictlyGreaterThan(MaxPickGapPixels);
        Check.That(KeyTargeting.PickKey(trustProbe, centres, boxes)).IsNull();

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

    /// <summary>
    /// A shipped-like letters grid at 200 % scale — 56 x 52 DIP keys, 4 DIP gutters, staggered rows,
    /// a wider shift key and a wide space bar that outlives the shorter row above it — sampled every
    /// 2 physical px. Every point within 8 px of SOME box must pick a key, and every point further
    /// out must pick nothing. The expected key is the nearest centre among the boxes IN REACH; the
    /// number of points whose nearest centre overall belongs to an out-of-reach box (the dead-strip
    /// trigger) is asserted non-zero, so the test can tell the per-box band from the first T2 rule
    /// (nearest centre, then check that key's box).
    /// </summary>
    [Fact]
    public void ShippedLikeGrid_At200Percent_EveryPointWithin8PixelsOfABoxPicksAKey()
    {
        var (order, centres, boxes) = ShippedLikeGrid(2.0);
        double maxX = boxes.Values.Max(b => b.X + b.Width);
        double maxY = boxes.Values.Max(b => b.Y + b.Height);
        var wrong = new List<string>();
        int inReachPoints = 0, outOfReachPoints = 0, nearestCentreOutOfReach = 0;

        for (double y = -12; y <= maxY + 12; y += 2)
        {
            for (double x = -12; x <= maxX + 12; x += 2)
            {
                var p = new PhysicalPoint(x, y);
                var containing = order.Where(id => boxes[id].Contains(p.X, p.Y)).ToList();
                string? expected = null;
                double expectedDist = double.MaxValue;
                double minGap = double.MaxValue;
                foreach (var box in boxes.Values)
                    minGap = Math.Min(minGap, DistanceToBox(p, box));

                if (containing.Count > 0)
                {
                    foreach (var id in order)
                    {
                        if (!containing.Contains(id))
                            continue;
                        double d = p.DistanceSquaredTo(centres[id]);
                        if (d < expectedDist)
                        {
                            expectedDist = d;
                            expected = id;
                        }
                    }
                }
                else
                {
                    string? nearestOverall = null;
                    double overallDist = double.MaxValue;
                    foreach (var id in order)
                    {
                        double d = p.DistanceSquaredTo(centres[id]);
                        if (d < overallDist)
                        {
                            overallDist = d;
                            nearestOverall = id;
                        }
                        if (DistanceToBox(p, boxes[id]) <= MaxPickGapPixels && d < expectedDist)
                        {
                            expectedDist = d;
                            expected = id;
                        }
                    }
                    if (minGap <= MaxPickGapPixels
                        && DistanceToBox(p, boxes[nearestOverall!]) > MaxPickGapPixels)
                    {
                        nearestCentreOutOfReach++;
                    }
                }

                if (minGap <= MaxPickGapPixels) inReachPoints++; else outOfReachPoints++;
                var picked = KeyTargeting.PickKey(p, centres, boxes);
                if (picked != expected)
                    wrong.Add($"({x:F0},{y:F0}) minGap {minGap:F1} expected {expected ?? "null"} got {picked ?? "null"}");
            }
        }

        Check.WithCustomMessage($"{wrong.Count} of {inReachPoints + outOfReachPoints} grid points picked the "
                + "wrong key: " + string.Join("; ", wrong.Take(20)))
             .That(wrong).IsEmpty();
        Check.WithCustomMessage("The probe grid had no point within the band — vacuous.")
             .That(inReachPoints).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage("The probe grid had no point beyond the band — vacuous.")
             .That(outOfReachPoints).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage("No point had an out-of-reach nearest centre while another box was in "
                + "reach — the grid cannot tell the per-box band from the old rule.")
             .That(nearestCentreOutOfReach).IsStrictlyGreaterThan(0);
    }

    /// <summary>
    /// A letters-page-like grid in layout order: three staggered QWERTZ rows of 56 x 52 DIP keys
    /// with 4 DIP gutters, a wider shift key, and a bottom row whose space bar extends past the
    /// shorter row above it. Scaled to physical pixels.
    /// </summary>
    private static (List<string> Order, Dictionary<string, PhysicalPoint> Centres, Dictionary<string, PhysicalRect> Boxes)
        ShippedLikeGrid(double scale)
    {
        var order = new List<string>();
        var boxes = new Dictionary<string, PhysicalRect>();
        var centres = new Dictionary<string, PhysicalPoint>();

        void Add(string id, double x, double y, double w, double h)
        {
            var box = new PhysicalRect(x * scale, y * scale, w * scale, h * scale);
            order.Add(id);
            boxes[id] = box;
            centres[id] = box.Center;
        }

        for (int c = 0; c < 10; c++)
            Add($"r0c{c}", c * 60, 0, 56, 52);
        for (int c = 0; c < 9; c++)
            Add($"r1c{c}", 30 + c * 60, 56, 56, 52);
        Add("r2shift", 0, 112, 84, 52);
        for (int c = 0; c < 6; c++)
            Add($"r2c{c}", 88 + c * 60, 112, 56, 52);
        Add("r3ctrl", 0, 168, 72, 52);
        Add("r3win", 76, 168, 56, 52);
        Add("r3alt", 136, 168, 56, 52);
        Add("r3space", 196, 168, 300, 52);

        return (order, centres, boxes);
    }

    [Fact]
    public void NoKeys_PicksNothing()
    {
        Check.That(KeyTargeting.PickKey(new PhysicalPoint(1, 1),
            new Dictionary<string, PhysicalPoint>(), new Dictionary<string, PhysicalRect>())).IsNull();
    }
}
